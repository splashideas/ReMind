# Operations setup

This is the operator runbook for GitHub Actions, Azure connections, and the few Azure steps that cannot be Terraform. It covers the stack that deploys today and the redesign in [`architecture-design.md`](architecture-design.md).

**IaC rule:** every Azure resource in architecture-design §10 (App Service, SQL, storage, queues, Event Grid, Front Door, WAF, Key Vault, VNet, private endpoint, DNS zone, identities, role assignments, Application Insights, Log Analytics, Azure Maps account, Static Web Apps or storage static website) is created in `infra/terraform` with the `hashicorp/azurerm` provider `~> 4.0`. Do not create those resources in the portal. Portal, `az`, and vendor-console steps below are only for bootstrap that Terraform cannot own, or for values Terraform must receive as sensitive inputs.

Secret names below match the workflows. If a workflow and this doc disagree, the workflow file wins and this doc should be updated in the same change.

The current-stack narrative also lives in the repository [`README.md`](../README.md) (state storage, service principal, plan/apply). This page is the checklist operators follow, including gaps that README does not name.

## What must stay manual

| Step | Why it is not Terraform | Where to do it |
| --- | --- | --- |
| Terraform state storage account and containers | The backend account cannot be created by the configuration that stores its state there | Azure CLI, once per subscription |
| GitHub Actions service principal or OIDC app registration in the **subscription** Entra tenant | Chicken-and-egg: Actions must authenticate before Terraform runs | `az ad sp` / Entra portal, then GitHub secrets |
| GitHub Actions secrets, variables, and environment protection | GitHub settings, not Azure resources | Repository Settings |
| Entra **External ID** tenant | Separate customer-identity tenant from the SQL server's Entra tenant; tenant creation is not a reliable Terraform resource | Microsoft Entra admin center |
| External ID customer user flow and app association | The AzureAD provider does not manage External ID customer user flows or their application associations ([issue #1798](https://github.com/hashicorp/terraform-provider-azuread/issues/1798)) | Microsoft Entra admin center; follow §3.3 |
| Social IdP apps (Google, Apple, Facebook) | Created in those vendors' consoles | Vendor consoles |
| External ID social-provider configuration | The AzureAD Terraform provider has no resource for configuring CIAM social providers | Microsoft Entra admin center; follow §3.4 |
| Custom-domain registrar records | The domain registrar is outside Azure | Registrar; Front Door custom-domain resource is still Terraform |
| First environment inventory of `dbo.DataPoints` | A read against existing databases, not a resource | SQL query from an identity that can already connect |
| Self-hosted runner registration, only if the Azure-side migration job cannot reach private SQL | GitHub runner install is outside Azure | Last resort; prefer an Azure job in the VNet |

Everything else, including app registrations **inside** the External ID tenant once a pipeline identity exists there, Admin app role, redirect URIs, Key Vault secret *containers*, Defender for Storage, and firewall rules, is an implementation task for `infra/terraform`. If a provider resource cannot express one of those, the pull request must say why and link the fallback to this page. Do not leave a silent portal click.

## 1. Current-stack bootstrap

Do this before the first `deploy-infrastructure` run. State and app resources must share one subscription because one `AZURE_CREDENTIALS` value is used for both the backend and the provider.

### 1.1 State storage (one-time, outside Terraform)

```bash
LOCATION=eastus
RG_NAME=rg-remind-tfstate
SA_NAME=stremindtfstate   # globally unique, 3-24 lowercase alphanumeric
STATE_CONTAINER=tfstate
PLAN_CONTAINER=tfplans

az group create --name "$RG_NAME" --location "$LOCATION"

az storage account create \
  --name "$SA_NAME" \
  --resource-group "$RG_NAME" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --min-tls-version TLS1_2 \
  --allow-blob-public-access false

az storage container-rm create \
  --storage-account "$SA_NAME" \
  --resource-group "$RG_NAME" \
  --name "$STATE_CONTAINER"
az storage container-rm create \
  --storage-account "$SA_NAME" \
  --resource-group "$RG_NAME" \
  --name "$PLAN_CONTAINER"
```

Add the 7-day lifecycle rule on `${PLAN_CONTAINER}/plans/` from the README so cancelled plan blobs do not live forever. Do not commit storage account keys. CI uses Azure AD (`use_azuread_auth = true`).

Local init, optional:

```bash
cd infra/terraform
cp backend.hcl.example backend.hcl
# edit names to match the account above; backend.hcl is gitignored
az login
terraform init -backend-config=backend.hcl
```

### 1.2 Deployment identity

The identity needs, on that same subscription:

- **Contributor** (create app resources)
- **Role Based Access Control Administrator** (Terraform creates `azurerm_role_assignment`; Contributor cannot)
- **Storage Blob Data Contributor** on the state account (plan/state blobs; no account keys in CI)

```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
SP_NAME=sp-remind-github-actions

az ad sp create-for-rbac \
  --name "$SP_NAME" \
  --role Contributor \
  --scopes "/subscriptions/$SUBSCRIPTION_ID" \
  --sdk-auth

SP_CLIENT_ID="<appId from the JSON>"
SP_OBJECT_ID=$(az ad sp show --id "$SP_CLIENT_ID" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"

SA_ID=$(az storage account show --name "$SA_NAME" --resource-group "$RG_NAME" --query id -o tsv)
az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$SA_ID"
```

Save the `--sdk-auth` JSON. It is the `AZURE_CREDENTIALS` secret. Do not commit it, and do not paste it into an issue or pull request.

`--sdk-auth` is the shape `deploy-infrastructure` parses today (`clientId`, `clientSecret`, `subscriptionId`, `tenantId`). Prefer replacing that long-lived secret with OIDC (§1.4) once the federated credential exists. Until the workflows are switched, the client secret is still required.

Tighten RBAC Administrator from subscription scope to the app resource group after the first apply has created `TF_VAR_RESOURCE_GROUP_NAME`.

### 1.3 GitHub secrets and variables

Repository → **Settings → Secrets and variables → Actions**.

**Secrets**

| Name | Used by | Value |
| --- | --- | --- |
| `AZURE_CREDENTIALS` | `deploy-infrastructure`, `deploy-solution` | Full `--sdk-auth` JSON. `azure/login@v2` consumes it. `deploy-infrastructure` also exports `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`, `ARM_SUBSCRIPTION_ID`, `ARM_TENANT_ID` from the same JSON. |
| `TF_VAR_SQL_ADMIN_PASSWORD` | `deploy-infrastructure` | SQL admin password. Sensitive Terraform input. Not an app setting. |
| `TF_VAR_SQL_CONNECTION_SETTING_VALUE` | `deploy-infrastructure` | Current Function App `SqlConnectionString`. Prefer a Key Vault reference over a raw password. The redesign removes plaintext SQL connection strings from app settings; do not add a new copy for `ReMind.Api`. |

**Variables**

| Name | Example | Used by |
| --- | --- | --- |
| `TF_BACKEND_RESOURCE_GROUP` | `rg-remind-tfstate` | `deploy-infrastructure` init |
| `TF_BACKEND_STORAGE_ACCOUNT` | `stremindtfstate` | init and plan blob upload |
| `TF_BACKEND_STATE_CONTAINER` | `tfstate` | state blob container |
| `TF_BACKEND_STATE_KEY` | `remind/infrastructure.tfstate` | state blob name |
| `TF_BACKEND_PLAN_CONTAINER` | `tfplans` | saved plan container |
| `TF_VAR_NAME_PREFIX` | `remind` | resource name prefix |
| `TF_VAR_RESOURCE_GROUP_NAME` | `rg-remind` | app resource group |
| `TF_VAR_LOCATION` | `eastus` | region; empty falls back to the Terraform default |
| `TF_VAR_SQL_ADMIN_LOGIN` | `sqladmin` | SQL admin login |
| `TF_VAR_SQL_AAD_ADMIN_LOGIN` | `admin@contoso.com` | Entra admin on the **SQL server's** tenant, not the External ID tenant |
| `TF_VAR_SQL_AAD_ADMIN_OBJECT_ID` | object id GUID | that admin's object id |
| `AZURE_WEBAPP_NAME` | `remind-frontend` | `deploy-solution`. Must match `${TF_VAR_NAME_PREFIX}-frontend` from `azurerm_linux_web_app.frontend` |
| `AZURE_FUNCTIONAPP_NAME` | `remind-function` | `deploy-solution`. Must match `${TF_VAR_NAME_PREFIX}-function` |

There is no publish-profile secret. `deploy-solution` signs in with `azure/login` and `AZURE_CREDENTIALS`, then deploys by app name.

### 1.4 Prefer OIDC over the client secret

Do this when you are ready to stop storing `clientSecret` in GitHub. It is still a manual Entra + GitHub step; the Azure resources stay in Terraform.

1. On the app registration from §1.2, add a federated credential:
   - Entity: branch, or environment if you use §1.5
   - Organization: `splashideas`
   - Repository: `ReMind`
   - Branch subject: `repo:splashideas/ReMind:ref:refs/heads/main`
   - Environment subject (if used): `repo:splashideas/ReMind:environment:production`
   - Audience: `api://AzureADTokenExchange`
2. Add repository variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (not secrets).
3. Change `azure/login` to `client-id` / `tenant-id` / `subscription-id` and give those jobs `permissions: id-token: write`.
4. Remove `AZURE_CREDENTIALS` only after a plan/apply succeeds with OIDC. Terraform still needs `ARM_CLIENT_ID`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID` and `ARM_USE_OIDC=true` instead of `ARM_CLIENT_SECRET`.

`deploy-infrastructure` already sets `id-token: write`, but it still logs in with the client-secret JSON. Do not delete the secret until that workflow is switched.

### 1.5 GitHub Environment protection

`deploy-infrastructure` plan uses the `production` GitHub Environment so it can read the social-provider secrets in §3. Create it before enabling External ID. Required reviewers are recommended:

1. Settings → Environments → create `production`.
2. Add required reviewers if production changes require approval.
3. Add the three social-provider client secrets listed in §3 only after the vendor apps exist. Keep `AZURE_CREDENTIALS` and current SQL secrets in their existing scope unless deliberately migrating those workflows too.

Do not put production secrets in the repository Actions secrets if you move them to the environment. Keep plan logs from printing SQL passwords (Terraform sensitive variables already redact).

### 1.6 Run the workflows

Both deploy workflows are **workflow_dispatch** only. They do not run on push.

1. **Actions → terraform-validate** runs on pull requests (fmt, `init -backend=false`, validate). No Azure login.
2. **Actions → deploy-infrastructure → Run workflow** on `main`. Plan writes `plans/remind-<run_id>.tfplan` to the plan container; apply downloads that blob and deletes it after success. Confirm the plan does not destroy the resource group or SQL server before approving apply.
3. **Actions → deploy-solution → Run workflow** after the web app and function app exist. Publishes `ReMind.Frontend` and `ReMind.Functions`.
`dotnet-build` needs no secrets. It restores, builds, and tests `ReMind.sln` on .NET 10 and installs Playwright Chromium.

### 1.7 SQL reachability and retired schema job

The `deploy-database` workflow has been removed. `ReMind.Database` remains in git for reference only and must not be used to deploy schema; EF Core migrations in `ReMind.Data` are the only future schema apply path. `infra/terraform/main.tf` does not open the SQL firewall.

- Do **not** enable "Allow Azure services" (`0.0.0.0`) or add a firewall rule to run a schema deployment from a GitHub-hosted runner.
- Inventory must use an existing authorized operator path. After the private endpoint issue, production SQL public network access is disabled and migration apply uses a VNet-connected job (§8).

## 2. Inventory before any EF deployment

Mandatory before the first EF migration, from architecture-design §3. This is a read, not a portal resource.

For each shared, dev, and prod server that ever ran the Functions app or `deploy-database`:

```sql
SELECT COUNT(*) AS row_count FROM dbo.DataPoints;
SELECT TOP (5) DataPointId FROM dbo.DataPoints;
SELECT DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo'
  AND TABLE_NAME = 'DataPoints'
  AND COLUMN_NAME = 'Location';
```

Record the server name, count, and whether `Location` is still `NVARCHAR`. If any environment has rows, stop. Do not apply the EF schema. Write a data-disposition plan (NVARCHAR location mapping, missing `UserId`/`Title`, rollback) and get it reviewed. Greenfield is allowed only when every environment is confirmed empty or never deployed.

The removed workflow no longer reads `AZURE_SQL_CONNECTION_STRING`; it is not a required GitHub secret. If it remains configured in GitHub, remove it without displaying its value. Check workflow and variable targets so a forgotten deployment path cannot recreate `dbo.DataPoints`.

## 3. Entra External ID

Customer sign-in is a **new** External ID tenant. Do not reuse the tenant that administers Azure SQL (`TF_VAR_SQL_AAD_ADMIN_*`).

### 3.1 Create the tenant (manual)

1. Microsoft Entra admin center → **Create a tenant** → **External ID** / customer tenant.
2. Record the External ID tenant id and primary domain in the infrastructure PR. Confirm this directory is different from the SQL server's Entra tenant (`TF_VAR_SQL_AAD_ADMIN_*`).
3. In the subscription tenant, confirm the GitHub deployment app registration (`AZURE_CREDENTIALS.clientId`) supports accounts in any organizational directory. If permitted, make it multi-tenant; do not grant it unrelated Graph permissions.
4. Sign in as an External ID tenant administrator and create the deployment application's service principal in that directory:

   ```bash
   az login --tenant "<EXTERNAL_ID_TENANT_ID>" --allow-no-subscriptions
   az ad sp create --id "<GITHUB_DEPLOYMENT_CLIENT_ID>"
   az ad sp show --id "<GITHUB_DEPLOYMENT_CLIENT_ID>" --query id -o tsv
   ```

   In the External ID tenant, open **Roles and administrators → Application Administrator → Add assignments**, select that service principal, and assign the role. Verify its tenant-local service principal exists and the deployment identity can sign in to that tenant before enabling the Terraform resources.

5. If tenant policy prevents making the deployment app multi-tenant, creating its service principal, or assigning the directory role, stop after tenant creation. Do not set `TF_VAR_EXTERNAL_ID_ENABLED=true` and do not claim app registrations are managed by Terraform. Use the portal fallback in §3.2 and record the blocking policy and completed clicks in the PR.

### 3.2 App registrations (Terraform after the tenant exists)

The opt-in Terraform resources in `infra/terraform` target the External ID tenant via an `azuread.external_id` provider alias. Keep `TF_VAR_EXTERNAL_ID_ENABLED` unset/false until the tenant exists and the deployment principal can manage its app registrations. Configure these GitHub repository variables before enabling it:

- `TF_VAR_EXTERNAL_ID_ENABLED=true`
- `TF_VAR_EXTERNAL_ID_TENANT_ID=<External ID tenant GUID>`
- `TF_VAR_EXTERNAL_ID_KEY_VAULT_NAME=<globally unique Key Vault name>`
- `TF_VAR_EXTERNAL_ID_WEB_REDIRECT_URIS=["https://<deployed-spa-origin>/"]` (valid JSON list; local `http://localhost:5173/` is registered automatically)

The `production` GitHub Environment must exist because the plan job reads the social-provider secrets from it. Add `TF_VAR_google_client_secret`, `TF_VAR_apple_client_secret`, and `TF_VAR_facebook_client_secret` as environment secrets. Do not use repository secrets or put their values in Terraform variables files. Terraform marks these inputs sensitive and writes them to the RBAC-protected Key Vault.

The configuration registers:

- **API app**: `api://<API-client-id>` identifier URI, `access_as_user` delegated scope, and v2 access tokens. The `api_audience` output is the API client id, the `aud` value to validate; `api_scope` is the URI clients request. No API client secret is created.
- **SPA app**: local and deployed redirect URIs on the SPA platform. Implicit token issuance is disabled; use authorization code + PKCE.
- **Mobile app**: public client with `remind-mobile://auth`, matching the `remind-mobile` scheme in `src/ReMind.Mobile/app.json`. Implicit token issuance is disabled; use authorization code + PKCE. No client secret is created.
- **Admin app role**: app role value `Admin`, emitted in the `roles` claim when assigned. Assign it only to operator users in Entra; there is no self-service role API.

Do not proceed with the first enabled apply until a disposable registration has confirmed the aliased provider writes into the External ID tenant. The AzureAD provider has an open report that aliased application registrations can be created in the default tenant instead ([issue #1772](https://github.com/hashicorp/terraform-provider-azuread/issues/1772)); its broader CIAM support request is also open ([issue #1263](https://github.com/hashicorp/terraform-provider-azuread/issues/1263)). Verify the created app in the External ID portal, not only the `azuread_client_config` output. If the deployment identity cannot be granted access, or the provider targets the wrong tenant, stop: do not apply the three registrations from Terraform. Use the portal fallback below and record the limitation and actual tenant id/domain in the PR.

**Portal fallback when app registration automation is unavailable:** in the External ID tenant, create an API app and expose `api://<API-client-id>/access_as_user`; create a single-page app with `http://localhost:5173/` and the deployed origin as redirect URIs; create a public-client app with `remind-mobile://auth`; disable implicit grant; then add the API delegated scope to both clients. On the API app, add a user-assigned app role with display name and value `Admin`, then assign only operator accounts under Enterprise applications → Users and groups. Do not create client secrets for either public client or add a self-service role grant. Record all client ids and the API audience, but no secrets, in the PR.

Non-secret ids (tenant id, client ids, audience) are Terraform outputs or non-secret app settings. The Key Vault is created in the subscription tenant and contains the three social-provider secrets. Terraform state and plan artifacts are access-controlled and may contain provider-managed secret values; plan text redacts them. The SPA and mobile apps are public clients and must not have secrets.

**Blocking follow-up for issue #22:** the current React scaffold has no sign-in flow, so its PKCE and token acceptance checks have not been run. Keep issue #22 open and do not use a closing reference in the PR until the check below has been completed and its result recorded:

1. Sign in a non-production user through the SPA's authorization-code + PKCE flow, request `api_scope`, and confirm locally that the access token's `aud` equals `api_audience`.
2. Assign the `Admin` app role to a separate operator test user in **Enterprise applications → ReMind.Api → Users and groups**. Confirm that user's token contains `roles: ["Admin"]` and the ordinary user's token has no `Admin` role.

Do not paste or log access tokens.

### 3.3 Customer sign-up and sign-in user flow (manual)

The AzureAD provider does not currently manage External ID customer user flows or associate them with applications. After the app registrations exist and the desired identity providers are configured:

1. In the External ID tenant, open **External Identities → User flows → New user flow** and create a **Sign up and sign in** flow.
2. Configure the approved sign-in methods and sign-up attributes for ReMind.
3. Open the flow's **Applications** page, add both `ReMind.Web` and `ReMind.Mobile`, and save.
4. Use **Run user flow** to verify that the flow opens and returns to the configured client redirect URI.

Record the flow name and associated client applications in the infrastructure PR. Do not record tokens or secrets.

### 3.4 Social identity providers (manual vendor consoles)

Google Cloud Console, Apple Developer, and Meta for Developers each need an OAuth client whose redirect URI is the External ID tenant's identity-provider callback (shown in the Entra External ID IdP blade). Those consoles are outside Azure.

After you have the client id and secret:

1. Add the secrets as `TF_VAR_google_client_secret`, `TF_VAR_apple_client_secret`, and `TF_VAR_facebook_client_secret` in GitHub Settings → Environments → `production`. Terraform writes them to Key Vault; do not commit them or print them in logs.
2. In Microsoft Entra admin center, open External Identities → All identity providers. Add only Google, Apple, and Facebook, using each vendor's client id and secret. The identity-provider callback URL shown there must be configured as the OAuth redirect URI in the matching vendor console.
3. Complete a test sign-in for each enabled provider after the customer flow is available. Apple credentials expire; document the rotation date in the issue that lands the IdP, update the GitHub environment secret, and re-run the Terraform plan/apply.

Enable only Google, Apple, and Facebook unless the design changes.

## 4. DNS and custom domains

Front Door endpoints, custom domains, managed certificates, and origin host names are Terraform (`azurerm_cdn_frontdoor_*`).

Manual registrar steps, after Terraform prints the validation token:

1. Create the TXT (or CNAME) record Front Door requires for domain validation.
2. Point the public API hostname and the media hostname at the Front Door endpoint. Do not point DNS at the App Service default hostname; direct origin access is locked to Front Door.
3. Wait for the managed certificate. Re-run Terraform if the resource stays in a pending validation state that a second apply must refresh.
4. Confirm `GET /healthz/live` through the Front Door hostname returns success and does not require a JWT. Confirm the App Service hostname rejects requests that lack both `AzureFrontDoor.Backend` and the matching `X-Azure-FDID`.

## 5. Subject pseudonym key

`ErasedSubjectHashes` uses HMAC-SHA-256 with `SubjectPseudonymKey` from Key Vault. The key must not be in git, SQL, or logs.

Preferred: Terraform creates an Azure Key Vault **oct** key (or a secret whose value Terraform generates and stores only in Key Vault and state). State is already sensitive; restrict state blob access to the deployment identity.

If the pinned `azurerm` provider cannot create an oct key:

1. Locally: `openssl rand -base64 64` (at least 256 bits).
2. Put the value in GitHub secret `TF_VAR_subject_pseudonym_key_b64`.
3. Terraform `azurerm_key_vault_secret` writes version 1. Delete the shell history. Do not echo the secret in the workflow log.

Rotation **adds** a version. Do not disable or delete a version that `ErasedSubjectHashes.KeyVersion` still references. A referenced version stays readable for the life of those rows. That rule is application behavior; the manual mistake to avoid is disabling the old version in the portal after rotation.

## 6. Defender for Storage and Azure Maps

**Defender:** enable malware scanning with Terraform (`azurerm_security_center_storage_defender` or the current `azurerm` 4.x equivalent) on the media storage account, plus the Event Grid subscription the design requires. If the subscription has no Defender plan and the provider cannot attach one, record a one-time portal enablement in the infra PR and then import the resource into state. Do not leave scanning as an untracked portal toggle.

**Azure Maps:** create the account in Terraform. If the Maps client requires a key, Terraform stores it in Key Vault and the API reads it with managed identity. Do not put the key in `appsettings` as plaintext, and do not call Maps from the browser with that key. The SPA calls `GET /api/datapoints/search/place` only.

## 7. Redesign host settings (IaC, not portal)

When the foundation Terraform issue lands, these are code, not manual configuration:

- Service plan SKU **P1v3** for `ReMind.Api` (today's plan is `B1`; do not change it until that issue, and do not delete the current frontend app until cutover).
- SQL database SKU **S0** or higher (today's database is `Basic`).
- System- or user-assigned identity on the API and worker Function App.
- App settings are Key Vault references or identity-based (SQL AAD), never a copied connection string.
- Anonymous `GET /healthz/live` for Front Door probes.
- Separate static host for `ReMind.Web` (Static Web Apps, or storage static website if Static Web Apps is unavailable). Document the choice in that PR. Front Door origin for the SPA is Terraform.
- Worker Function App keeps thumbnail, outbox, and janitor triggers only. Remove HTTP triggers at cutover, not before.
- Blob anonymous access stays disabled. Blob CORS lists SPA origins and upload methods only, in Terraform.

Outputs should include the web app name, function app name, and API hostname so `AZURE_WEBAPP_NAME` / `AZURE_FUNCTIONAPP_NAME` do not have to be hand-copied forever. Until those outputs exist, set the variables from the names in §1.3.

## 8. Private SQL and migration apply

After the VNet issue:

- SQL public network access disabled.
- Private endpoint, private DNS zone, **VNet link**, and **private DNS zone group** are all Terraform. A private endpoint without the zone group resolves to the public name and the app fails closed.
- App Service VNet integration is Terraform.
- GitHub-hosted runners cannot apply EF migrations to that server. Build the migration bundle in GitHub Actions, then apply it from an Azure job that is in the VNet (Container Apps job or equivalent), invoked with the same OIDC identity. A self-hosted runner in the VNet is the fallback and is the only extra manual install (runner registration token from GitHub, not stored in the repo).

Do not re-open the SQL firewall to the internet so a hosted runner can migrate.

## 9. Local developer setup

No production secrets on a laptop.

- .NET 10 SDK (CI uses `10.0.x`).
- Node.js 22 for `src/ReMind.Web` once that project exists.
- Azure Functions Core Tools 4, only while Functions workers or the current save path still run.
- `az login` for local Terraform. Export `ARM_*` or use Azure CLI auth. Copy `backend.hcl.example` to `backend.hcl` (gitignored).
- External ID tenant ids for local SPA auth come from Terraform outputs or a gitignored `.env`. Do not commit `.env`.
- `gh auth login` with issue write permission only if you run `.github/architecture-issues/seed-issues.sh` locally.

## 10. Verification

- [ ] `deploy-infrastructure` plan succeeds and apply is reviewed before it runs.
- [ ] `AZURE_WEBAPP_NAME` and `AZURE_FUNCTIONAPP_NAME` match Terraform names, and `deploy-solution` deploys after `azure/login`.
- [ ] `deploy-database` is removed; schema deployment uses only EF Core migrations in `ReMind.Data`.
- [ ] No storage account key, SQL password, Maps key, social client secret, or subject pseudonym key is in git.
- [ ] External ID tenant id is not the SQL server's tenant id.
- [ ] "Allow Azure services" is off.
- [ ] After Front Door: public hostname works, App Service direct hostname does not.
- [ ] After private SQL: API connects, GitHub-hosted runner cannot.

## Related

- Architecture contract: [`architecture-design.md`](architecture-design.md) §10–§13
- Issue catalog and how to seed or assign work: [`.github/architecture-issues/README.md`](../.github/architecture-issues/README.md)

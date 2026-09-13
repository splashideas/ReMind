# ReMind

ReMind is structured as a .NET 10 solution for Azure deployment:

- `src/ReMind.Frontend`: ASP.NET Core frontend intended for Azure App Service.
- `src/ReMind.Functions`: Azure Functions app that receives save events and stores data points in Azure SQL.
- `database/ReMind.Database`: .NET database project containing Azure SQL schema deployment scripts.
- `infra/terraform`: Terraform IaC for Azure App Service, Function App, and Azure SQL resources.
- `.github/workflows`: GitHub Actions workflows for build/validate and deploy.

## Infrastructure deploy (Terraform + Azure Storage)

The `deploy-infrastructure` workflow splits **plan** and **apply**:

1. **Plan** initializes Terraform against an Azure Storage remote backend, writes a plan file (`tfplan`), and uploads that plan blob to Azure Storage.
2. **Apply** re-initializes against the same remote backend, downloads the plan blob, and runs `terraform apply` with that file.

Remote **Terraform state** (`*.tfstate`) lives in one blob container. Saved **plan files** live in a second container (or the same account) so plan and apply can share artifacts across jobs.

### 1. Create the Terraform state storage account (one-time)

Run these against the single Azure subscription that hosts both Terraform state and the deployed app resources. This workflow uses one `AZURE_CREDENTIALS` subscription for the provider and backend, so state and app must share that subscription. Use names that match the GitHub variables you will set below.

```bash
# Adjust names/location as needed
LOCATION=eastus
RG_NAME=rg-remind-tfstate
SA_NAME=stremindtfstate   # must be globally unique, 3-24 lowercase alphanumeric
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

az storage container create --account-name "$SA_NAME" --name "$STATE_CONTAINER" --auth-mode login
az storage container create --account-name "$SA_NAME" --name "$PLAN_CONTAINER" --auth-mode login

# Auto-delete saved plan blobs under the plans/ prefix after 7 days so cleanup
# does not depend solely on the apply job (e.g. cancelled workflows).
# prefixMatch is account-scoped and includes the container name.
cat > /tmp/tfplan-lifecycle.json <<EOF
{
  "rules": [
    {
      "enabled": true,
      "name": "delete-expired-tfplans",
      "type": "Lifecycle",
      "definition": {
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["${PLAN_CONTAINER}/plans/"]
        },
        "actions": {
          "baseBlob": {
            "delete": {
              "daysAfterModificationGreaterThan": 7
            }
          }
        }
      }
    }
  ]
}
EOF

az storage account management-policy create \
  --account-name "$SA_NAME" \
  --resource-group "$RG_NAME" \
  --policy @/tmp/tfplan-lifecycle.json
```

### 2. Create a service principal for GitHub Actions

Prefer a credential scoped to that same subscription (Contributor or a tighter custom role) **and** data-plane access on the state account. `subscriptionId` in `AZURE_CREDENTIALS` is the subscription Terraform uses for both backend access and resource deployment.

```bash
# Must be the same subscription used for state storage above.
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
SP_NAME=sp-remind-github-actions

# Creates an app registration + client secret and grants subscription Contributor.
# Save the JSON output; it becomes the AZURE_CREDENTIALS GitHub secret.
az ad sp create-for-rbac \
  --name "$SP_NAME" \
  --role Contributor \
  --scopes "/subscriptions/$SUBSCRIPTION_ID" \
  --sdk-auth
```

Grant the same principal permission to read/write state and plan blobs (Azure AD auth, no storage account keys in CI):

```bash
# Set this from clientId in the saved AZURE_CREDENTIALS JSON.
SP_CLIENT_ID="<appId>"
SP_OBJECT_ID=$(az ad sp show --id "$SP_CLIENT_ID" --query id -o tsv)
SA_ID=$(az storage account show --name "$SA_NAME" --resource-group "$RG_NAME" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$SA_ID"
```

### 3. Configure GitHub Actions secrets and variables

In the repository: **Settings → Secrets and variables → Actions**.

**Secret `AZURE_CREDENTIALS`** — paste the full JSON from `az ad sp create-for-rbac --sdk-auth`. Shape:

```json
{
  "clientId": "<appId>",
  "clientSecret": "<password>",
  "subscriptionId": "<subscription-guid>",
  "tenantId": "<tenant-guid>",
  "activeDirectoryEndpointUrl": "https://login.microsoftonline.com",
  "resourceManagerEndpointUrl": "https://management.azure.com/",
  "...": "other sdk-auth fields"
}
```

`azure/login@v2` signs the Azure CLI in with these credentials; it does not create `ARM_*` variables. Configure Terraform authentication separately by exporting the service-principal fields as `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`, `ARM_SUBSCRIPTION_ID`, and `ARM_TENANT_ID`, or use Terraform's native OIDC authentication.

**Additional secrets** (Terraform sensitive inputs used by plan/apply):

| Secret | Purpose |
| --- | --- |
| `TF_VAR_SQL_ADMIN_PASSWORD` | SQL admin password |
| `TF_VAR_SQL_CONNECTION_SETTING_VALUE` | Function app `SqlConnectionString` value (prefer a Key Vault reference) |

**Repository variables**:

| Variable | Example | Purpose |
| --- | --- | --- |
| `TF_BACKEND_RESOURCE_GROUP` | `rg-remind-tfstate` | State storage resource group |
| `TF_BACKEND_STORAGE_ACCOUNT` | `stremindtfstate` | State/plan storage account name |
| `TF_BACKEND_STATE_CONTAINER` | `tfstate` | Container for `terraform.tfstate` |
| `TF_BACKEND_STATE_KEY` | `remind/infrastructure.tfstate` | Blob key for state |
| `TF_BACKEND_PLAN_CONTAINER` | `tfplans` | Container for saved plan files |
| `TF_VAR_NAME_PREFIX` | `remind` | Terraform `name_prefix` |
| `TF_VAR_RESOURCE_GROUP_NAME` | `rg-remind` | App resource group name |
| `TF_VAR_LOCATION` | `eastus` | Azure region |
| `TF_VAR_SQL_ADMIN_LOGIN` | `sqladmin` | SQL admin login |
| `TF_VAR_SQL_AAD_ADMIN_LOGIN` | `admin@contoso.com` | Entra SQL admin name |
| `TF_VAR_SQL_AAD_ADMIN_OBJECT_ID` | `<guid>` | Entra SQL admin object id |

### 4. Local Terraform init (optional)

```bash
cd infra/terraform
cp backend.hcl.example backend.hcl
# edit backend.hcl, then:
az login
terraform init -backend-config=backend.hcl
```

Do not commit `backend.hcl` if it contains environment-specific values you want to keep out of git.

### 5. Run the workflow

GitHub → **Actions → deploy-infrastructure → Run workflow**.

- Plan uploads `plans/remind-<run_id>.tfplan` to `TF_BACKEND_PLAN_CONTAINER`.
- Apply downloads that blob, applies it, then deletes the plan blob.
- A storage lifecycle policy also deletes blobs under the `plans/` prefix **7 days** after last modification, so a cancelled run cannot leave a plan blob indefinitely.
- Ongoing resource state remains in `TF_BACKEND_STATE_CONTAINER` / `TF_BACKEND_STATE_KEY`.

### Security notes

- Prefer OpenID Connect (federated credentials) instead of a long-lived client secret when you can; keep `id-token: write` and switch `azure/login` to `client-id` / `tenant-id` / `subscription-id` with a federated credential on the app registration.
- Plan files can contain sensitive values — keep the plan container private, delete plans after apply (the workflow does this), and rely on the **7-day** lifecycle rule on the `plans/` prefix as a backstop when apply never runs.
- Lock down the state account with Azure AD only (`use_azuread_auth = true`), disable shared-key access when your org policy allows it, and restrict network access if runners have stable egress.

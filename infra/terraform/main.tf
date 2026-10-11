terraform {
  required_version = ">= 1.7.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }

  # Partial backend config. Values are supplied at init time via
  # -backend-config flags (CI) or a local backend.hcl (see backend.hcl.example).
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

provider "azuread" {
  alias     = "external_id"
  tenant_id = var.external_id_enabled ? var.external_id_tenant_id : null
}

data "azurerm_client_config" "current" {}

data "azuread_client_config" "external_id" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id
}

resource "azuread_application" "api" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  display_name                   = "ReMind.Api"
  sign_in_audience               = "AzureADMyOrg"
  owners                         = [data.azuread_client_config.external_id[0].object_id]
  fallback_public_client_enabled = false

  web {
    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Allow the application to access ReMind.Api on behalf of the signed-in user."
      admin_consent_display_name = "Access ReMind.Api"
      enabled                    = true
      id                         = "2d3f6dc1-8932-4e9f-b8ac-3ca24aad6e2b"
      type                       = "User"
      user_consent_description   = "Allow this application to access ReMind.Api on your behalf."
      user_consent_display_name  = "Access ReMind.Api"
      value                      = "access_as_user"
    }

  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Administrators can perform operator actions in ReMind."
    display_name         = "Admin"
    enabled              = true
    id                   = "86b5f6e1-c5d7-42d7-9aa6-781825862fe9"
    value                = "Admin"
  }

  lifecycle {
    ignore_changes = [identifier_uris]
  }
}

resource "azuread_application_identifier_uri" "api" {
  count          = var.external_id_enabled ? 1 : 0
  provider       = azuread.external_id
  application_id = azuread_application.api[0].id
  identifier_uri = "api://${azuread_application.api[0].client_id}"
}

resource "azuread_application" "spa" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  display_name                   = "ReMind.Web"
  sign_in_audience               = "AzureADMyOrg"
  owners                         = [data.azuread_client_config.external_id[0].object_id]
  fallback_public_client_enabled = false

  web {
    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  single_page_application {
    redirect_uris = concat(
      ["http://localhost:5173/"],
      var.external_id_web_redirect_uris,
    )
  }
}

resource "azuread_application_api_access" "spa" {
  count          = var.external_id_enabled ? 1 : 0
  provider       = azuread.external_id
  application_id = azuread_application.spa[0].id
  api_client_id  = azuread_application.api[0].client_id
  scope_ids      = ["2d3f6dc1-8932-4e9f-b8ac-3ca24aad6e2b"]
}

resource "azuread_application" "mobile" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  display_name                   = "ReMind.Mobile"
  sign_in_audience               = "AzureADMyOrg"
  owners                         = [data.azuread_client_config.external_id[0].object_id]
  fallback_public_client_enabled = false

  web {
    implicit_grant {
      access_token_issuance_enabled = false
      id_token_issuance_enabled     = false
    }
  }

  public_client {
    redirect_uris = [var.external_id_mobile_redirect_uri]
  }
}

resource "azuread_application_api_access" "mobile" {
  count          = var.external_id_enabled ? 1 : 0
  provider       = azuread.external_id
  application_id = azuread_application.mobile[0].id
  api_client_id  = azuread_application.api[0].client_id
  scope_ids      = ["2d3f6dc1-8932-4e9f-b8ac-3ca24aad6e2b"]
}

resource "azuread_service_principal" "api" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  client_id                    = azuread_application.api[0].client_id
  app_role_assignment_required = false
  owners                       = [data.azuread_client_config.external_id[0].object_id]
}

resource "azuread_service_principal" "spa" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  client_id = azuread_application.spa[0].client_id
  owners    = [data.azuread_client_config.external_id[0].object_id]
}

resource "azuread_service_principal" "mobile" {
  count    = var.external_id_enabled ? 1 : 0
  provider = azuread.external_id

  client_id = azuread_application.mobile[0].client_id
  owners    = [data.azuread_client_config.external_id[0].object_id]
}

resource "azurerm_key_vault" "external_id" {
  count               = var.external_id_enabled ? 1 : 0
  name                = var.external_id_key_vault_name
  location            = azurerm_resource_group.remind.location
  resource_group_name = azurerm_resource_group.remind.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true
}

resource "azurerm_role_assignment" "external_id_key_vault_secrets_officer" {
  count                = var.external_id_enabled ? 1 : 0
  scope                = azurerm_key_vault.external_id[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_resource_group" "remind" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_service_plan" "app_plan" {
  name                = "${var.name_prefix}-plan"
  location            = azurerm_resource_group.remind.location
  resource_group_name = azurerm_resource_group.remind.name
  os_type             = "Linux"
  sku_name            = "B1"
}

resource "azurerm_linux_web_app" "frontend" {
  name                = "${var.name_prefix}-frontend"
  location            = azurerm_resource_group.remind.location
  resource_group_name = azurerm_resource_group.remind.name
  service_plan_id     = azurerm_service_plan.app_plan.id

  site_config {
    application_stack {
      dotnet_version = var.webapp_dotnet_version
    }
  }

  app_settings = {
    SaveDataPointFunctionUrl = "https://${azurerm_linux_function_app.function.default_hostname}/api/datapoints"
    SaveDataPointFunctionKey = data.azurerm_function_app_host_keys.function.default_function_key
  }
}

resource "azurerm_storage_account" "function_storage" {
  name                     = lower(replace("${var.name_prefix}funcsa", "-", ""))
  resource_group_name      = azurerm_resource_group.remind.name
  location                 = azurerm_resource_group.remind.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
}

resource "azurerm_linux_function_app" "function" {
  name                          = "${var.name_prefix}-function"
  location                      = azurerm_resource_group.remind.location
  resource_group_name           = azurerm_resource_group.remind.name
  service_plan_id               = azurerm_service_plan.app_plan.id
  storage_account_name          = azurerm_storage_account.function_storage.name
  storage_uses_managed_identity = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version = var.function_dotnet_version
    }
  }

  app_settings = {
    SqlConnectionString = var.sql_connection_setting_value
  }
}

data "azurerm_function_app_host_keys" "function" {
  name                = azurerm_linux_function_app.function.name
  resource_group_name = azurerm_resource_group.remind.name

  depends_on = [azurerm_role_assignment.function_blob]
}

resource "azurerm_role_assignment" "function_blob" {
  scope                = azurerm_storage_account.function_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_linux_function_app.function.identity[0].principal_id
}

resource "azurerm_role_assignment" "function_queue" {
  scope                = azurerm_storage_account.function_storage.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_linux_function_app.function.identity[0].principal_id
}

resource "azurerm_mssql_server" "sql" {
  name                         = "${var.name_prefix}-sql"
  resource_group_name          = azurerm_resource_group.remind.name
  location                     = azurerm_resource_group.remind.location
  version                      = "12.0"
  administrator_login          = var.sql_admin_login
  administrator_login_password = var.sql_admin_password

  azuread_administrator {
    login_username = var.sql_aad_admin_login
    object_id      = var.sql_aad_admin_object_id
  }
}

resource "azurerm_mssql_database" "db" {
  name           = "${var.name_prefix}-db"
  server_id      = azurerm_mssql_server.sql.id
  sku_name       = "Basic"
  max_size_gb    = 2
  zone_redundant = false
}

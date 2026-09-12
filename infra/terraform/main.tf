terraform {
  required_version = ">= 1.7.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
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

variable "name_prefix" {
  type        = string
  description = "Prefix used for all resource names."
}

variable "resource_group_name" {
  type        = string
  description = "Azure resource group name."
}

variable "location" {
  type        = string
  description = "Azure region."
  default     = "eastus"
}

variable "webapp_dotnet_version" {
  type        = string
  description = "Dotnet runtime for Azure Linux Web App application stack."
  default     = "10.0"
}

variable "function_dotnet_version" {
  type        = string
  description = "Dotnet runtime for Azure Linux Function App application stack."
  default     = "10.0"
}

variable "sql_admin_login" {
  type        = string
  description = "SQL admin login."
}

variable "sql_admin_password" {
  type        = string
  description = "SQL admin password."
  sensitive   = true
}

variable "sql_connection_setting_value" {
  type        = string
  description = "Function app setting value for SqlConnectionString (prefer a Key Vault reference)."
  sensitive   = true
}

variable "sql_aad_admin_login" {
  type        = string
  description = "Microsoft Entra ID SQL admin name."
}

variable "sql_aad_admin_object_id" {
  type        = string
  description = "Microsoft Entra ID SQL admin object id."
}

variable "external_id_enabled" {
  type        = bool
  description = "Whether to manage customer identity resources in the separate Entra External ID tenant."
  default     = false
}

variable "external_id_tenant_id" {
  type        = string
  description = "Microsoft Entra External ID tenant id, separate from the SQL server's tenant."
  default     = ""
}

variable "external_id_key_vault_name" {
  type        = string
  description = "Globally unique name for the Key Vault that stores social identity-provider secrets."
  default     = ""
}

variable "external_id_web_redirect_uris" {
  type        = list(string)
  description = "Deployed ReMind.Web SPA redirect URIs."
  default     = []
}

variable "external_id_mobile_redirect_uri" {
  type        = string
  description = "ReMind.Mobile public-client redirect URI; keep aligned with the Expo app scheme."
  default     = "remind-mobile://auth"
}

variable "google_client_secret" {
  type        = string
  description = "Google OAuth client secret, supplied through a GitHub environment secret."
  sensitive   = true
  default     = null
}

variable "apple_client_secret" {
  type        = string
  description = "Apple OAuth client secret, supplied through a GitHub environment secret."
  sensitive   = true
  default     = null
}

variable "facebook_client_secret" {
  type        = string
  description = "Facebook OAuth client secret, supplied through a GitHub environment secret."
  sensitive   = true
  default     = null
}

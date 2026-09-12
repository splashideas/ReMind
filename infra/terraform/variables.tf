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

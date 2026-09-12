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

variable "sql_admin_login" {
  type        = string
  description = "SQL admin login."
}

variable "sql_admin_password" {
  type        = string
  description = "SQL admin password."
  sensitive   = true
}

variable "sql_connection_string" {
  type        = string
  description = "Azure SQL connection string used by the function app."
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

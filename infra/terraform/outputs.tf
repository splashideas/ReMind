output "frontend_url" {
  value = "https://${azurerm_linux_web_app.frontend.default_hostname}"
}

output "function_url" {
  value = "https://${azurerm_linux_function_app.function.default_hostname}"
}

output "sql_fqdn" {
  value = azurerm_mssql_server.sql.fully_qualified_domain_name
}

output "external_id_tenant_id" {
  description = "Customer identity tenant id (not the SQL server's Entra tenant)."
  value       = var.external_id_enabled ? var.external_id_tenant_id : null
}

output "api_audience" {
  description = "Audience claim value for v2 access tokens issued for ReMind.Api."
  value       = var.external_id_enabled ? azuread_application.api[0].client_id : null
}

output "api_identifier_uri" {
  description = "Scope request URI prefix for ReMind.Api."
  value       = var.external_id_enabled ? "api://${azuread_application.api[0].client_id}" : null
}

output "api_app_client_id" {
  description = "ReMind.Api application client id."
  value       = var.external_id_enabled ? azuread_application.api[0].client_id : null
}

output "spa_app_client_id" {
  description = "ReMind.Web SPA application client id."
  value       = var.external_id_enabled ? azuread_application.spa[0].client_id : null
}

output "mobile_app_client_id" {
  description = "ReMind.Mobile public-client application client id."
  value       = var.external_id_enabled ? azuread_application.mobile[0].client_id : null
}

output "api_scope" {
  description = "Delegated scope for the SPA and mobile clients to request."
  value       = var.external_id_enabled ? "api://${azuread_application.api[0].client_id}/access_as_user" : null
}

output "admin_role_value" {
  description = "The app-role value emitted in the roles claim for assigned operators."
  value       = var.external_id_enabled ? "Admin" : null
}

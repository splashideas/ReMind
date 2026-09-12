# ReMind

ReMind is structured as a .NET 10 solution for Azure deployment:

- `src/ReMind.Frontend`: ASP.NET Core frontend intended for Azure App Service.
- `src/ReMind.Functions`: Azure Functions app that receives save events and stores data points in Azure SQL.
- `database/ReMind.Database`: SQL project that defines Azure SQL schema and builds a DACPAC.
- `infra/terraform`: Terraform IaC for Azure App Service, Function App, and Azure SQL resources.
- `.github/workflows`: GitHub Actions workflows for build/validate and deploy.

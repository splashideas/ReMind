using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ReMind.Data;
using Testcontainers.MsSql;

namespace ReMind.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public ReMindDbContext CreateContext(string databaseName)
    {
        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 60,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<ReMindDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseNetTopologySuite();
                sql.MigrationsAssembly(typeof(ReMindDbContext).Assembly.GetName().Name);
                sql.CommandTimeout(180);
            })
            .Options;

        return new ReMindDbContext(options);
    }
}

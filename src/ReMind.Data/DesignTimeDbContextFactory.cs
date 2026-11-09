using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReMind.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReMindDbContext>
{
    public ReMindDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReMindDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\mssqllocaldb;Database=ReMind_Design;Trusted_Connection=True;MultipleActiveResultSets=true",
                sql => sql.UseNetTopologySuite())
            .Options;

        return new ReMindDbContext(options);
    }
}

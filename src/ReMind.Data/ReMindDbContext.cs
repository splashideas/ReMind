using Microsoft.EntityFrameworkCore;

namespace ReMind.Data;

public sealed class ReMindDbContext : DbContext
{
    public ReMindDbContext(DbContextOptions<ReMindDbContext> options)
        : base(options)
    {
        if (string.Equals(Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            Database.EnsureCreated();
        }
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<AvatarUpload> AvatarUploads => Set<AvatarUpload>();
    public DbSet<DataPointChain> DataPointChains => Set<DataPointChain>();
    public DbSet<DataPoint> DataPoints => Set<DataPoint>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<DataPointTag> DataPointTags => Set<DataPointTag>();
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();
    public DbSet<GpsFix> GpsFixes => Set<GpsFix>();
    public DbSet<ErasedSubjectHash> ErasedSubjectHashes => Set<ErasedSubjectHash>();
    public DbSet<UserErasureRequest> UserErasureRequests => Set<UserErasureRequest>();
    public DbSet<UserExportRequest> UserExportRequests => Set<UserExportRequest>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<Report> Reports => Set<Report>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer(
                "Server=(localdb)\\mssqllocaldb;Database=ReMind_Design;Trusted_Connection=True;MultipleActiveResultSets=true",
                sql => sql.UseNetTopologySuite());
        }

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReMindDbContext).Assembly);

        // Admin queries must explicitly call IgnoreQueryFilters() when they need deleted rows.
        modelBuilder.Entity<DataPoint>().HasQueryFilter(dataPoint => !dataPoint.IsDeleted);
        modelBuilder.Entity<Comment>().HasQueryFilter(comment => !comment.IsDeleted);

        modelBuilder.Entity<User>().HasData(ReMindSeedData.TombstoneUser);
        modelBuilder.Entity<Tag>().HasData(ReMindSeedData.SystemTags);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReMind.Data;

namespace ReMind.IntegrationTests;

public class SeedAndFilterTests
{
    [Fact]
    public void SystemTagsMatchSeedCatalogFile()
    {
        var seedingDirectory = "Seeding";
        var systemTagsFileName = "system-tags.json";

        if (Path.IsPathRooted(seedingDirectory) || Path.IsPathRooted(systemTagsFileName))
        {
            throw new InvalidOperationException("Path segments for seeded test data must be relative.");
        }

        var jsonPath = Path.Join(AppContext.BaseDirectory, seedingDirectory, systemTagsFileName);
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(jsonPath));

        Assert.NotNull(names);
        Assert.Equal(names, ReMindSeedData.SystemTags.Select(tag => tag.Name));
        Assert.All(ReMindSeedData.SystemTags, tag =>
        {
            Assert.True(tag.IsSystem);
            Assert.Equal(tag.Name.ToLowerInvariant(), tag.NormalizedName);
        });
    }

    [Fact]
    public void ConstructorMaterializesSeedsForFreshInMemoryDatabase()
    {
        var options = new DbContextOptionsBuilder<ReMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new ReMindDbContext(options);

        Assert.Equal(6, db.Tags.Count());
        Assert.Equal("Historic site", db.Tags.Single(t => t.TagId == new Guid("b22f4342-9b18-fd33-975b-304ecdef8895")).Name);
        Assert.Equal(ReMindSeedData.TombstoneUserId, db.Users.Single(u => u.UserId == ReMindSeedData.TombstoneUserId).UserId);
    }

    [Fact]
    public void SeededTagsAndTombstoneAreStableAndQueryable()
    {
        var options = new DbContextOptionsBuilder<ReMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new ReMindDbContext(options);
        db.Database.EnsureCreated();

        Assert.Equal(6, db.Tags.Count());
        Assert.Equal("Historic site", db.Tags.Single(t => t.TagId == new Guid("b22f4342-9b18-fd33-975b-304ecdef8895")).Name);
        Assert.Equal(ReMindSeedData.TombstoneUserId, db.Users.Single(u => u.UserId == ReMindSeedData.TombstoneUserId).UserId);

        db.DataPoints.Add(new DataPoint
        {
            UserId = Guid.NewGuid(),
            Title = "Visible",
            Description = "ok",
            EventDate = DateTimeOffset.UtcNow,
            Location = new NetTopologySuite.Geometries.Point(0, 0) { SRID = 4326 },
            Visibility = Visibility.Public,
            IsDeleted = false,
        });

        db.DataPoints.Add(new DataPoint
        {
            UserId = Guid.NewGuid(),
            Title = "Deleted",
            Description = "hidden",
            EventDate = DateTimeOffset.UtcNow,
            Location = new NetTopologySuite.Geometries.Point(1, 1) { SRID = 4326 },
            Visibility = Visibility.Public,
            IsDeleted = true,
        });

        db.SaveChanges();

        Assert.Equal(1, db.DataPoints.Count());
        Assert.Equal("Visible", db.DataPoints.Single().Title);

        var comment = new Comment
        {
            CommentId = Guid.NewGuid(),
            DataPointId = db.DataPoints.Single().DataPointId,
            UserId = Guid.NewGuid(),
            Text = "ok",
            IsDeleted = false,
        };

        db.Comments.Add(comment);
        db.SaveChanges();

        Assert.Equal(1, db.Comments.Count());

        db.Comments.Add(new Comment
        {
            CommentId = Guid.NewGuid(),
            DataPointId = db.DataPoints.Single().DataPointId,
            UserId = Guid.NewGuid(),
            Text = "deleted",
            IsDeleted = true,
        });

        db.SaveChanges();

        Assert.Equal(1, db.Comments.Count());
        Assert.Equal(2, db.Comments.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void ConstructorMaterializesSeedsWithoutExplicitEnsureCreated()
    {
        var options = new DbContextOptionsBuilder<ReMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new ReMindDbContext(options);

        Assert.Equal(6, db.Tags.Count());
        Assert.Equal("Historic site", db.Tags.Single(t => t.TagId == new Guid("b22f4342-9b18-fd33-975b-304ecdef8895")).Name);
        Assert.Equal(ReMindSeedData.TombstoneUserId, db.Users.Single(u => u.UserId == ReMindSeedData.TombstoneUserId).UserId);
    }
}

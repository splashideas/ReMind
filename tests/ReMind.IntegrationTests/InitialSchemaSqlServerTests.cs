using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NetTopologySuite.Geometries;
using ReMind.Data;
using ReMind.Data.Migrations;

namespace ReMind.IntegrationTests;

public sealed class InitialSchemaSqlServerTests : IClassFixture<SqlServerFixture>
{
    private const string InitialSchemaMigrationId = "20261109011639_InitialSchema";
    private const string HistoryRowsSql = """
        SELECT STRING_AGG(MigrationId + '|' + ProductVersion, ',') WITHIN GROUP (ORDER BY MigrationId)
        FROM [__EFMigrationsHistory]
        """;

    private readonly SqlServerFixture _fixture;

    public InitialSchemaSqlServerTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MigrationAppliesSeedsAndInsertsOneLegalRowPerTable()
    {
        var databaseName = NewDatabaseName();
        await using var db = _fixture.CreateContext(databaseName);
        await db.Database.MigrateAsync(InitialSchemaMigrationId);

        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes"));

        await db.Database.MigrateAsync();

        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UQ_AvatarUploads_OneLivePerOwner'"));
        Assert.Equal(
            "NO_ACTION",
            await ScalarAsync<string>(
                db,
                "SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = 'FK_Comments_Comments_ParentCommentId'"));

        var tombstone = await db.Users.SingleAsync(user => user.UserId == ReMindSeedData.TombstoneUserId);
        Assert.Equal("__remind_tombstone__", tombstone.Issuer);
        var systemTagNames = await db.Tags.Where(tag => tag.IsSystem).Select(tag => tag.Name).ToListAsync();
        Assert.Equal(
            ReMindSeedData.SystemTags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.Ordinal),
            systemTagNames.OrderBy(name => name, StringComparer.Ordinal));

        var inserted = await InsertOneLegalRowPerTableAsync(db);

        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(1, await db.AvatarUploads.CountAsync());
        Assert.Equal(1, await db.DataPointChains.CountAsync());
        Assert.Equal(1, await db.DataPoints.CountAsync());
        Assert.Equal(7, await db.Tags.CountAsync());
        Assert.Equal(1, await db.DataPointTags.CountAsync());
        Assert.Equal(1, await db.UserConsents.CountAsync());
        Assert.Equal(1, await db.GpsFixes.CountAsync());
        Assert.Equal(1, await db.ErasedSubjectHashes.CountAsync());
        Assert.Equal(1, await db.UserErasureRequests.CountAsync());
        Assert.Equal(1, await db.UserExportRequests.CountAsync());
        Assert.Equal(1, await db.Media.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.Follows.CountAsync());
        Assert.Equal(1, await db.Comments.CountAsync());
        Assert.Equal(1, await db.Reactions.CountAsync());
        Assert.Equal(1, await db.Reports.CountAsync());
        Assert.Equal(inserted.DataPointId, (await db.DataPoints.SingleAsync()).DataPointId);
        Assert.Equal(4326, (await db.DataPoints.SingleAsync()).Location.SRID);
        Assert.Equal(4326, (await db.GpsFixes.SingleAsync()).Location.SRID);
    }

    [Fact]
    public async Task CheckConstraintsRejectIllegalRows()
    {
        var databaseName = NewDatabaseName();
        await using var setup = _fixture.CreateContext(databaseName);
        await setup.Database.MigrateAsync();
        var legal = await InsertOneLegalRowPerTableAsync(setup);
        var commentId = await setup.Comments.Select(comment => comment.CommentId).SingleAsync();

        await using (var db = _fixture.CreateContext(databaseName))
        {
            db.Users.Add(NewUser(Guid.NewGuid(), pendingReservedBytes: -1));
            await AssertCheckConstraintAsync(() => db.SaveChangesAsync(), "CK_Users_PendingReservedBytes_NonNegative");
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            db.DataPoints.Add(NewDataPoint(legal.OwnerId, visibility: (Visibility)3));
            await AssertCheckConstraintAsync(() => db.SaveChangesAsync(), "CK_DataPoints_Visibility");
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            db.Follows.Add(new Follow
            {
                FollowerId = legal.OwnerId,
                FolloweeId = legal.OwnerId,
                Status = (byte)FollowStatus.Pending,
                CreatedUtc = DateTime.UtcNow,
            });
            await AssertCheckConstraintAsync(() => db.SaveChangesAsync(), "CK_Follows_NotSelf");
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            db.Reports.Add(new Report
            {
                ReportId = Guid.NewGuid(),
                ReporterId = legal.OtherId,
                DataPointId = legal.DataPointId,
                CommentId = commentId,
                Reason = "both targets",
                Status = (byte)ReportStatus.Open,
                CreatedUtc = DateTime.UtcNow,
            });
            await AssertCheckConstraintAsync(() => db.SaveChangesAsync(), "CK_Reports_OneTarget");
        }
    }

    [Fact]
    public async Task SecondMigrateOnDatabaseWithHistoryIsNoOp()
    {
        var databaseName = NewDatabaseName();
        Guid markerId;
        string historyBefore;
        int tagCountBefore;
        int userCountBefore;

        await using (var db = _fixture.CreateContext(databaseName))
        {
            await db.Database.MigrateAsync();
            markerId = Guid.NewGuid();
            db.Users.Add(NewUser(markerId));
            await db.SaveChangesAsync();
            historyBefore = await ScalarAsync<string>(db, HistoryRowsSql);
            tagCountBefore = await db.Tags.CountAsync();
            userCountBefore = await db.Users.CountAsync();
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            await db.Database.MigrateAsync();

            Assert.Equal(historyBefore, await ScalarAsync<string>(db, HistoryRowsSql));
            Assert.Equal(2, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM [__EFMigrationsHistory]"));
            Assert.Equal(tagCountBefore, await db.Tags.CountAsync());
            Assert.Equal(userCountBefore, await db.Users.CountAsync());
            Assert.Equal(6, await db.Tags.CountAsync(tag => tag.IsSystem));
            Assert.Equal(markerId, (await db.Users.SingleAsync(user => user.UserId == markerId)).UserId);
            Assert.Equal(ReMindSeedData.TombstoneUserId, (await db.Users.SingleAsync(user => user.Issuer == "__remind_tombstone__")).UserId);
        }
    }

    [Fact]
    public async Task DeletingParentCommentNullsChildParentCommentId()
    {
        var databaseName = NewDatabaseName();
        Guid parentId;
        Guid childId;

        await using (var db = _fixture.CreateContext(databaseName))
        {
            await db.Database.MigrateAsync();
            var legal = await InsertOneLegalRowPerTableAsync(db);
            parentId = await db.Comments.Select(comment => comment.CommentId).SingleAsync();
            childId = Guid.NewGuid();
            db.Comments.Add(new Comment
            {
                CommentId = childId,
                DataPointId = legal.DataPointId,
                UserId = legal.OwnerId,
                Text = "reply",
                ParentCommentId = parentId,
                CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            var parent = await db.Comments.SingleAsync(comment => comment.CommentId == parentId);
            db.Comments.Remove(parent);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext(databaseName))
        {
            var child = await db.Comments.SingleAsync(comment => comment.CommentId == childId);
            Assert.Null(child.ParentCommentId);
            Assert.False(await db.Comments.IgnoreQueryFilters().AnyAsync(comment => comment.CommentId == parentId));
        }
    }

    [Fact]
    public async Task SpatialMigrationCreatesLocationIndex()
    {
        var databaseName = NewDatabaseName();
        await using var db = _fixture.CreateContext(databaseName);
        await db.Database.MigrateAsync();

        Assert.Equal(
            1,
            await ScalarAsync<int>(
                db,
                "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));
        Assert.Equal(
            "dbo",
            await ScalarAsync<string>(
                db,
                """
                SELECT s.name
                FROM sys.spatial_indexes AS i
                INNER JOIN sys.tables AS t ON i.object_id = t.object_id
                INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
                WHERE i.name = 'IX_DataPoints_Location'
                """));
        Assert.Equal(
            "DataPoints",
            await ScalarAsync<string>(
                db,
                """
                SELECT t.name
                FROM sys.spatial_indexes AS i
                INNER JOIN sys.tables AS t ON i.object_id = t.object_id
                WHERE i.name = 'IX_DataPoints_Location'
                """));
        Assert.Equal(
            "Location",
            await ScalarAsync<string>(
                db,
                """
                SELECT c.name
                FROM sys.spatial_indexes AS i
                INNER JOIN sys.index_columns AS ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                INNER JOIN sys.columns AS c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE i.name = 'IX_DataPoints_Location'
                """));
        Assert.Equal(
            "GEOGRAPHY_AUTO_GRID",
            await ScalarAsync<string>(
                db,
                "SELECT tessellation_scheme FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));
    }

    [Fact]
    public async Task SpatialMigrationSqlCanBeReplayedWithoutThrowing()
    {
        var migration = new AddDataPointsLocationSpatialIndex();
        var create = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.True(create.SuppressTransaction);
        Assert.Contains("IX_DataPoints_Location", create.Sql, StringComparison.Ordinal);
        Assert.Contains("GEOGRAPHY_AUTO_GRID", create.Sql, StringComparison.Ordinal);
        Assert.Contains("sys.spatial_indexes", create.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", create.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN TRAN", create.Sql, StringComparison.OrdinalIgnoreCase);

        var databaseName = NewDatabaseName();
        await using var db = _fixture.CreateContext(databaseName);
        await db.Database.MigrateAsync(InitialSchemaMigrationId);
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes"));

        await db.Database.ExecuteSqlRawAsync(create.Sql);
        await db.Database.ExecuteSqlRawAsync(create.Sql);
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                db,
                "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(create.Sql);

        Assert.Equal(
            1,
            await ScalarAsync<int>(
                db,
                "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));
        Assert.Equal(2, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM [__EFMigrationsHistory]"));
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                db,
                "SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE MigrationId = '20261111235036_AddDataPointsLocationSpatialIndex'"));
    }

    [Fact]
    public async Task SpatialMigrationDownDropsIndexAndReapplyRestoresIt()
    {
        var databaseName = NewDatabaseName();
        await using var db = _fixture.CreateContext(databaseName);
        await db.Database.MigrateAsync();
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));

        await db.Database.MigrateAsync(InitialSchemaMigrationId);
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));

        await db.Database.MigrateAsync();
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.spatial_indexes WHERE name = 'IX_DataPoints_Location'"));
    }

    private static async Task<LegalRows> InsertOneLegalRowPerTableAsync(ReMindDbContext db)
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var chainId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var erasureId = Guid.NewGuid();

        db.Users.AddRange(NewUser(ownerId), NewUser(otherId));
        db.AvatarUploads.Add(new AvatarUpload
        {
            UploadId = Guid.NewGuid(),
            OwnerUserId = ownerId,
            ClientRequestId = "avatar-1",
            Status = 0,
            ReservedBytes = 1024,
            RequestedSizeBytes = 512,
            StagingBlobPath = "avatars/staging/1",
            CanonicalBlobPath = "avatars/canonical/1",
            UploadExpiresUtc = now.AddHours(1),
            CreatedUtc = now,
        });
        db.DataPointChains.Add(new DataPointChain
        {
            ChainId = chainId,
            Title = "Chain",
            CreatedByUserId = ownerId,
            CreatedUtc = now,
        });
        db.Tags.Add(new Tag
        {
            TagId = tagId,
            Name = "Custom",
            NormalizedName = "custom",
            IsSystem = false,
            CreatedByUserId = ownerId,
            CreatedUtc = now,
        });
        db.UserConsents.Add(new UserConsent
        {
            ConsentId = Guid.NewGuid(),
            UserId = ownerId,
            Purpose = 0,
            PolicyVersion = "1",
            Granted = true,
            RecordedUtc = now,
        });
        db.GpsFixes.Add(new GpsFix
        {
            GpsFixId = Guid.NewGuid(),
            UserId = ownerId,
            Location = new Point(13.405, 52.52) { SRID = 4326 },
            CapturedUtc = now,
            AccuracyMeters = 5,
            ExpiresUtc = now.AddMinutes(15),
        });
        db.UserErasureRequests.Add(new UserErasureRequest
        {
            RequestId = erasureId,
            UserId = ownerId,
            Status = (byte)UserErasureStatus.Pending,
            StatusReceiptHash = Hash(1),
            RequestedUtc = now,
        });
        db.ErasedSubjectHashes.Add(new ErasedSubjectHash
        {
            SubjectHash = Hash(2),
            KeyVersion = 1,
            ErasedUtc = now,
            ErasureRequestId = erasureId,
        });
        db.UserExportRequests.Add(new UserExportRequest
        {
            RequestId = Guid.NewGuid(),
            UserId = ownerId,
            Status = (byte)UserExportStatus.Pending,
            DownloadReceiptHash = Hash(3),
            RequestedUtc = now,
        });
        db.OutboxMessages.Add(new OutboxMessage
        {
            OutboxId = Guid.NewGuid(),
            Type = "media.scan",
            DedupKey = "dedup-1",
            Payload = "{}",
            CreatedUtc = now,
            Attempts = 0,
        });
        await db.SaveChangesAsync();

        var point = NewDataPoint(ownerId, Visibility.Public, chainId);
        db.DataPoints.Add(point);
        await db.SaveChangesAsync();

        db.DataPointTags.Add(new DataPointTag { DataPointId = point.DataPointId, TagId = tagId });
        db.Media.Add(new Media
        {
            MediaId = Guid.NewGuid(),
            DataPointId = point.DataPointId,
            OwnerUserId = ownerId,
            UploadId = Guid.NewGuid(),
            ClientRequestId = "media-1",
            MediaType = 0,
            Status = 0,
            ReservedBytes = 1024,
            RequestedSizeBytes = 256,
            StagingBlobPath = "media/staging/1",
            CanonicalBlobPath = "media/canonical/1",
            UploadExpiresUtc = now.AddHours(1),
            CreatedUtc = now,
        });
        db.Comments.Add(new Comment
        {
            CommentId = Guid.NewGuid(),
            DataPointId = point.DataPointId,
            UserId = ownerId,
            Text = "Legal comment",
            CreatedUtc = now,
        });
        db.Reactions.Add(new Reaction
        {
            ReactionId = Guid.NewGuid(),
            DataPointId = point.DataPointId,
            UserId = ownerId,
            ReactionType = 0,
            CreatedUtc = now,
        });
        db.Reports.Add(new Report
        {
            ReportId = Guid.NewGuid(),
            ReporterId = otherId,
            DataPointId = point.DataPointId,
            Reason = "spam",
            Status = (byte)ReportStatus.Open,
            CreatedUtc = now,
        });
        db.Follows.Add(new Follow
        {
            FollowerId = ownerId,
            FolloweeId = otherId,
            Status = (byte)FollowStatus.Accepted,
            CreatedUtc = now,
        });
        await db.SaveChangesAsync();

        return new LegalRows(ownerId, otherId, point.DataPointId);
    }

    private static User NewUser(Guid userId, long pendingReservedBytes = 0) => new()
    {
        UserId = userId,
        Issuer = "https://login.example.test",
        ExternalSubject = userId.ToString("D"),
        DisplayName = "Tester",
        PendingReservedBytes = pendingReservedBytes,
        CreatedUtc = DateTime.UtcNow,
    };

    private static DataPoint NewDataPoint(Guid ownerId, Visibility visibility, Guid? chainId = null) => new()
    {
        UserId = ownerId,
        ChainId = chainId,
        Title = "Legal point",
        Description = "A legal row",
        EventDate = DateTimeOffset.UtcNow,
        Location = new Point(13.405, 52.52) { SRID = 4326 },
        CreatedUtc = DateTime.UtcNow,
        Visibility = visibility,
    };

    private static byte[] Hash(byte marker)
    {
        var hash = new byte[32];
        hash[0] = marker;
        return hash;
    }

    private static async Task AssertCheckConstraintAsync(Func<Task> act, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(act);
        var sql = FindSqlException(exception);
        Assert.Equal(547, sql.Number);
        Assert.Contains(constraintName, sql.Message, StringComparison.Ordinal);
    }

    private static SqlException FindSqlException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        throw new InvalidOperationException("Expected a SQL Server exception.", exception);
    }

    private static async Task<T> ScalarAsync<T>(ReMindDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return (T)result!;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string NewDatabaseName() => "ReMind_" + Guid.NewGuid().ToString("N");

    private sealed record LegalRows(Guid OwnerId, Guid OtherId, long DataPointId);
}

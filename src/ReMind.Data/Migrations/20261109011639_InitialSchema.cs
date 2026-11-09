using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ReMind.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErasedSubjectHashes",
                columns: table => new
                {
                    SubjectHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    ErasedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ErasureRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErasedSubjectHashes", x => x.SubjectHash);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DedupKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.OutboxId);
                });

            migrationBuilder.CreateTable(
                name: "UserErasureRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    StatusReceiptHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuditJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserErasureRequests", x => x.RequestId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExternalSubject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AvatarBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PendingReservedBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    Bio = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    DeletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                    table.CheckConstraint("CK_Users_PendingReservedBytes_NonNegative", "PendingReservedBytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "AvatarUploads",
                columns: table => new
                {
                    UploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientRequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StagingBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CanonicalBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CanonicalETag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UploadExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MalwareScanVerdict = table.Column<byte>(type: "tinyint", nullable: true),
                    MalwareScannedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QuarantineReviewByUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarUploads", x => x.UploadId);
                    table.CheckConstraint("CK_AvatarUploads_RejectedUtc_Matches_Status", "(Status = 5 AND RejectedUtc IS NOT NULL) OR (Status <> 5 AND RejectedUtc IS NULL)");
                    table.CheckConstraint("CK_AvatarUploads_Requested_Within_Reserved", "RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes");
                    table.CheckConstraint("CK_AvatarUploads_ReservedBytes_Positive", "ReservedBytes > 0");
                    table.CheckConstraint("CK_AvatarUploads_Status_Range", "Status BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_AvatarUploads_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "DataPointChains",
                columns: table => new
                {
                    ChainId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataPointChains", x => x.ChainId);
                    table.ForeignKey(
                        name: "FK_DataPointChains_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "Follows",
                columns: table => new
                {
                    FollowerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolloweeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    RespondedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Follows", x => new { x.FollowerId, x.FolloweeId });
                    table.CheckConstraint("CK_Follows_NotSelf", "FollowerId <> FolloweeId");
                    table.ForeignKey(
                        name: "FK_Follows_Users_FolloweeId",
                        column: x => x.FolloweeId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                    table.ForeignKey(
                        name: "FK_Follows_Users_FollowerId",
                        column: x => x.FollowerId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "GpsFixes",
                columns: table => new
                {
                    GpsFixId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Location = table.Column<Point>(type: "geography", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccuracyMeters = table.Column<double>(type: "float", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpsFixes", x => x.GpsFixId);
                    table.CheckConstraint("CK_GpsFixes_Location_SRID", "Location.STSrid = 4326");
                    table.ForeignKey(
                        name: "FK_GpsFixes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.TagId);
                    table.ForeignKey(
                        name: "FK_Tags_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "UserConsents",
                columns: table => new
                {
                    ConsentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<byte>(type: "tinyint", nullable: false),
                    PolicyVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Granted = table.Column<bool>(type: "bit", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ClientUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserConsents", x => x.ConsentId);
                    table.ForeignKey(
                        name: "FK_UserConsents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "UserExportRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    DownloadReceiptHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    DownloadExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DownloadBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AuditJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExportRequests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_UserExportRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "DataPoints",
                columns: table => new
                {
                    DataPointId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChainId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EventDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Location = table.Column<Point>(type: "geography", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Visibility = table.Column<byte>(type: "tinyint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataPoints", x => x.DataPointId);
                    table.CheckConstraint("CK_DataPoints_Location_SRID", "Location.STSrid = 4326");
                    table.CheckConstraint("CK_DataPoints_Visibility", "Visibility IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_DataPoints_DataPointChains_ChainId",
                        column: x => x.ChainId,
                        principalTable: "DataPointChains",
                        principalColumn: "ChainId");
                    table.ForeignKey(
                        name: "FK_DataPoints_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataPointId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ParentCommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.CommentId);
                    table.ForeignKey(
                        name: "FK_Comments_Comments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "Comments",
                        principalColumn: "CommentId");
                    table.ForeignKey(
                        name: "FK_Comments_DataPoints_DataPointId",
                        column: x => x.DataPointId,
                        principalTable: "DataPoints",
                        principalColumn: "DataPointId");
                    table.ForeignKey(
                        name: "FK_Comments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "DataPointTags",
                columns: table => new
                {
                    DataPointId = table.Column<long>(type: "bigint", nullable: false),
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataPointTags", x => new { x.DataPointId, x.TagId });
                    table.ForeignKey(
                        name: "FK_DataPointTags_DataPoints_DataPointId",
                        column: x => x.DataPointId,
                        principalTable: "DataPoints",
                        principalColumn: "DataPointId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataPointTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "TagId");
                });

            migrationBuilder.CreateTable(
                name: "Media",
                columns: table => new
                {
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataPointId = table.Column<long>(type: "bigint", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientRequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MediaType = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StagingBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CanonicalBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CanonicalETag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ThumbnailPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ModerationApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ThumbnailReadyUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MalwareScanVerdict = table.Column<byte>(type: "tinyint", nullable: true),
                    MalwareScannedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QuarantineReviewByUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UploadExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media", x => x.MediaId);
                    table.CheckConstraint("CK_Media_RejectedUtc_Matches_Status", "(Status = 5 AND RejectedUtc IS NOT NULL) OR (Status <> 5 AND RejectedUtc IS NULL)");
                    table.CheckConstraint("CK_Media_Requested_Within_Reserved", "RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes");
                    table.ForeignKey(
                        name: "FK_Media_DataPoints_DataPointId",
                        column: x => x.DataPointId,
                        principalTable: "DataPoints",
                        principalColumn: "DataPointId");
                    table.ForeignKey(
                        name: "FK_Media_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "Reactions",
                columns: table => new
                {
                    ReactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataPointId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReactionType = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reactions", x => x.ReactionId);
                    table.ForeignKey(
                        name: "FK_Reactions_DataPoints_DataPointId",
                        column: x => x.DataPointId,
                        principalTable: "DataPoints",
                        principalColumn: "DataPointId");
                    table.ForeignKey(
                        name: "FK_Reactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataPointId = table.Column<long>(type: "bigint", nullable: true),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.ReportId);
                    table.CheckConstraint("CK_Reports_OneTarget", "(DataPointId IS NOT NULL AND CommentId IS NULL) OR (DataPointId IS NULL AND CommentId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Reports_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "CommentId");
                    table.ForeignKey(
                        name: "FK_Reports_DataPoints_DataPointId",
                        column: x => x.DataPointId,
                        principalTable: "DataPoints",
                        principalColumn: "DataPointId");
                    table.ForeignKey(
                        name: "FK_Reports_Users_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.InsertData(
                table: "Tags",
                columns: new[] { "TagId", "CreatedByUserId", "CreatedUtc", "IsSystem", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("14d24f98-ae39-d299-0b27-77a4dcfb7211"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Industry", "industry" },
                    { new Guid("6694ea80-7500-1f66-28da-20f1afdafc74"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Transport", "transport" },
                    { new Guid("6af308d5-ad70-1d4b-9fe0-b567c2786755"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Disaster", "disaster" },
                    { new Guid("b22f4342-9b18-fd33-975b-304ecdef8895"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Historic site", "historic site" },
                    { new Guid("f1947f79-fdfb-8046-1509-59ca09cdd05c"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Culture", "culture" },
                    { new Guid("fa9e1d22-205a-d852-b0dc-9509ec4e3164"), null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "Conflict", "conflict" }
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "UserId", "AvatarBlobPath", "Bio", "CreatedUtc", "DeletedUtc", "DisplayName", "ExternalSubject", "Issuer" },
                values: new object[] { new Guid("0d0f89d2-40db-47b1-b1ae-6ff9a4f14092"), null, null, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "System Tombstone", "00000000-0000-0000-0000-000000000000", "__remind_tombstone__" });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarUploads_Owner_Status_Expires",
                table: "AvatarUploads",
                columns: new[] { "OwnerUserId", "Status", "UploadExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "UQ_AvatarUploads_OneLivePerOwner",
                table: "AvatarUploads",
                column: "OwnerUserId",
                unique: true,
                filter: "Status IN (0, 1, 2, 3)");

            migrationBuilder.CreateIndex(
                name: "UQ_AvatarUploads_Owner_ClientRequestId",
                table: "AvatarUploads",
                columns: new[] { "OwnerUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_DataPointId",
                table: "Comments",
                column: "DataPointId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentCommentId",
                table: "Comments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_UserId",
                table: "Comments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DataPointChains_CreatedByUserId",
                table: "DataPointChains",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DataPoints_Chain_EventDate",
                table: "DataPoints",
                columns: new[] { "ChainId", "EventDate", "DataPointId" },
                descending: new[] { false, true, true },
                filter: "IsDeleted = 0 AND ChainId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DataPoints_User_CreatedUtc",
                table: "DataPoints",
                columns: new[] { "UserId", "CreatedUtc", "DataPointId" },
                descending: new[] { false, true, true },
                filter: "IsDeleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DataPointTags_TagId",
                table: "DataPointTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ErasedSubjectHashes_KeyVersion",
                table: "ErasedSubjectHashes",
                column: "KeyVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Follows_Followee_Status_CreatedUtc",
                table: "Follows",
                columns: new[] { "FolloweeId", "Status", "CreatedUtc", "FollowerId" });

            migrationBuilder.CreateIndex(
                name: "IX_GpsFixes_Expires",
                table: "GpsFixes",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_GpsFixes_UserId",
                table: "GpsFixes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_DataPointId_CanonicalBlobPath",
                table: "Media",
                columns: new[] { "DataPointId", "CanonicalBlobPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_OwnerUserId_DataPointId_ClientRequestId",
                table: "Media",
                columns: new[] { "OwnerUserId", "DataPointId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_Status_CreatedUtc",
                table: "Media",
                columns: new[] { "Status", "CreatedUtc", "MediaId" })
                .Annotation("SqlServer:Include", new[] { "DataPointId", "OwnerUserId", "UploadId", "ThumbnailPath", "MalwareScanVerdict", "QuarantineReviewByUtc", "RejectedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Media_UploadId",
                table: "Media",
                column: "UploadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DedupKey",
                table: "OutboxMessages",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Unprocessed",
                table: "OutboxMessages",
                columns: new[] { "CreatedUtc", "OutboxId" },
                filter: "ProcessedUtc IS NULL")
                .Annotation("SqlServer:Include", new[] { "Type", "DedupKey", "Attempts" });

            migrationBuilder.CreateIndex(
                name: "IX_Reactions_DataPointId_UserId",
                table: "Reactions",
                columns: new[] { "DataPointId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reactions_UserId",
                table: "Reactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CommentId",
                table: "Reports",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_DataPointId",
                table: "Reports",
                column: "DataPointId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId",
                table: "Reports",
                column: "ReporterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status_CreatedUtc",
                table: "Reports",
                columns: new[] { "Status", "CreatedUtc", "ReportId" })
                .Annotation("SqlServer:Include", new[] { "ReporterId", "DataPointId", "CommentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_CreatedByUserId",
                table: "Tags",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NormalizedName",
                table: "Tags",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserConsents_UserId_Purpose_PolicyVersion",
                table: "UserConsents",
                columns: new[] { "UserId", "Purpose", "PolicyVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserExportRequests_UserId",
                table: "UserExportRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Issuer_ExternalSubject",
                table: "Users",
                columns: new[] { "Issuer", "ExternalSubject" },
                unique: true);

            // Drift from §2 ON DELETE SET NULL: SQL Server rejects that self-referencing
            // cascading action (error 1785). The FK is NO ACTION; this trigger preserves
            // the SET NULL outcome in the same statement.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER TR_Comments_NullParentOnDelete
                ON dbo.Comments
                INSTEAD OF DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;

                    UPDATE child
                    SET ParentCommentId = NULL
                    FROM dbo.Comments AS child
                    INNER JOIN deleted AS parent ON child.ParentCommentId = parent.CommentId;

                    DELETE target
                    FROM dbo.Comments AS target
                    INNER JOIN deleted AS removed ON target.CommentId = removed.CommentId;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_Comments_NullParentOnDelete;");

            migrationBuilder.DropTable(
                name: "AvatarUploads");

            migrationBuilder.DropTable(
                name: "DataPointTags");

            migrationBuilder.DropTable(
                name: "ErasedSubjectHashes");

            migrationBuilder.DropTable(
                name: "Follows");

            migrationBuilder.DropTable(
                name: "GpsFixes");

            migrationBuilder.DropTable(
                name: "Media");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "Reactions");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "UserConsents");

            migrationBuilder.DropTable(
                name: "UserErasureRequests");

            migrationBuilder.DropTable(
                name: "UserExportRequests");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "DataPoints");

            migrationBuilder.DropTable(
                name: "DataPointChains");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

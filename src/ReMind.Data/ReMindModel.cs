using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace ReMind.Data;

public enum Visibility : byte
{
    Public = 0,
    FollowersOnly = 1,
    Private = 2,
}

public enum FollowStatus : byte
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

public enum ReportStatus : byte
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2,
}

public enum UserErasureStatus : byte
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

public enum UserExportStatus : byte
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

public static class ReMindSeedData
{
    public static readonly Guid TombstoneUserId = new("0d0f89d2-40db-47b1-b1ae-6ff9a4f14092");

    public static readonly User TombstoneUser = new()
    {
        UserId = TombstoneUserId,
        Issuer = "__remind_tombstone__",
        ExternalSubject = "00000000-0000-0000-0000-000000000000",
        DisplayName = "System Tombstone",
        PendingReservedBytes = 0,
        CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static readonly Tag[] SystemTags =
    [
        new()
        {
            TagId = new Guid("b22f4342-9b18-fd33-975b-304ecdef8895"),
            Name = "Historic site",
            NormalizedName = "historic site",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            TagId = new Guid("fa9e1d22-205a-d852-b0dc-9509ec4e3164"),
            Name = "Conflict",
            NormalizedName = "conflict",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            TagId = new Guid("f1947f79-fdfb-8046-1509-59ca09cdd05c"),
            Name = "Culture",
            NormalizedName = "culture",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            TagId = new Guid("14d24f98-ae39-d299-0b27-77a4dcfb7211"),
            Name = "Industry",
            NormalizedName = "industry",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            TagId = new Guid("6694ea80-7500-1f66-28da-20f1afdafc74"),
            Name = "Transport",
            NormalizedName = "transport",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            TagId = new Guid("6af308d5-ad70-1d4b-9fe0-b567c2786755"),
            Name = "Disaster",
            NormalizedName = "disaster",
            IsSystem = true,
            CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        },
    ];
}

public sealed class User
{
    public Guid UserId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string ExternalSubject { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarBlobPath { get; set; }
    public long PendingReservedBytes { get; set; }
    public string? Bio { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
}

public sealed class AvatarUpload
{
    public Guid UploadId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
    public byte Status { get; set; }
    public long ReservedBytes { get; set; }
    public long RequestedSizeBytes { get; set; }
    public string StagingBlobPath { get; set; } = string.Empty;
    public string CanonicalBlobPath { get; set; } = string.Empty;
    public string? CanonicalETag { get; set; }
    public DateTime UploadExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? RejectedUtc { get; set; }
    public byte? MalwareScanVerdict { get; set; }
    public DateTime? MalwareScannedUtc { get; set; }
    public DateTime? QuarantineReviewByUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public User? Owner { get; set; }
}

public sealed class DataPointChain
{
    public Guid ChainId { get; set; }
    public string? Title { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public User? CreatedBy { get; set; }
}

public sealed class DataPoint
{
    public long DataPointId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ChainId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset EventDate { get; set; }
    public Point Location { get; set; } = new Point(0, 0) { SRID = 4326 };
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public Visibility Visibility { get; set; }
    public bool IsDeleted { get; set; }
    public User? User { get; set; }
    public DataPointChain? Chain { get; set; }
}

public sealed class Tag
{
    public Guid TagId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public User? CreatedBy { get; set; }
}

public sealed class DataPointTag
{
    public long DataPointId { get; set; }
    public Guid TagId { get; set; }
    public DataPoint? DataPoint { get; set; }
    public Tag? Tag { get; set; }
}

public sealed class UserConsent
{
    public Guid ConsentId { get; set; }
    public Guid UserId { get; set; }
    public byte Purpose { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public bool Granted { get; set; }
    public DateTime RecordedUtc { get; set; }
    public DateTimeOffset? ClientUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public User? User { get; set; }
}

public sealed class GpsFix
{
    public Guid GpsFixId { get; set; }
    public Guid UserId { get; set; }
    public Point Location { get; set; } = new Point(0, 0) { SRID = 4326 };
    public DateTime CapturedUtc { get; set; }
    public double? AccuracyMeters { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public User? User { get; set; }
}

public sealed class ErasedSubjectHash
{
    public byte[] SubjectHash { get; set; } = Array.Empty<byte>();
    public int KeyVersion { get; set; }
    public DateTime ErasedUtc { get; set; }
    public Guid ErasureRequestId { get; set; }
}

public sealed class UserErasureRequest
{
    public Guid RequestId { get; set; }
    public Guid? UserId { get; set; }
    public byte Status { get; set; }
    public byte[] StatusReceiptHash { get; set; } = Array.Empty<byte>();
    public DateTime RequestedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? AuditJson { get; set; }
}

public sealed class UserExportRequest
{
    public Guid RequestId { get; set; }
    public Guid UserId { get; set; }
    public byte Status { get; set; }
    public byte[] DownloadReceiptHash { get; set; } = Array.Empty<byte>();
    public DateTime? DownloadExpiresUtc { get; set; }
    public string? DownloadBlobPath { get; set; }
    public DateTime RequestedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? AuditJson { get; set; }
    public User? User { get; set; }
}

public sealed class Media
{
    public Guid MediaId { get; set; }
    public long DataPointId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid UploadId { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
    public byte MediaType { get; set; }
    public byte Status { get; set; }
    public long ReservedBytes { get; set; }
    public long RequestedSizeBytes { get; set; }
    public string StagingBlobPath { get; set; } = string.Empty;
    public string CanonicalBlobPath { get; set; } = string.Empty;
    public string? CanonicalETag { get; set; }
    public string? ThumbnailPath { get; set; }
    public int SortOrder { get; set; }
    public DateTime? ModerationApprovedUtc { get; set; }
    public DateTime? ThumbnailReadyUtc { get; set; }
    public byte? MalwareScanVerdict { get; set; }
    public DateTime? MalwareScannedUtc { get; set; }
    public DateTime? QuarantineReviewByUtc { get; set; }
    public DateTime? RejectedUtc { get; set; }
    public DateTime UploadExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DataPoint? DataPoint { get; set; }
    public User? Owner { get; set; }
}

public sealed class OutboxMessage
{
    public Guid OutboxId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string DedupKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public int Attempts { get; set; }
}

public sealed class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
    public byte Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RespondedUtc { get; set; }
    public User? Follower { get; set; }
    public User? Followee { get; set; }
}

public sealed class Comment
{
    public Guid CommentId { get; set; }
    public long DataPointId { get; set; }
    public Guid UserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public Guid? ParentCommentId { get; set; }
    public bool IsDeleted { get; set; }
    public DataPoint? DataPoint { get; set; }
    public User? User { get; set; }
    public Comment? ParentComment { get; set; }
}

public sealed class Reaction
{
    public Guid ReactionId { get; set; }
    public long DataPointId { get; set; }
    public Guid UserId { get; set; }
    public byte ReactionType { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DataPoint? DataPoint { get; set; }
    public User? User { get; set; }
}

public sealed class Report
{
    public Guid ReportId { get; set; }
    public Guid ReporterId { get; set; }
    public long? DataPointId { get; set; }
    public Guid? CommentId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public byte Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public User? Reporter { get; set; }
    public DataPoint? DataPoint { get; set; }
    public Comment? Comment { get; set; }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("UserId");
        builder.Property(x => x.Issuer).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ExternalSubject).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.AvatarBlobPath).HasMaxLength(400);
        builder.Property(x => x.Bio).HasMaxLength(1000);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.PendingReservedBytes).HasDefaultValue(0L);
        builder.HasIndex(x => new { x.Issuer, x.ExternalSubject }).IsUnique();
        builder.HasCheckConstraint("CK_Users_PendingReservedBytes_NonNegative", "PendingReservedBytes >= 0");
    }
}

internal sealed class AvatarUploadConfiguration : IEntityTypeConfiguration<AvatarUpload>
{
    public void Configure(EntityTypeBuilder<AvatarUpload> builder)
    {
        builder.ToTable("AvatarUploads");
        builder.HasKey(x => x.UploadId);
        builder.Property(x => x.ClientRequestId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.StagingBlobPath).HasMaxLength(400).IsRequired();
        builder.Property(x => x.CanonicalBlobPath).HasMaxLength(400).IsRequired();
        builder.Property(x => x.CanonicalETag).HasMaxLength(200);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.OwnerUserId, x.Status, x.UploadExpiresUtc }).HasDatabaseName("IX_AvatarUploads_Owner_Status_Expires");
        builder.HasIndex(x => new { x.OwnerUserId, x.ClientRequestId }).IsUnique().HasDatabaseName("UQ_AvatarUploads_Owner_ClientRequestId");
        builder.HasIndex(x => x.OwnerUserId).HasFilter("Status IN (0, 1, 2, 3)").IsUnique().HasDatabaseName("UQ_AvatarUploads_OneLivePerOwner");
        builder.HasCheckConstraint("CK_AvatarUploads_ReservedBytes_Positive", "ReservedBytes > 0");
        builder.HasCheckConstraint("CK_AvatarUploads_Requested_Within_Reserved", "RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes");
        builder.HasCheckConstraint("CK_AvatarUploads_Status_Range", "Status BETWEEN 0 AND 5");
        builder.HasCheckConstraint("CK_AvatarUploads_RejectedUtc_Matches_Status", "(Status = 5 AND RejectedUtc IS NOT NULL) OR (Status <> 5 AND RejectedUtc IS NULL)");
        builder.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DataPointChainConfiguration : IEntityTypeConfiguration<DataPointChain>
{
    public void Configure(EntityTypeBuilder<DataPointChain> builder)
    {
        builder.ToTable("DataPointChains");
        builder.HasKey(x => x.ChainId);
        builder.Property(x => x.Title).HasMaxLength(200);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DataPointConfiguration : IEntityTypeConfiguration<DataPoint>
{
    public void Configure(EntityTypeBuilder<DataPoint> builder)
    {
        builder.ToTable("DataPoints");
        builder.HasKey(x => x.DataPointId);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.EventDate).IsRequired();
        builder.Property(x => x.Location).HasColumnType("geography").IsRequired();
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.UpdatedUtc).IsRequired(false);
        builder.Property(x => x.IsDeleted).HasDefaultValue(false);
        builder.HasCheckConstraint("CK_DataPoints_Location_SRID", "Location.STSrid = 4326");
        builder.HasIndex(x => new { x.ChainId, x.EventDate, x.DataPointId }).HasFilter("IsDeleted = 0 AND ChainId IS NOT NULL").HasDatabaseName("IX_DataPoints_Chain_EventDate").IsDescending(false, true, true);
        builder.HasIndex(x => new { x.UserId, x.CreatedUtc, x.DataPointId }).HasFilter("IsDeleted = 0").HasDatabaseName("IX_DataPoints_User_CreatedUtc").IsDescending(false, true, true);
        builder.HasCheckConstraint("CK_DataPoints_Location_SRID", "Location.STSrid = 4326");
        builder.HasCheckConstraint("CK_DataPoints_Visibility", "Visibility IN (0, 1, 2)");
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Chain).WithMany().HasForeignKey(x => x.ChainId).OnDelete(DeleteBehavior.NoAction);
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.HasKey(x => x.TagId);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.IsSystem).HasDefaultValue(false);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => x.NormalizedName).IsUnique();
        builder.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DataPointTagConfiguration : IEntityTypeConfiguration<DataPointTag>
{
    public void Configure(EntityTypeBuilder<DataPointTag> builder)
    {
        builder.ToTable("DataPointTags");
        builder.HasKey(x => new { x.DataPointId, x.TagId });
        builder.HasOne(x => x.DataPoint).WithMany().HasForeignKey(x => x.DataPointId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UserConsentConfiguration : IEntityTypeConfiguration<UserConsent>
{
    public void Configure(EntityTypeBuilder<UserConsent> builder)
    {
        builder.ToTable("UserConsents");
        builder.HasKey(x => x.ConsentId);
        builder.Property(x => x.PolicyVersion).HasMaxLength(50).IsRequired();
        builder.Property(x => x.RecordedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => new { x.UserId, x.Purpose, x.PolicyVersion }).IsUnique();
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class GpsFixConfiguration : IEntityTypeConfiguration<GpsFix>
{
    public void Configure(EntityTypeBuilder<GpsFix> builder)
    {
        builder.ToTable("GpsFixes");
        builder.HasKey(x => x.GpsFixId);
        builder.Property(x => x.Location).HasColumnType("geography").IsRequired();
        builder.HasCheckConstraint("CK_GpsFixes_Location_SRID", "Location.STSrid = 4326");
        builder.HasIndex(x => x.ExpiresUtc).HasDatabaseName("IX_GpsFixes_Expires");
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ErasedSubjectHashConfiguration : IEntityTypeConfiguration<ErasedSubjectHash>
{
    public void Configure(EntityTypeBuilder<ErasedSubjectHash> builder)
    {
        builder.ToTable("ErasedSubjectHashes");
        builder.HasKey(x => x.SubjectHash);
        builder.Property(x => x.SubjectHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.KeyVersion).IsRequired();
        builder.Property(x => x.ErasedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => x.KeyVersion).HasDatabaseName("IX_ErasedSubjectHashes_KeyVersion");
    }
}

internal sealed class UserErasureRequestConfiguration : IEntityTypeConfiguration<UserErasureRequest>
{
    public void Configure(EntityTypeBuilder<UserErasureRequest> builder)
    {
        builder.ToTable("UserErasureRequests");
        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.StatusReceiptHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.RequestedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.FailureCode).HasMaxLength(100);
    }
}

internal sealed class UserExportRequestConfiguration : IEntityTypeConfiguration<UserExportRequest>
{
    public void Configure(EntityTypeBuilder<UserExportRequest> builder)
    {
        builder.ToTable("UserExportRequests");
        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.DownloadReceiptHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.DownloadBlobPath).HasMaxLength(400);
        builder.Property(x => x.RequestedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.FailureCode).HasMaxLength(100);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class MediaConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> builder)
    {
        builder.ToTable("Media");
        builder.HasKey(x => x.MediaId);
        builder.Property(x => x.ClientRequestId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.StagingBlobPath).HasMaxLength(400).IsRequired();
        builder.Property(x => x.CanonicalBlobPath).HasMaxLength(400).IsRequired();
        builder.Property(x => x.CanonicalETag).HasMaxLength(200);
        builder.Property(x => x.ThumbnailPath).HasMaxLength(400);
        builder.Property(x => x.SortOrder).HasDefaultValue(0);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => x.UploadId).IsUnique();
        builder.HasIndex(x => new { x.OwnerUserId, x.DataPointId, x.ClientRequestId }).IsUnique();
        builder.HasIndex(x => new { x.DataPointId, x.CanonicalBlobPath }).IsUnique();
        builder.HasCheckConstraint("CK_Media_Requested_Within_Reserved", "RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes");
        builder.HasCheckConstraint("CK_Media_RejectedUtc_Matches_Status", "(Status = 5 AND RejectedUtc IS NOT NULL) OR (Status <> 5 AND RejectedUtc IS NULL)");
        builder.HasIndex(x => new { x.Status, x.CreatedUtc, x.MediaId }).HasDatabaseName("IX_Media_Status_CreatedUtc");
        builder.HasOne(x => x.DataPoint).WithMany().HasForeignKey(x => x.DataPointId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(x => x.OutboxId);
        builder.Property(x => x.Type).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DedupKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.Attempts).HasDefaultValue(0);
        builder.HasIndex(x => x.DedupKey).IsUnique();
        builder.HasIndex(x => new { x.CreatedUtc, x.OutboxId }).HasFilter("ProcessedUtc IS NULL").HasDatabaseName("IX_OutboxMessages_Unprocessed");
    }
}

internal sealed class FollowConfiguration : IEntityTypeConfiguration<Follow>
{
    public void Configure(EntityTypeBuilder<Follow> builder)
    {
        builder.ToTable("Follows");
        builder.HasKey(x => new { x.FollowerId, x.FolloweeId });
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => new { x.FolloweeId, x.Status, x.CreatedUtc, x.FollowerId }).HasDatabaseName("IX_Follows_Followee_Status_CreatedUtc");
        builder.HasCheckConstraint("CK_Follows_NotSelf", "FollowerId <> FolloweeId");
        builder.HasOne(x => x.Follower).WithMany().HasForeignKey(x => x.FollowerId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Followee).WithMany().HasForeignKey(x => x.FolloweeId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("Comments");
        builder.HasKey(x => x.CommentId);
        builder.Property(x => x.Text).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false);
        builder.HasOne(x => x.DataPoint).WithMany().HasForeignKey(x => x.DataPointId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.ParentComment).WithMany().HasForeignKey(x => x.ParentCommentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

internal sealed class ReactionConfiguration : IEntityTypeConfiguration<Reaction>
{
    public void Configure(EntityTypeBuilder<Reaction> builder)
    {
        builder.ToTable("Reactions");
        builder.HasKey(x => x.ReactionId);
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => new { x.DataPointId, x.UserId }).IsUnique();
        builder.HasOne(x => x.DataPoint).WithMany().HasForeignKey(x => x.DataPointId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("Reports");
        builder.HasKey(x => x.ReportId);
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasCheckConstraint("CK_Reports_OneTarget", "(DataPointId IS NOT NULL AND CommentId IS NULL) OR (DataPointId IS NULL AND CommentId IS NOT NULL)");
        builder.HasIndex(x => new { x.Status, x.CreatedUtc, x.ReportId }).HasDatabaseName("IX_Reports_Status_CreatedUtc");
        builder.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.DataPoint).WithMany().HasForeignKey(x => x.DataPointId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Comment).WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.NoAction);
    }
}

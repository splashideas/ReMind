# ReMind Architecture Design

## Overview

ReMind is a map-based social platform that lets users pin historical data points (text, dates, photos, videos, tags) to real-world locations, link related events into location chains/timelines, and explore history on a map. Users interact through a React web app or a React Native mobile app (iOS/Android). The backend is a .NET API on Azure, using Azure SQL spatial types for proximity queries, Azure Maps for place geocoding, and Microsoft Entra External ID for authentication.

This document is the **implementation contract** for the redesign: requirements here must be internally consistent, secure by default, and complete enough to decompose into work items without re-litigating core decisions.

### Document map

| § | Section | Purpose |
|---|---|---|
| — | Current vs target | Migration baseline |
| 1 | Solution restructuring | Project layout and retirement decisions |
| 2 | Spatial data model | Schema, chains, tags, supporting entities |
| 3 | EF Core + NetTopologySuite | Data access, identity, query/cursor patterns, migrations |
| 4 | Authentication & authorization | Entra External ID, policies, visibility matrix |
| 5 | API design | Endpoints and request contracts |
| 6 | Media storage | Upload, quota, malware, thumbnails, CDN |
| 7 | Data point UX | Product create/search/timeline behavior |
| 8 | Operational defaults | Numeric bounds and TTLs used across §2–§7 |
| 9 | Social features | Follows, comments, reactions, feed, reporting |
| 10 | Infrastructure (Terraform) | Azure resource deltas |
| 11 | DevOps & testing | CI and test strategy |
| 12 | Security & compliance | Privacy, app, and network controls |
| 13 | Implementation phases | Rollout order |

## Current State vs. Target

| Aspect | Current | Target |
|---|---|---|
| Frontend | ASP.NET Core Razor Pages (`ReMind.Frontend`) | React SPA + React Native mobile app |
| API | Azure Functions with function-key auth | ASP.NET Core Web API on App Service (P1v3) with JWT |
| Database | `dbo.DataPoints` with `Location NVARCHAR(200)` | Azure SQL with `GEOGRAPHY` + spatial index, chains, tags |
| Data Access | Raw `SqlConnection` + inline SQL | EF Core + `Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite` |
| Auth | Function keys | Microsoft Entra External ID (JWT bearer tokens) |
| Create UX | Simple text location form | Map pin or GPS, historical date, visibility, tags, chain association with selectable radius |
| Search UX | None | Nearby radius, place/address geocode, map+list, viewport refine, chain timeline |
| Media | None | Azure Blob Storage + Front Door; signed reads; outbox + idempotent thumbnails |
| Social | None | User profiles, follows, comments, reactions, moderation |

## 1. Solution Restructuring

```
ReMind.sln
├── src/
│   ├── ReMind.Web/            # React SPA (replaces ReMind.Frontend)
│   ├── ReMind.Mobile/         # React Native app (iOS/Android)
│   ├── ReMind.Api/            # ASP.NET Core Web API (replaces ReMind.Functions)
│   ├── ReMind.Core/           # Domain models, DTOs, shared contracts
│   └── ReMind.Data/           # EF Core DbContext, migrations, configurations
├── database/
│   └── ReMind.Database/       # SQL project (schema-as-code, if kept)
├── infra/
│   └── terraform/             # Updated IaC
└── tests/
    ├── ReMind.UnitTests/
    ├── ReMind.IntegrationTests/
    └── ReMind.PlaywrightTests/
```

**Decided (do not reopen during Phase 1 scaffolding):**
- **Schema source of truth**: EF Core migrations in `ReMind.Data`. Retire `ReMind.Database` and the create-if-missing SQL path **before** the first database deployment (§3 Migration Strategy).
- **API host**: `ReMind.Api` (ASP.NET Core) replaces `ReMind.Functions` as the public HTTP API; any remaining Functions are **workers only** (thumbnails, outbox dispatcher, janitors)—not a second public API surface.
- **Web host**: `ReMind.Web` (React SPA) replaces `ReMind.Frontend` (Razor) as the web client. Keep Razor/`ReMind.Functions` HTTP triggers only until cutover, then remove them from deploy and CI.
- **Mobile**: `ReMind.Mobile` (React Native) is in-repo from Pre-Phase 1; feature parity lands in Phase 4.

## 2. Spatial Data Model

Core tables appear as DDL below. Supporting entities required for auth, media, privacy, and social features are specified immediately after (full column contracts). Implementers must materialize **all** of §2 in the initial EF model/migration—not only the DDL block.

### Core tables (DDL)

```sql
CREATE TABLE dbo.Users (
    UserId                UNIQUEIDENTIFIER PRIMARY KEY,
    Issuer                NVARCHAR(200) NOT NULL,
    ExternalSubject       NVARCHAR(200) NOT NULL,
    DisplayName           NVARCHAR(200) NOT NULL,
    AvatarBlobPath        NVARCHAR(400) NULL, -- server-set sealed path only; never accept client-supplied paths on profile update
    -- Live avatar upload state lives in dbo.AvatarUploads (not a single Users.AvatarUploadId pointer):
    -- a second replacement must not overwrite the only reservation identity while the first remains charged.
    PendingReservedBytes  BIGINT NOT NULL DEFAULT 0, -- atomic pending-upload quota counter (data-point media + avatar)
    Bio                   NVARCHAR(1000) NULL,
    CreatedUtc            DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    DeletedUtc            DATETIME2(7) NULL,
    CONSTRAINT UQ_Users_Issuer_ExternalSubject UNIQUE (Issuer, ExternalSubject),
    CONSTRAINT CK_Users_PendingReservedBytes_NonNegative CHECK (PendingReservedBytes >= 0)
);

-- Dedicated avatar-upload reservation rows (required — Users.AvatarUploadId alone cannot hold
-- ClientRequestId, ReservedBytes, expiry, status, or staging/canonical paths under concurrent replace).
-- AvatarUploads.Status: 0=PendingUpload 1=Quarantined 2=Processing 3=PendingModeration 4=Ready 5=Rejected
-- Non-terminal (live/charged) statuses are 0–3; Ready and Rejected are terminal.
CREATE TABLE dbo.AvatarUploads (
    UploadId              UNIQUEIDENTIFIER PRIMARY KEY,
    OwnerUserId           UNIQUEIDENTIFIER NOT NULL,
    ClientRequestId       NVARCHAR(100) NOT NULL, -- idempotency key unique per owner while reservation is live
    Status                TINYINT NOT NULL, -- 0=PendingUpload 1=Quarantined 2=Processing 3=PendingModeration 4=Ready 5=Rejected
    ReservedBytes         BIGINT NOT NULL, -- photo per-type max charged against Users.PendingReservedBytes
    RequestedSizeBytes    BIGINT NOT NULL, -- client expected size only (<= ReservedBytes)
    StagingBlobPath       NVARCHAR(400) NOT NULL,
    CanonicalBlobPath     NVARCHAR(400) NOT NULL,
    UploadExpiresUtc      DATETIME2(7) NOT NULL,
    CreatedUtc            DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedUtc          DATETIME2(7) NULL,
    MalwareScanVerdict    TINYINT NULL, -- 0=Clean 1=Malicious 2=Error/Timeout
    MalwareScannedUtc     DATETIME2(7) NULL,
    QuarantineReviewByUtc DATETIME2(7) NULL,
    RowVersion            ROWVERSION NOT NULL, -- concurrency token so replace cannot clobber an in-flight reservation silently
    CONSTRAINT FK_AvatarUploads_Users FOREIGN KEY (OwnerUserId)
        REFERENCES dbo.Users (UserId)
        ON DELETE NO ACTION,
    CONSTRAINT UQ_AvatarUploads_Owner_ClientRequestId UNIQUE (OwnerUserId, ClientRequestId),
    CONSTRAINT CK_AvatarUploads_ReservedBytes_Positive CHECK (ReservedBytes > 0),
    CONSTRAINT CK_AvatarUploads_Requested_Within_Reserved CHECK (RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes),
    CONSTRAINT CK_AvatarUploads_Status_Range CHECK (Status BETWEEN 0 AND 5)
);

CREATE INDEX IX_AvatarUploads_Owner_Status_Expires
ON dbo.AvatarUploads (OwnerUserId, Status, UploadExpiresUtc);

-- Database-enforced one-live-reservation invariant (non-terminal Status 0–3 only).
-- Init/replace still runs under a lock/serializable recheck so cancel/release/debit/insert cannot race the filtered unique index.
CREATE UNIQUE INDEX UQ_AvatarUploads_OneLivePerOwner
ON dbo.AvatarUploads (OwnerUserId)
WHERE Status IN (0, 1, 2, 3);

CREATE TABLE dbo.DataPointChains (
    ChainId         UNIQUEIDENTIFIER PRIMARY KEY,
    Title           NVARCHAR(200) NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_DataPointChains_Users FOREIGN KEY (CreatedByUserId)
        REFERENCES dbo.Users (UserId)
        ON DELETE NO ACTION
);

CREATE TABLE dbo.DataPoints (
    DataPointId     BIGINT IDENTITY(1,1) PRIMARY KEY,
    UserId          UNIQUEIDENTIFIER NOT NULL,
    ChainId         UNIQUEIDENTIFIER NULL,
    Title           NVARCHAR(200) NOT NULL,
    Description     NVARCHAR(4000) NOT NULL,
    EventDate       DATETIMEOFFSET NOT NULL, -- may be historical (past) or near-present
    Location        GEOGRAPHY NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedUtc      DATETIME2(7) NULL,
    Visibility      TINYINT NOT NULL,  -- 0=Public, 1=FollowersOnly, 2=Private; no DB default — API DTO/deserializer must also require presence on create (for example, a required nullable enum so omitted JSON cannot bind to 0)
    IsDeleted       BIT NOT NULL DEFAULT 0,
    CONSTRAINT CK_DataPoints_Location_SRID CHECK (Location.STSrid = 4326),
    CONSTRAINT CK_DataPoints_Visibility CHECK (Visibility IN (0, 1, 2)),
    CONSTRAINT FK_DataPoints_Users FOREIGN KEY (UserId)
        REFERENCES dbo.Users (UserId)
        ON DELETE NO ACTION,
    CONSTRAINT FK_DataPoints_Chains FOREIGN KEY (ChainId)
        REFERENCES dbo.DataPointChains (ChainId)
        ON DELETE NO ACTION
);

CREATE SPATIAL INDEX IX_DataPoints_Location
ON dbo.DataPoints (Location)
USING GEOGRAPHY_AUTO_GRID;

CREATE INDEX IX_DataPoints_Chain_EventDate
ON dbo.DataPoints (ChainId, EventDate DESC, DataPointId DESC)
WHERE IsDeleted = 0 AND ChainId IS NOT NULL;

CREATE INDEX IX_DataPoints_User_CreatedUtc
ON dbo.DataPoints (UserId, CreatedUtc DESC, DataPointId DESC)
WHERE IsDeleted = 0;

CREATE TABLE dbo.Tags (
    TagId            UNIQUEIDENTIFIER PRIMARY KEY,
    Name             NVARCHAR(100) NOT NULL,
    NormalizedName   NVARCHAR(100) NOT NULL,
    IsSystem         BIT NOT NULL DEFAULT 0, -- predetermined catalog when 1
    CreatedByUserId  UNIQUEIDENTIFIER NULL,
    CreatedUtc       DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Tags_NormalizedName UNIQUE (NormalizedName),
    CONSTRAINT FK_Tags_Users FOREIGN KEY (CreatedByUserId)
        REFERENCES dbo.Users (UserId)
        ON DELETE NO ACTION
);

CREATE TABLE dbo.DataPointTags (
    DataPointId BIGINT NOT NULL,
    TagId       UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT PK_DataPointTags PRIMARY KEY (DataPointId, TagId),
    CONSTRAINT FK_DataPointTags_DataPoints FOREIGN KEY (DataPointId)
        REFERENCES dbo.DataPoints (DataPointId)
        ON DELETE CASCADE,
    CONSTRAINT FK_DataPointTags_Tags FOREIGN KEY (TagId)
        REFERENCES dbo.Tags (TagId)
        ON DELETE NO ACTION
);

-- Privacy / identity (required for GPS consent and post-erasure guarantees)
CREATE TABLE dbo.UserConsents (
    ConsentId     UNIQUEIDENTIFIER PRIMARY KEY,
    UserId        UNIQUEIDENTIFIER NOT NULL,
    Purpose       TINYINT NOT NULL, -- 0=LocationGps
    PolicyVersion NVARCHAR(50) NOT NULL,
    Granted       BIT NOT NULL,
    RecordedUtc   DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    ClientUtc     DATETIMEOFFSET NULL,
    RevokedUtc    DATETIME2(7) NULL,
    CONSTRAINT UQ_UserConsents_User_Purpose_PolicyVersion UNIQUE (UserId, Purpose, PolicyVersion),
    CONSTRAINT FK_UserConsents_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

CREATE TABLE dbo.GpsFixes (
    GpsFixId         UNIQUEIDENTIFIER PRIMARY KEY,
    UserId           UNIQUEIDENTIFIER NOT NULL,
    Location         GEOGRAPHY NOT NULL,
    CapturedUtc      DATETIME2(7) NOT NULL,
    AccuracyMeters   FLOAT NULL,
    ExpiresUtc       DATETIME2(7) NOT NULL,
    CONSTRAINT CK_GpsFixes_Location_SRID CHECK (Location.STSrid = 4326),
    CONSTRAINT FK_GpsFixes_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

CREATE INDEX IX_GpsFixes_Expires ON dbo.GpsFixes (ExpiresUtc);

-- Versioned HMAC-SHA-256 of Issuer || 0x00 || ExternalSubject using SubjectPseudonymKey[v] from Key Vault (never stored in SQL);
-- retained after Users scrub so the same IdP subject cannot JIT-reprovision and undo erasure. Plain SHA-256 is not sufficient:
-- enumerable provider subjects would allow offline dictionary attacks after a DB leak.
CREATE TABLE dbo.ErasedSubjectHashes (
    SubjectHash   BINARY(32) PRIMARY KEY, -- HMAC-SHA-256 output for KeyVersion
    KeyVersion    INT NOT NULL,           -- version of SubjectPseudonymKey used to compute SubjectHash
    ErasedUtc     DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    ErasureRequestId UNIQUEIDENTIFIER NOT NULL
);

CREATE INDEX IX_ErasedSubjectHashes_KeyVersion
ON dbo.ErasedSubjectHashes (KeyVersion);

CREATE TABLE dbo.UserErasureRequests (
    RequestId          UNIQUEIDENTIFIER PRIMARY KEY,
    UserId             UNIQUEIDENTIFIER NULL, -- cleared when Users row is scrubbed
    Status             TINYINT NOT NULL, -- 0=Pending 1=Running 2=Completed 3=Failed
    StatusReceiptHash  BINARY(32) NOT NULL, -- SHA-256 of one-time opaque receipt; never store raw receipt
    RequestedUtc       DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    StartedUtc         DATETIME2(7) NULL,
    CompletedUtc       DATETIME2(7) NULL,
    FailureCode        NVARCHAR(100) NULL,
    AuditJson          NVARCHAR(MAX) NULL
);

CREATE TABLE dbo.UserExportRequests (
    RequestId            UNIQUEIDENTIFIER PRIMARY KEY,
    UserId               UNIQUEIDENTIFIER NOT NULL,
    Status               TINYINT NOT NULL,
    DownloadReceiptHash  BINARY(32) NOT NULL,
    DownloadExpiresUtc   DATETIME2(7) NULL,
    DownloadBlobPath     NVARCHAR(400) NULL,
    RequestedUtc         DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    StartedUtc           DATETIME2(7) NULL,
    CompletedUtc         DATETIME2(7) NULL,
    FailureCode          NVARCHAR(100) NULL,
    AuditJson            NVARCHAR(MAX) NULL,
    CONSTRAINT FK_UserExportRequests_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

-- Media (data-point attachments). Status: 0=PendingUpload 1=Quarantined 2=Processing 3=PendingModeration 4=Ready 5=Rejected
CREATE TABLE dbo.Media (
    MediaId               UNIQUEIDENTIFIER PRIMARY KEY,
    DataPointId           BIGINT NOT NULL,
    OwnerUserId           UNIQUEIDENTIFIER NOT NULL,
    UploadId              UNIQUEIDENTIFIER NOT NULL,
    ClientRequestId       NVARCHAR(100) NOT NULL,
    MediaType             TINYINT NOT NULL, -- 0=Photo 1=Video
    Status                TINYINT NOT NULL,
    ReservedBytes         BIGINT NOT NULL,
    RequestedSizeBytes    BIGINT NOT NULL,
    StagingBlobPath       NVARCHAR(400) NOT NULL,
    CanonicalBlobPath     NVARCHAR(400) NOT NULL,
    ThumbnailPath         NVARCHAR(400) NULL,
    SortOrder             INT NOT NULL DEFAULT 0,
    ModerationApprovedUtc DATETIME2(7) NULL,
    ThumbnailReadyUtc     DATETIME2(7) NULL,
    MalwareScanVerdict    TINYINT NULL, -- 0=Clean 1=Malicious 2=Error/Timeout
    MalwareScannedUtc     DATETIME2(7) NULL,
    QuarantineReviewByUtc DATETIME2(7) NULL,
    RejectedUtc           DATETIME2(7) NULL, -- set atomically when Status becomes Rejected; drives §8 rejected-retention purge
    UploadExpiresUtc      DATETIME2(7) NOT NULL,
    CreatedUtc            DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Media_UploadId UNIQUE (UploadId),
    CONSTRAINT UQ_Media_Owner_DataPoint_ClientRequest UNIQUE (OwnerUserId, DataPointId, ClientRequestId),
    CONSTRAINT UQ_Media_DataPoint_CanonicalPath UNIQUE (DataPointId, CanonicalBlobPath),
    CONSTRAINT CK_Media_Requested_Within_Reserved CHECK (RequestedSizeBytes > 0 AND RequestedSizeBytes <= ReservedBytes),
    CONSTRAINT CK_Media_RejectedUtc_Matches_Status CHECK (
        (Status = 5 AND RejectedUtc IS NOT NULL) OR (Status <> 5 AND RejectedUtc IS NULL)),
    CONSTRAINT FK_Media_DataPoints FOREIGN KEY (DataPointId) REFERENCES dbo.DataPoints (DataPointId) ON DELETE NO ACTION,
    CONSTRAINT FK_Media_Users FOREIGN KEY (OwnerUserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

-- Moderation/finalization workers poll by Status; include CreatedUtc for stable queue order.
CREATE INDEX IX_Media_Status_CreatedUtc
ON dbo.Media (Status, CreatedUtc, MediaId)
INCLUDE (DataPointId, OwnerUserId, UploadId, ThumbnailPath, MalwareScanVerdict, QuarantineReviewByUtc, RejectedUtc);

CREATE TABLE dbo.OutboxMessages (
    OutboxId     UNIQUEIDENTIFIER PRIMARY KEY,
    Type         NVARCHAR(100) NOT NULL,
    DedupKey     NVARCHAR(200) NOT NULL,
    Payload      NVARCHAR(MAX) NOT NULL,
    CreatedUtc   DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedUtc DATETIME2(7) NULL,
    Attempts     INT NOT NULL DEFAULT 0,
    CONSTRAINT UQ_OutboxMessages_DedupKey UNIQUE (DedupKey)
);

CREATE INDEX IX_OutboxMessages_Unprocessed
ON dbo.OutboxMessages (CreatedUtc, OutboxId)
INCLUDE (Type, DedupKey, Attempts)
WHERE ProcessedUtc IS NULL;

CREATE TABLE dbo.Follows (
    FollowerId   UNIQUEIDENTIFIER NOT NULL,
    FolloweeId   UNIQUEIDENTIFIER NOT NULL,
    Status       TINYINT NOT NULL, -- 0=Pending 1=Accepted 2=Rejected
    CreatedUtc   DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    RespondedUtc DATETIME2(7) NULL,
    CONSTRAINT PK_Follows PRIMARY KEY (FollowerId, FolloweeId),
    CONSTRAINT CK_Follows_NotSelf CHECK (FollowerId <> FolloweeId),
    CONSTRAINT FK_Follows_Follower FOREIGN KEY (FollowerId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION,
    CONSTRAINT FK_Follows_Followee FOREIGN KEY (FolloweeId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

-- Inbound follow-request and followee-side lookups filter by FolloweeId + Status; PK starts with FollowerId.
CREATE INDEX IX_Follows_Followee_Status_CreatedUtc
ON dbo.Follows (FolloweeId, Status, CreatedUtc, FollowerId);

CREATE TABLE dbo.Comments (
    CommentId       UNIQUEIDENTIFIER PRIMARY KEY,
    DataPointId     BIGINT NOT NULL,
    UserId          UNIQUEIDENTIFIER NOT NULL,
    Text            NVARCHAR(2000) NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    ParentCommentId UNIQUEIDENTIFIER NULL,
    IsDeleted       BIT NOT NULL DEFAULT 0,
    CONSTRAINT FK_Comments_DataPoints FOREIGN KEY (DataPointId) REFERENCES dbo.DataPoints (DataPointId) ON DELETE NO ACTION,
    CONSTRAINT FK_Comments_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION,
    CONSTRAINT FK_Comments_Parent FOREIGN KEY (ParentCommentId) REFERENCES dbo.Comments (CommentId) ON DELETE SET NULL
);

CREATE TABLE dbo.Reactions (
    ReactionId   UNIQUEIDENTIFIER PRIMARY KEY,
    DataPointId  BIGINT NOT NULL,
    UserId       UNIQUEIDENTIFIER NOT NULL,
    ReactionType TINYINT NOT NULL, -- 0=Like 1=Love (extensible)
    CreatedUtc   DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Reactions_DataPoint_User UNIQUE (DataPointId, UserId),
    CONSTRAINT FK_Reactions_DataPoints FOREIGN KEY (DataPointId) REFERENCES dbo.DataPoints (DataPointId) ON DELETE NO ACTION,
    CONSTRAINT FK_Reactions_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION
);

CREATE TABLE dbo.Reports (
    ReportId    UNIQUEIDENTIFIER PRIMARY KEY,
    ReporterId  UNIQUEIDENTIFIER NOT NULL,
    DataPointId BIGINT NULL,
    CommentId   UNIQUEIDENTIFIER NULL,
    Reason      NVARCHAR(500) NOT NULL,
    Status      TINYINT NOT NULL, -- 0=Open 1=Resolved 2=Dismissed
    CreatedUtc  DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Reports_OneTarget CHECK (
        (DataPointId IS NOT NULL AND CommentId IS NULL)
        OR (DataPointId IS NULL AND CommentId IS NOT NULL)),
    CONSTRAINT FK_Reports_Reporter FOREIGN KEY (ReporterId) REFERENCES dbo.Users (UserId) ON DELETE NO ACTION,
    CONSTRAINT FK_Reports_DataPoints FOREIGN KEY (DataPointId) REFERENCES dbo.DataPoints (DataPointId) ON DELETE NO ACTION,
    CONSTRAINT FK_Reports_Comments FOREIGN KEY (CommentId) REFERENCES dbo.Comments (CommentId) ON DELETE NO ACTION
);

-- Moderation queue filters open reports by Status.
CREATE INDEX IX_Reports_Status_CreatedUtc
ON dbo.Reports (Status, CreatedUtc, ReportId)
INCLUDE (ReporterId, DataPointId, CommentId);
```

### Supporting table notes

- **Users**: profile info, persisted external identity pair (`Issuer`, `ExternalSubject`) mapped from the validated token (`iss` + `oid`, or another explicitly linked provider subject when `oid` is unavailable), display name, server-owned `AvatarBlobPath` only (no single `AvatarUploadId` pointer on the user row), atomic `PendingReservedBytes` quota counter, bio; avatars are never written from a client-supplied path—only the caller-scoped avatar upload/complete flow may set them after validation. **JIT provisioning** must refuse when a versioned `HMAC-SHA-256(SubjectPseudonymKey[v], Issuer || 0x00 || ExternalSubject)` exists in `ErasedSubjectHashes` for any active key version `v` (HTTP 403 with a stable “account closed” code)—erasure is not complete if the same IdP subject can immediately recreate a profile. User erasure stays asynchronous and only deletes or scrubs the `Users` row after dependent SQL rows/blob artifacts (including avatar staging/canonical objects) are removed or reassigned in order (consents, follows, reactions, reports, media/data points/avatar uploads, comments on deleted data points, then any retained `DataPointChains.CreatedByUserId` / `Tags.CreatedByUserId` references reassigned to a single well-known non-authenticating system tombstone user row provisioned at deploy time—not a shared deleted end-user account). Comments that survive because their parent data point still remains visible are reassigned to that same system tombstone user before the live user row is removed.
- **AvatarUploads**: dedicated reservation entity per avatar init/replace (`UploadId`, `OwnerUserId`, `ClientRequestId`, `Status`, `ReservedBytes` = photo type max, `RequestedSizeBytes`, `StagingBlobPath`, `CanonicalBlobPath`, `UploadExpiresUtc`, `MalwareScanVerdict`, `MalwareScannedUtc`, `QuarantineReviewByUtc`, `RowVersion`). Concurrent replacements insert or refresh distinct rows under the unique (`OwnerUserId`, `ClientRequestId`) key and optimistic concurrency; overwriting one live pointer is forbidden so the janitor can always release the correct `ReservedBytes` for each charged reservation. At most one non-terminal pending reservation is active per user, enforced by serializing init/replace per owner in one transaction (lock the `Users` row, cancel/release prior pending avatar reservations, then debit/insert or refresh the replacement). Avatars require clean malware verdict before `Users.AvatarBlobPath` is set; the scan worker persists verdict/timeout state on this row (same contract as `Media`) so readiness and quarantine-hold expiry survive worker restarts; human content-moderation queue is **optional** for avatars (default: auto-ready after clean malware + signature validation) but data-point media still requires moderation approval before `Ready`.
- **UserConsents** / **GpsFixes**: `UserConsents` is a single mutable current-state row per (`UserId`, `Purpose`, `PolicyVersion`) via the unique key above; `POST /api/users/me/consents` upserts that row and updates `Granted` / `RecordedUtc` / `RevokedUtc` instead of appending a second competing row for the same tuple. GPS-assisted operations enforce the resulting non-revoked grant for the current policy version at mint **and** redeem; expired fixes are purged on the §8 cadence and always deleted during erasure.
- **ErasedSubjectHashes**: purpose-limited identity blocklist retained after `Users` scrub so receipt-only status and anti-reprovisioning both remain implementable without keeping live PII or matching JWTs to a tombstone subject. Hashes are **keyed HMAC-SHA-256** outputs (`SubjectPseudonymKey[v]` from Key Vault, never in the database); store `KeyVersion` with each row. On rotation, mint with the current version and, for JIT lookup, evaluate **every** `KeyVersion` still referenced by at least one `ErasedSubjectHashes` row. Because erased subjects are no longer available to re-hash, a referenced key version **must remain available indefinitely** (or until its rows are migrated under a redesign that can safely re-key without plaintext); a key must not be disabled merely because a rotation window elapsed, or scrubbed identities become indistinguishable from new users and anti-reprovisioning fails.
- **DataPointChains**: logical timeline grouping; membership is via `DataPoints.ChainId`. Empty chains **are** supported: only `CreatedByUserId` may add the first node without a visible-node proximity match; later nodes use normal association rules.
- **Tags** / **DataPointTags**: many-to-many labels; `IsSystem = 1` rows are the predetermined catalog (seeded), custom tags are user-created with unique normalized names.
- **Media**: column contract as in DDL (including `RejectedUtc` set atomically with `Status = Rejected`). `ReservedBytes` is the **per-type maximum** for abuse containment; client `requestedSizeBytes` is expected size only; concurrency-safe total lives on `Users.PendingReservedBytes`. A **hard** byte cap requires the size-enforcing upload proxy path; retained direct-to-Blob SAS for large uploads is documented only as bounded cleanup with sub-minute uncommitted-block inspection plus committed-oversize cleanup (§6), not as a strict pre-storage quota guarantee. Moderation/finalization workers select work via `IX_Media_Status_CreatedUtc`. Only an idempotent finalization step may promote data-point media to `Ready` after clean malware + thumbnail success + moderation approval.
- **OutboxMessages**: transactional outbox with unique `DedupKey` per upload/work type. The dispatcher polls unprocessed rows via filtered index `IX_OutboxMessages_Unprocessed` (`ProcessedUtc IS NULL`, ordered by `CreatedUtc`, `OutboxId`).
- **UserExportRequests** / **UserErasureRequests**: as in DDL. Exports: authenticated owner + download receipt. Erasure status: **receipt-only** against `StatusReceiptHash` (§12)—never JWT/`UserId` after scrub.
- **Comments**: `ParentCommentId` is a single-column FK with `ON DELETE SET NULL`; writes enforce same-`DataPointId` as parent transactionally; erased authors are anonymized/tombstoned so replies remain.
- **Reactions**: one current reaction per (`DataPointId`, `UserId`); new value replaces prior.
- **Follows**: `CK_Follows_NotSelf` rejects self-follow. `PUT /api/users/{id}/follow` creates/reopens `Pending` only; followee accept/reject; all visibility/feed queries use `Status = Accepted` only. Inbound pending lists and followee-side lookups use `IX_Follows_Followee_Status_CreatedUtc` and the cursor-paginated `GET /api/users/me/follow-requests` contract (§5/§8)—never an unbounded select.
- **Reports**: exactly one of `DataPointId` or `CommentId`; reporter must be allowed to view the target; missing and non-visible targets share the same not-found response.

### Chain Association Rules

- A **chain** is an ordered set of data points (by `EventDate DESC`, then `DataPointId DESC`) that share a `ChainId`.
- When creating a point at location *L* with association radius *R* (meters, user-selected), the API proposes:
  1. Existing chains that have **any node visible to the caller** within `STDistance(node.Location, L) <= R`; returned chain labels/counts are derived only from that visible subset and never reveal hidden members
  2. Standalone (unchained) visible data points within *R* that the caller **owns** and can merge into a **new** chain with the new point
- `chainId` and `linkToDataPointId` are mutually exclusive in create/update requests; supplying both is a validation error (HTTP 400).
- Joining an existing chain (`chainId`) attaches the new row’s `ChainId` when the caller can view at least one node in range (or is empty-chain creator adding the first node). Under default `READ COMMITTED`, a plain transaction is **not** enough for empty-chain first-node adds: two concurrent creator requests can both observe an empty chain and both bypass the proximity check. Require a **conditional claim** (for example `UPDATE DataPointChains SET … WHERE ChainId = @id AND NOT EXISTS (SELECT 1 FROM DataPoints WHERE ChainId = @id)` that affects exactly one row, or an equivalent serializable/row-lock recheck on the chain row) so only one request wins the first-node slot; losers re-evaluate association rules against the now-non-empty chain.
- **`linkToDataPointId` (MVP)**: allowed only when the caller **owns** that standalone data point; the server creates a new chain and attaches both points only after a **lock or serializable recheck** that the target is still standalone (`ChainId IS NULL`) and still owned by the caller. Under default `READ COMMITTED`, two concurrent requests can otherwise both observe the same standalone point and create different chains. Use a conditional claim such as updating the owned standalone row to the new `ChainId` only while `ChainId IS NULL` (0-row update → conflict/retry) inside one transaction with the new point insert. Cross-user “invite to chain” is **out of MVP** (no invitation tokens/endpoints)—do not implement a vague owner-approval path without a concrete API.
- Chain membership does not require identical coordinates—only proximity of at least one node within the chosen radius at link time (except empty-chain first node).
- Timeline reads for a chain return all non-deleted members the caller is allowed to see (visibility matrix), ordered by `EventDate DESC`, then `DataPointId DESC`.

### Tag Rules

- Predetermined (system) tags are seeded and immutable in name; clients list them from `GET /api/tags`.
- Users may attach multiple tags per data point (system and/or custom).
- Custom tags are created on demand (`NormalizedName` = trimmed, case-folded); concurrent creates collide on the unique index and return the existing tag (idempotent).
- Tag attach/detach is author-only for the data point.

### Spatial Design Decisions

- Use `GEOGRAPHY` (not `GEOMETRY`) for real-world lat/long with `STDistance` and `STIntersects`/containment-style predicates for bounds filtering
- SRID 4326 (WGS 84) — the GPS standard
- `DATETIMEOFFSET` for `EventDate` so historical dates retain the recorded UTC offset; if named time-zone rules/context are required later, store a separate IANA/Windows zone identifier alongside it; clients may set past event times explicitly on create/update
- Spatial index is critical for proximity query performance
- Association and search “near” radius is always caller-supplied within documented bounds (see §8 defaults: default 250 m, min 1 m, max 50_000 m)

## 3. EF Core + NetTopologySuite

### Package

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite" Version="10.0.0" />
<PackageReference Include="Microsoft.Identity.Web" Version="3.8.3" />
```

`Microsoft.Identity.Web` provides `AddMicrosoftIdentityWebApi` for JWT bearer validation. Pin package versions to the chosen .NET TFM at implementation time; the versions above are placeholders, not a mandate to use outdated builds.

### DbContext Setup

```csharp
builder.Services.AddDbContext<ReMindDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.UseNetTopologySuite()));

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<DataPoint>()
        .Property(d => d.Location)
        .HasColumnType("geography");

    // Soft-delete: default filters exclude IsDeleted rows; bypass only on explicit admin/moderation queries.
    modelBuilder.Entity<DataPoint>().HasQueryFilter(d => !d.IsDeleted);
    modelBuilder.Entity<Comment>().HasQueryFilter(c => !c.IsDeleted);
}
```

API errors use RFC 7807 Problem Details (`application/problem+json`) for 4xx/5xx with stable `type`/`code` values (validation, not-found, forbidden, account-closed, quota-exceeded). Route examples below call a central helper such as `ApiProblems.Validation(...)` that writes those stable fields through the app's Problem Details service/factory; do not return stack traces or internal IDs beyond what the contract allows.

Opaque pagination cursors are produced and verified with ASP.NET Data Protection (`IDataProtector`) or equivalent authenticated server-side handles: payload includes `callerUserId`, route, seek keys, and bound query inputs; set a short TTL (§8); reject tampered/expired tokens without leaking which field failed.

### Caller Identity

Never accept `userId` from the client request body or query string for authorization. Resolve the internal `Users.UserId` server-side from the validated JWT (`iss` + `oid` → the stable user identity key, or another explicitly linked provider key when `oid` is unavailable).

**JIT profile creation** (first authenticated request):
1. Load active `SubjectPseudonymKey` versions from Key Vault (current version plus any prior versions still in the rotation retention window). For each active version `v`, compute `subjectHash[v] = HMAC-SHA-256(SubjectPseudonymKey[v], UTF8(Issuer) || 0x00 || UTF8(ExternalSubject))`.
2. If `ErasedSubjectHashes` contains any `subjectHash[v]` (match on `SubjectHash`, optionally constrained by that row's `KeyVersion`), **do not** create a profile—return 403 `account-closed` (stable code). Erasure must not be reversible by signing in again with the same IdP subject. Do **not** use plain `SHA256(Issuer || 0x00 || ExternalSubject)`—provider subjects can be enumerable, so an unkeyed digest is only pseudonymous and is offline-attackable after a database leak.
3. Otherwise create the `Users` row as today.

**Erasure-status polls** must not provision a profile and must not require a live `Users` row. `GET /api/users/me/erasure-requests/{id}` (or the dedicated status route in §5) is **receipt-only**: authorize solely by constant-time compare of `SHA256(receipt)` to `UserErasureRequests.StatusReceiptHash` for that id. The receipt is a high-entropy bearer secret (§8), not proof of JWT ownership against a tombstone subject. Exempt this endpoint from the authenticated fallback policy (`AllowAnonymous` + receipt required) so status works after scrub even when the client still holds a JWT for a deleted profile; if a JWT is present it must **not** drive authorization or JIT. All visibility, ownership, and follow-graph checks use the server-resolved ID only when a live, non-closed profile exists.

**JSON presence validation for required enums**: non-nullable enums such as create/update `Visibility` must not rely on default `System.Text.Json` binding, because an omitted property otherwise binds to underlying `0` (`Public`). Require property presence at the request-contract boundary (for example: nullable enum + `[Required]`, explicit raw JSON property checks, or an equivalent custom binder/validator) so omission fails 400 before mapping to domain values.

### Proximity Query Example

```csharp
if (!double.IsFinite(latitude)
    || !double.IsFinite(longitude)
    || !double.IsFinite(radiusMeters)
    || latitude is < -90 or > 90
    || longitude is < -180 or > 180
    || radiusMeters is < 1 or > 50_000
    || pageSize is < 1 or > 100)
{
    return ApiProblems.Validation(
        code: "nearby.invalid-parameters",
        detail: "Latitude, longitude, radiusMeters, and pageSize must be within the documented bounds.");
}

// Resolve caller identity server-side from the validated token (iss + oid → the stable user identity key).
// Never accept currentUserId from the request body/query; clients must not supply caller identity.
Guid currentUserId = await userDirectory.GetCurrentUserIdAsync(HttpContext.User, cancellationToken);
if (request.Cursor is not null
    && (request.Cursor.DistanceMeters is null || request.Cursor.DataPointId is null))
{
    return ApiProblems.Validation(
        code: "nearby.invalid-cursor",
        detail: "Nearby cursor must include both distanceMeters and dataPointId.");
}
double? cursorDistance = request.Cursor?.DistanceMeters;
long? cursorDataPointId = request.Cursor?.DataPointId;
var userLocation = new Point(longitude, latitude) { SRID = 4326 };

// Keep the follow visibility test in SQL via a correlated EXISTS; do not materialize
// followee ids into application memory and feed them back through Contains(...).
// Only Accepted follows unlock FollowersOnly posts (Pending/Rejected never grant access).
var nearby = await db.DataPoints
    .Select(d => new
    {
        DataPoint = d,
        Distance = d.Location.Distance(userLocation)
    })
    .Where(x => !x.DataPoint.IsDeleted)
    .Where(x =>
        x.DataPoint.Visibility == Visibility.Public
        || x.DataPoint.UserId == currentUserId
        || (x.DataPoint.Visibility == Visibility.FollowersOnly
            && db.Follows.Any(f =>
                f.FollowerId == currentUserId
                && f.FolloweeId == x.DataPoint.UserId
                && f.Status == FollowStatus.Accepted)))
    .Where(x => x.Distance <= radiusMeters)
    .Where(x => cursorDistance == null
        || x.Distance > cursorDistance
        || (x.Distance == cursorDistance && x.DataPoint.DataPointId > cursorDataPointId))
    .OrderBy(x => x.Distance)
    .ThenBy(x => x.DataPoint.DataPointId)
    .Take(pageSize)
    // Keep the SQL-computed geography distance (meters) on the DTO so the next
    // (distance, DataPointId) cursor matches the database ordering boundary.
    // Do not recompute distance client-side with NetTopologySuite planar units.
    .Select(x => new NearbyDataPointDto
    {
        DataPoint = x.DataPoint,
        DistanceMeters = x.Distance
    })
    .ToListAsync(cancellationToken);

// nextCursor = nearby.Count == 0
//     ? null
//     : nearbyCursorProtector.Create(new NearbyCursorPayload(
//         route: "nearby",
//         callerUserId: currentUserId, // bind visibility-dependent cursors to the issuing caller
//         distanceMeters: nearby.Last().DistanceMeters,
//         dataPointId: nearby.Last().DataPoint.DataPointId,
//         latitude: latitude,
//         longitude: longitude,
//         radiusMeters: radiusMeters,
//         pageSize: pageSize))
// On redeem: reject the cursor unless payload.callerUserId == currentUserId.
```

Chain-candidate lookup for create uses the same visibility predicate (including `FollowStatus.Accepted` only) and `Distance <= associationRadiusMeters`, then materializes a heterogeneous candidate list of chain summaries plus standalone neighbors. Each chain candidate’s sort distance is the minimum visible-node distance within the radius; candidates are totally ordered by `(minVisibleDistanceMeters ASC, candidateKind ASC, stableId ASC)` where `candidateKind` is `chain=0` / `standalone=1` and `stableId` is `ChainId` or `DataPointId`. Pagination seeks with `>` on that ascending tuple via an opaque cursor bound to the issuing `callerUserId`, the same center/`gpsFixId`, radius, and page size—do not reuse the nearby `(distance, DataPointId)` leaf-point cursor. Timeline queries filter `ChainId == chainId`, apply visibility, and order by `EventDate DESC` / `DataPointId DESC` with a date cursor bound to the same caller—not distance.

### Migration Strategy

- **Selected approach (Option A)**: EF Core migrations are the schema source of truth.
- Treat the rollout as greenfield **only after** an environment inventory confirms that no shared/dev/prod environment has ever run the current Functions + create-if-missing SQL path. The repository and current-state table still describe a deployable `dbo.DataPoints` schema, so this check is mandatory before cutover.
- If any environment already contains `dbo.DataPoints` rows, stop and write a dedicated migration/data-disposition plan (mapping `Location NVARCHAR(200)` rows, missing `UserId`/`Title`, rollback, and cutover sequencing) before shipping the EF schema. Do not strand live rows behind a “no backfill” assumption.
- Before the first EF-backed deployment, remove the old create-if-missing SQL path and create the initial schema from the first EF Core migration/bundle.
- **Split spatial-index DDL from the initial schema migration.** Keep the first EF Core migration fully transactional (tables, constraints, non-spatial indexes only) so a crash before the migration-history row is recorded cannot leave half-applied unguarded `CREATE TABLE` statements that then fail on retry. Emit `CREATE SPATIAL INDEX IX_DataPoints_Location ...` only in a **subsequent** dedicated migration whose sole nontransactional command is that spatial-index statement via `migrationBuilder.Sql("""CREATE SPATIAL INDEX ...""", suppressTransaction: true)`: SQL Server rejects `CREATE SPATIAL INDEX` inside an explicit transaction, and EF Core migration SQL is transactional by default. That follow-on migration must be retry-safe (create only when `IX_DataPoints_Location` is absent, or an equivalent existence guard) so replaying the single nontransactional command is actually safe; do not mix `suppressTransaction: true` with unguarded table creates in the same migration.
- If the environment inventory proves there is still no deployed data, schema-only redesigns before first deployment may continue by updating the initial transactional migration/bundle (still keeping spatial-index DDL in its own subsequent migration).

## 4. Authentication & Authorization

### Entra External ID Setup

1. Create an Entra External ID tenant (separate from the existing Entra ID on the SQL server)
2. Register three applications:
   - **API**: expose the scopes and audience accepted by `ReMind.Api`
   - **React SPA**: MSAL.js with Authorization Code Flow + PKCE
   - **React Native**: MSAL or Expo AuthSession with PKCE
3. Configure social identity providers: Google, Apple, Facebook

### API Protection

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("CanViewFollowersOnly", p => p.AddRequirements(new CanViewFollowersOnlyRequirement()));
    options.AddPolicy("CanViewPrivate", p => p.AddRequirements(new CanViewPrivateRequirement()));
    options.AddPolicy("CanModerate", p => p.RequireRole("Admin"));
});
```

JWT validation alone is not enough: register authorization with a **fallback authenticated policy** so endpoints are not anonymously callable by default, and call `app.UseAuthentication();` plus `app.UseAuthorization();` before mapping endpoints/controllers so the fallback policy is enforced. Explicitly mark only the documented exceptions with `AllowAnonymous` (today: `GET /api/erasure-requests/{id}`, its legacy alias, and `GET /healthz/live`)—anonymous is never the default. `GET /healthz/live` must stay a narrow liveness probe (no JWT requirement, no user lookup/JIT profile creation, no user-scoped data) so Azure Front Door origin probes can call it while origin network restrictions still limit who can reach the app. Apply explicit endpoint policies for visibility decisions (`CanViewFollowersOnly` / `CanViewPrivate`, backed by resource-based authorization handlers that evaluate the caller against the target author/chain) and a separate admin-role policy for moderation routes (`CanModerate`, which is intentionally admin-only in this design). Map the stable caller identity (`iss` + `oid`, or another explicitly linked provider key when `oid` is unavailable) to `Users.UserId` inside the API boundary; do not trust client-supplied user IDs.

### Authorization Model

| Action | Rule |
|---|---|
| View public posts | Any authenticated user |
| View followers-only posts | Caller with an **Accepted** follow of the author (pending follow requests never unlock location posts) |
| Request to follow a user | Any authenticated user; `PUT /api/users/{id}/follow` creates/reopens `Follows.Status = Pending` only |
| Accept/reject follow request | Followee only (`PUT /api/users/me/follow-requests/{followerId}/accept` or `/reject`) |
| View private posts | Author only on normal read APIs; moderators use separate admin-only moderation endpoints rather than a visibility bypass on `/api/datapoints/{id}` or `/nearby` |
| Create post | Any authenticated user (GPS/location consent recorded when using device GPS) |
| Join an existing chain | Any authenticated user who can view at least one proposed chain node |
| Create a new chain from nearby standalone points | Caller may create the chain row (`CreatedByUserId`); `linkToDataPointId` may attach only a **caller-owned** standalone point (MVP). Cross-user chain invitations are out of scope until a concrete invite API is specified |
| Follow self | Rejected (`FollowerId <> FolloweeId`); `PUT /api/users/{id}/follow` where `{id}` is the caller returns 400 |
| Attach media to own post | Author only; upload reservation/media row stores `OwnerUserId` matching the parent data point owner |
| Edit/delete own post | Author only |
| Comment | Any authenticated user who can view the target post |
| React | Any authenticated user who can view the target post |
| Tag own post | Author only |
| Report | Any authenticated user who can view the target post/comment; nonexistent and non-visible targets both return the same not-found response so sequential IDs cannot be probed |
| Moderate | Admin role (custom claim) |
## 5. API Design

### Endpoints

```
GET    /healthz/live                    # **AllowAnonymous** liveness probe for Azure Front Door/App Service origin health only; returns no user/domain data, does not require JWT, and must not JIT-provision profiles
POST   /api/datapoints                  # Create: title, description (max 4,000 chars), eventDate (past allowed), required visibility, exactly one of mapLocation or gpsFixId, optional chainId XOR linkToDataPointId, associationRadiusMeters, tagIds[] + customTagNames[]
GET    /api/datapoints/nearby           # Proximity search around explicit map-selected coordinates (lat [-90,90], lng [-180,180], radiusMeters 1-50000 default 250, opaque route-specific cursor bound to /nearby + issuing callerUserId + the same center/radius/pageSize, pageSize default 25 max 100); visibility via JWT user + Accepted follow graph only, with private posts returned only when `DataPoints.UserId == currentUserId`
GET    /api/datapoints/nearby/gps       # Proximity search around a previously consent-validated gpsFixId; same opaque route-specific cursor/pageSize contract as /nearby (including callerUserId binding), the cursor is rejected if reused by a different caller or with a different fix, radius, or route, and private posts are returned only when `DataPoints.UserId == currentUserId`
GET    /api/datapoints/in-bounds        # Map viewport query (north/south/east/west, zoom, mode=clusters|points, viewportToken, cursor, pageSize default 200 max 500); same visibility rules; cluster pages use a stable cluster cursor, point pages use a stable leaf-point cursor, every visibility-dependent cursor binds callerUserId, and any viewport/mode/caller change invalidates the prior cursor
GET    /api/datapoints/search/place     # Geocode address/city/state/country via Azure Maps, then return the first bounded nearby/in-bounds result page plus suggested map bounds and continuation cursor (caller-bound)
GET    /api/datapoints/{id}             # Detail + Ready media + tags + visibility-filtered chain summary (only caller-visible neighbor counts / adjacent timeline cursors); private posts return only to the author on this route; FollowersOnly requires Accepted follow
PUT    /api/datapoints/{id}             # Update own data point (including visibility, eventDate, tags, optional chain relink within rules)
DELETE /api/datapoints/{id}             # Soft-delete own data point

GET    /api/datapoints/chain-candidates # Given explicit map-selected lat/lng + associationRadiusMeters, list joinable chains the caller can see plus nearby **caller-owned** standalone points (Accepted follows only for FollowersOnly chain nodes); total order is (minVisibleDistanceMeters ASC, candidateKind ASC, stableId ASC) with candidateKind chain=0/standalone=1 and stableId=ChainId or DataPointId; opaque cursor seeks > on that tuple and binds callerUserId + center/radius/pageSize (default 25 max 100)
GET    /api/datapoints/chain-candidates/gps # Same lookup around a previously consent-validated gpsFixId with the same heterogeneous ordering, caller-bound seek cursor, and pageSize contract; never accept raw device GPS coordinates on this route
GET    /api/chains/{chainId}/timeline   # Chain events ordered by EventDate DESC, DataPointId DESC; cursor binds callerUserId + (eventDate, dataPointId) and the seek predicate is < on that descending tuple; includes detail payload suitable for scrubbing a timeline
POST   /api/chains                      # Explicitly create an empty/titled chain (optional; only the creator may add the first node before any visible-node proximity check exists)
POST   /api/chains/{chainId}/datapoints # Add a new data point into an existing chain; request must include exactly one of mapLocation or gpsFixId and still satisfy association radius vs some visible node, except for the creator-only first-node add to an empty chain

GET    /api/tags                        # List system tags + caller's recent custom tags (search q= optional)
POST   /api/tags                        # Create custom tag (idempotent on NormalizedName)

POST   /api/datapoints/{id}/media       # Author only: requires a client-generated idempotency key plus requestedSizeBytes (and media type); create/refresh the caller-owned PendingUpload row with ReservedBytes set to the per-type maximum (requestedSizeBytes is expected size only and must be ≤ that max), expiry + canonical blob key, atomically debit Users.PendingReservedBytes by that type max via conditional counter update (not a SUM read / not the tiny client declaration), and return the same UploadId/SAS/ReservedBytes reservation without a second debit when that key is retried before expiry. If the product needs a hard byte cap before storage accepts the upload, route through the size-enforcing proxy; direct SAS remains the bounded-cleanup path for large uploads, not a strict staging-cap guarantee
POST   /api/datapoints/{id}/media/complete # Validate the existing PendingUpload row/blob against requestedSizeBytes/ReservedBytes/per-type max; failed validation transitions it to Quarantined/Rejected and decrements Users.PendingReservedBytes, successful validation in one SQL transaction moves it to Processing/PendingModeration as appropriate, releases or converts the reserved counter bytes, writes one deduplicated OutboxMessages row, and lets the retrying dispatcher publish queue work
POST   /api/datapoints/{id}/comments    # Add comment (caller must be allowed to view the post)
GET    /api/datapoints/{id}/comments    # List comments oldest-first; cursor = (createdUtc, commentId) and the seek predicate is > on that ascending tuple; pageSize default 50 max 100
POST   /api/datapoints/{id}/reactions   # Create or replace the caller's single current reaction for the post (caller must be allowed to view it)
DELETE /api/datapoints/{id}/reactions   # Remove caller's reaction

GET    /api/users/{id}                  # Get user profile (avatar returned as a short-lived signed read URL when AvatarBlobPath is set—never as a raw writable path)
PUT    /api/users/me                    # Update own profile fields only (display name, bio); must not accept AvatarBlobPath or any client-supplied blob path
POST   /api/users/me/avatar             # Caller only: init/replace avatar upload; requires client idempotency key + requestedSizeBytes (photo types only); creates/refreshes a row in dbo.AvatarUploads (not a Users.AvatarUploadId pointer) under avatars/{userId}/... with ClientRequestId, Status, ReservedBytes = photo per-type max, StagingBlobPath, CanonicalBlobPath, UploadExpiresUtc, and RowVersion; avatar init runs under a lock/serializable recheck plus a filtered unique "one active reservation per owner" invariant so concurrent requests cannot create two live charged rows, cancelling/releasing prior non-terminal reservations before debiting the surviving row; returns UploadId/SAS/ReservedBytes (same reservation on key retry); optional API-proxied upload path may replace direct SAS when a hard body-size limit is required
POST   /api/users/me/avatar/complete    # Validate the AvatarUploads pending blob, run the same malware/signature gates as media, seal to the canonical avatar path, set Users.AvatarBlobPath server-side from that row, release/convert reserved bytes on the AvatarUploads row, delete prior avatar objects after the new path is live, and schedule erasure/janitor cleanup of abandoned AvatarUploads reservations
POST   /api/users/me/gps-fixes          # Bind a freshly captured device GPS fix to the authenticated user after verifying current consent; returns short-lived gpsFixId
POST   /api/users/me/consents           # Upsert the single current location/GPS consent row for (caller, purpose, policyVersion); updates granted/revoked state and timestamps instead of appending a second competing row
GET    /api/users/me/consents           # Current consent state used to gate GPS-assisted create/search
POST   /api/users/me/export-requests    # Request a full user-data export job; response returns a one-time opaque download receipt in the body (shown once; client must store it out-of-band—never embed it in URLs or logs); server persists only `DownloadReceiptHash` (not the raw receipt) with `DownloadExpiresUtc` null until the archive is ready
GET    /api/users/me/export-requests/{id} # Authenticated owner checks durable job state; when Status is Ready and `DownloadExpiresUtc` is still in the future, the short-lived signed download URL is returned only if the client also presents the opaque download receipt via a non-URL channel (e.g. `X-Export-Download-Receipt` header)—never authorize download by path/request id or authenticated ownership alone
POST   /api/users/me/erasure-requests   # Authenticated: start async erasure; response returns a one-time opaque status receipt (≥256-bit entropy, shown once; client stores out-of-band—never URLs/logs); server persists only `StatusReceiptHash` and, on completion path, inserts `ErasedSubjectHashes` for the caller before scrubbing `Users`
GET    /api/erasure-requests/{id}       # **AllowAnonymous**, receipt-only status (preferred path; also accept legacy `/api/users/me/erasure-requests/{id}` as an alias with the same rules). Require opaque receipt via non-URL channel (`X-Erasure-Receipt` header or body). Authorize solely by constant-time compare of SHA-256(receipt) to `StatusReceiptHash` for `{id}`. Do not require JWT; ignore JWT for authZ; never JIT-provision; never authorize by path id or tombstone subject. Rate-limit by IP + request id (§8)
PUT    /api/users/{id}/follow           # Request to follow (creates/reopens Pending; rejects self-follow; does not grant FollowersOnly visibility until Accepted)
DELETE /api/users/{id}/follow           # Unfollow / cancel pending request
GET    /api/users/me/follow-requests    # Followee: list Pending inbound follow requests ordered by CreatedUtc ASC, FollowerId ASC; opaque cursor seeks > on that tuple and binds callerUserId (= followee) + Status=Pending + pageSize (default 25 max 100 per §8). Never return an unbounded list—any authenticated account can create a pending request
PUT    /api/users/me/follow-requests/{followerId}/accept # Followee: set Status=Accepted (only then may the follower view FollowersOnly posts)
PUT    /api/users/me/follow-requests/{followerId}/reject # Followee: set Status=Rejected (no visibility grant)

GET    /api/feed                        # Activity feed (Accepted followed users' posts only), newest-first; cursor binds callerUserId + (createdUtc, dataPointId) and the seek predicate is < on that descending tuple; pageSize default 25 max 100
POST   /api/reports                     # Report content (caller must be allowed to view the target; same not-found for missing and non-visible targets)
GET    /api/moderation/queue            # Admin: moderation queue (CanModerate policy)
POST   /api/moderation/datapoints/{id}/hide # Admin: hide/tombstone a reported data point and resolve linked open reports idempotently
POST   /api/moderation/comments/{id}/hide   # Admin: hide/tombstone a reported comment and resolve linked open reports idempotently
POST   /api/moderation/media/{mediaId}/approve # Admin: idempotently record moderation approval; a separate finalization step promotes media to Ready only after validation + thumbnail success are already complete
POST   /api/moderation/media/{mediaId}/reject  # Admin: idempotently transition PendingModeration or Quarantined media to Rejected, set RejectedUtc atomically, and start the §8 rejected-retention clock (hard-delete only after that window—not an immediate quarantine delete path)
POST   /api/moderation/reports/{reportId}/dismiss # Admin: dismiss an open report without hiding the target and record the status transition
```

### Create request contract (illustrative)

```json
{
  "title": "Market square, 1890",
  "description": "...",
  "eventDate": "1890-06-12T00:00:00+01:00",
  "visibility": "Public",
  "mapLocation": { "latitude": 52.52, "longitude": 13.405 },
  "gpsFixId": null,
  "associationRadiusMeters": 200,
  "chainId": null,
  "linkToDataPointId": 12345,
  "tagIds": ["..."],
  "customTagNames": ["Cobblestones"]
}
```

`visibility` is illustrative, not optional: request binding must reject a missing property rather than silently defaulting a non-nullable enum to `Public`.

Create accepts exactly one of `mapLocation` or a short-lived server-issued `gpsFixId`. `gpsFixId` values come only from `POST /api/users/me/gps-fixes`, which verifies the caller has a stored, non-revoked location consent for the current policy version before binding the coordinates to that user, and every later redeem of that handle re-checks that the same consent is still current/non-revoked before the server uses the GPS fix. Map-pin-only creates do not require device GPS consent but still treat coordinates as personal data under the privacy policy.
The request intentionally omits any client-declared `location.source` flag: the server derives GPS-vs-map-pin handling solely from `gpsFixId` versus `mapLocation`.
`visibility` must be a **required JSON member** at the request-binding layer; do not model it as a plain non-nullable enum property that silently binds missing input to `Public`/`0`. Use required-member JSON handling or an equivalent nullable + validation pattern so omission returns HTTP 400 before persistence, with the no-default database column acting only as defense in depth.

### Nearby search request contract (illustrative)

```http
GET /api/datapoints/nearby?lat=52.52&lng=13.405&radiusMeters=250&pageSize=25&cursor=opaque.signed.shortlived.token
```

`/nearby` is for explicit map-selected coordinates. GPS-assisted nearby search uses `GET /api/datapoints/nearby/gps?gpsFixId=...` with a short-lived `gpsFixId`, and redeeming that handle re-checks current GPS consent so revocation blocks later searches even before the handle expires. The continuation cursor is an opaque, server-issued, signed short-lived token (or equivalent authenticated server-stored handle); it binds the issuing caller's internal `UserId`, the route/search mode, the `(distanceMeters, dataPointId)` seek boundary, and the original bound inputs (map center or `gpsFixId`, radius, page size). The API rejects any cursor that is expired, forged, replayed by a different authenticated caller, or replayed with different route or bound inputs—visibility-dependent seek boundaries are never transferable across accounts.

### Design Notes

- Pagination is cursor-based (not OFFSET) for large result sets; every visibility-dependent cursor (nearby, nearby/gps, in-bounds, place search, chain-candidates, timeline, feed, comments on a visibility-gated post) is an opaque, signed short-lived token (or equivalent authenticated handle) that **binds and validates the issuing caller's internal `UserId`** in addition to route/search inputs—redeem rejects a cursor when `payload.callerUserId != currentUserId` so one account cannot replay another's visibility-filtered seek boundary and skip eligible rows. Nearby cursors also bind the route/search mode plus the `(distanceMeters, dataPointId)` seek boundary to the original center/`gpsFixId`, radius, and page size before applying the next seek; chain-candidate cursors are a separate opaque token that binds the same caller + center/`gpsFixId`, radius, and page size to the heterogeneous seek boundary `(minVisibleDistanceMeters, candidateKind, stableId)` with ascending order and seek `>` (chain distance = minimum visible-node distance; do not reuse the nearby leaf-point cursor); timeline cursors bind caller + `(eventDate, dataPointId)` with `EventDate DESC, DataPointId DESC` and seek `<`, feed cursors bind caller + `(createdUtc, dataPointId)` with `CreatedUtc DESC, DataPointId DESC` and seek `<`, comments cursors bind caller + `(createdUtc, commentId)` with `CreatedUtc ASC, CommentId ASC` and seek `>`, and empty pages return no continuation cursor
- Reject invalid coordinates/radius/page size/bounds with HTTP 400 before `Point(...)`, `STDistance`, and `Take(...)`
- Create/update validation treats `visibility` as a required JSON property (for example, a required nullable enum plus validator); the API must reject omitted/null values with Problem Details instead of letting model binding default the enum to `Public`
- Bounding-box pre-filter before `STDistance` for map viewport and place-search result windows; when a viewport crosses the antimeridian (`west > east`), split the longitude predicate into west→180 and -180→east ranges while keeping the same viewport token/cursor contract
- Place search: Azure Maps Search (or equivalent) geocodes the query string → center/bbox → same visibility-filtered spatial query; response includes `mapBounds` so the client can fit all returned points, plus bounded result items (`pageSize` + `cursor`) and clusters when the viewport is too broad for raw markers
- Viewport refine: client debounces `moveend`/`zoomend`, calls `/in-bounds`, replaces markers + list, and keeps following continuation cursors while the viewport is unchanged; `/in-bounds` explicitly paginates either `mode=clusters` with a stable `(clusterSortKey, clusterId)` cursor or `mode=points` with a stable `(eventDate, dataPointId)` cursor bound to the same `viewportToken`; narrowing the map narrows the query, while low-zoom/world-scale boxes return clusters or a capped page instead of an unbounded raw point list
- Comments and feed reads are also cursor-bounded so no single request materializes an arbitrarily large thread or followed-user history
- API/application-layer rate limiting via `AspNetCoreRateLimit` or gateway quotas; Azure Front Door WAF is complementary edge protection, not a substitute for per-user/per-token throttling
- All read paths (including `GET /nearby`, `GET /nearby/gps`, `/in-bounds`, place search, and `GET /api/datapoints/{id}`) evaluate visibility with the authenticated caller ID resolved from the JWT (not a client-supplied user id) plus **Accepted** follow relationships only (`Follows.Status = Accepted`; Pending/Rejected never unlock `FollowersOnly`); the private-post branch is an explicit owner-only predicate (`DataPoints.UserId == currentUserId`), and moderators use separate `CanModerate` routes instead of bypassing user-facing visibility checks
- `/nearby` responses retain the SQL-computed `DistanceMeters` so the server can build the next opaque cursor from `(distance, DataPointId)` without recomputing the boundary client-side
- Detail and chain-summary responses are visibility-filtered end to end: neighbor counts, adjacent timeline cursors, and chain-candidate summaries are computed only from members the caller may currently view and never reveal hidden/deleted nodes
- GPS-assisted create/search flows rely on server-issued `gpsFixId` handles, not on a client-declared `"source"` enum, as the enforcement boundary for consent
- OpenAPI/Swagger for API documentation
- SignalR hub for real-time map updates (post-MVP)

## 6. Media Storage

### Architecture

```
Client → API (request upload URL with requestedSizeBytes + media type)
       → API confirms the caller owns the target data point, requires requestedSizeBytes within the per-type max, and treats that declaration as expected size only.
        Abuse containment reserves the **per-type maximum** (not the possibly-tiny client declaration) as `ReservedBytes` on a PendingUpload media row while atomically debiting
        `Users.PendingReservedBytes` with a single conditional counter update
        (`UPDATE ... SET PendingReservedBytes = PendingReservedBytes + @typeMax WHERE UserId = @caller AND PendingReservedBytes + @typeMax <= @quota`;
        refuse when the update affects 0 rows)—do not rely on a `SUM(ReservedBytes)` read under default `READ COMMITTED` to enforce the cap—
        persists the caller-supplied idempotency key, returns the existing unexpired reservation (same UploadId/SAS/ReservedBytes) when that key is retried without a second debit, otherwise creates/refreshes the row
        with bounded expiry/retention plus canonical blob/thumbnail paths, then uses its managed identity to obtain a user-delegation key and mint a short-lived,
        create + write Blob SAS for that server-owned uploadId/blob key (SAS TTL measured in minutes, ≤ pending-row expiry)
       → Client uploads directly to Blob Storage at a SAS-writable staging key **or** through the size-enforcing API proxy when required by §6 decisions; chunked direct uploads apply `If-None-Match: *` on final `Put Block List` (and single-shot on `Put Blob`), not only on uncommitted `Put Block`s
       → **Committed oversize cleanup**: Event Grid / sub-minute reaper deletes staging objects whose committed length exceeds the reservation or per-type max and releases counter bytes—do not wait solely for client complete or long expiry
       → **Uncommitted-block abuse containment** (required for any create+write SAS path because `BlobCreated` and committed-length checks do not see staged blocks): if the product needs a **hard** cap before storage accepts bytes, route the upload through the size-enforcing API/proxy that tracks cumulative body bytes and hard-stops at `ReservedBytes`/per-type max. Retained direct SAS is acceptable only as a **bounded/reactive cleanup** path for large uploads: run a short-cadence uncommitted-block inspector for every staging key whose write SAS has not expired—including already-`Rejected` rows still inside SAS TTL—`Get Block List` with `blocklisttype=uncommitted` (or uncommitted+committed), sum block sizes, and if staged bytes exceed `ReservedBytes`/per-type max **or** the reservation/SAS is expired/abused, **delete the staging blob** (which discards uncommitted blocks) and mark the media or `AvatarUploads` row `Rejected` (set `RejectedUtc` on `Media`). Keep `PendingReservedBytes` charged until SAS/`UploadExpiresUtc` elapses so the caller cannot recreate blocks on the rejected key and obtain more reservations; release the counter only after write capability ends. Inspector cadence must be sub-minute while any write SAS for that key is still valid; do not rely on Azure’s multi-day uncommitted-block GC as the control. **Default policy**: avatars and photo single-shot uploads use (A) or single `Put Blob`; retained direct chunked SAS is limited to large-video-style bounded cleanup with short TTLs (§8), not a guaranteed strict quota
       → Client confirms upload → API validates the staging blob exists and matches expected ETag/size/hash/signature and does not exceed `requestedSizeBytes` / `ReservedBytes` / per-type max,
        then seals that exact validated version by conditionally copying the validated ETag to the server-write-only canonical blob key that
        workers and read URLs use, and in one SQL transaction updates the existing media row to Quarantined (manual-hold only with `QuarantineReviewByUtc`)
        or Processing/PendingModeration/Rejected as appropriate, and releases or converts `Users.PendingReservedBytes` according to the selected proxy/direct cleanup path,
        and, only on the first `PendingUpload` → `Processing` / `PendingModeration` transition, inserts exactly one transactional outbox row for successful malware-scan/thumbnail/moderation work keyed by `UploadId` + work type
       → Retrying outbox dispatcher publishes the queue message (at-least-once) until acknowledged
       → Microsoft Defender for Storage malware scanning (or an equivalent required scanner) publishes result events; a scan-result worker correlates them by blob path/`UploadId`, records `MalwareScanVerdict` + `MalwareScannedUtc` idempotently on `Media`/`AvatarUploads`, retries transient delivery/lookup failures, and marks `Error/Timeout` after the bounded timeout window when no clean verdict arrives
       → Expired `PendingUpload` rows/blobs (decrementing `Users.PendingReservedBytes` only once write SAS/`UploadExpiresUtc` has elapsed), expired `Quarantined` manual holds (forced to `Rejected` + `RejectedUtc`, not hard-deleted), and `Rejected` artifacts older than §8 retention from `RejectedUtc` are removed by a janitor that deletes both blob objects and media metadata
       → Queue/thumbnail workers must read that persisted malware verdict and obtain a clean result before any ImageSharp/FFmpeg parse; malicious results reject the upload, while missing/failed/timed-out scans keep the object non-Ready in `Quarantined` without parsing or serving it until a clean rescan or an operator reject via `POST /api/moderation/media/{mediaId}/reject`
       → Azure Function (queue trigger) generates thumbnail idempotently to the canonical path `media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`, updates media thumbnail fields, and records `ThumbnailReadyUtc`
       → Media.Status becomes Ready only in a separate idempotent finalization transaction after validation + clean malware verdict + thumbnail success + moderation approval are all recorded; until then it remains non-public
        (`PendingModeration`, `Quarantined`, or `Rejected` as appropriate), and read APIs / signed URLs only expose Ready media
        for callers allowed to see the parent data point
       → Front Door / signed URLs serve approved thumbnails and media from those canonical paths
```

### Decisions

- **Container structure**: SAS-writable staging originals `media/{userId}/{dataPointId}/staging/{uploadId}/{fileName}`, sealed canonical originals `media/{userId}/{dataPointId}/{uploadId}/{fileName}`, and thumbnails `media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg` (canonical paths derived from `UploadId`, not random names)
- **Allowed types**: JPEG, PNG, WebP, MP4, MOV (validate blob signatures/content server-side, not only client-supplied MIME + extension, before publishing); a clean malware-scan verdict from Defender-for-Storage + Event Grid orchestration is required before ImageSharp/FFmpeg thumbnail parsing or any publication step—malicious findings reject the upload, and failed or timed-out scans leave the media in a non-Ready quarantine/hold until a clean rescan or explicit operator rejection, never parsing or serving the object meanwhile
- **Malware scanning infrastructure**: provision Microsoft Defender for Storage malware scanning (or an equivalent scanner with the same guarantees) plus Event Grid delivery of scan results to a queue/topic consumed by a scan-result worker. Correlate results to `Media` / `AvatarUploads` by sealed blob path and `UploadId`, persist `MalwareScanVerdict`/`MalwareScannedUtc` idempotently, retry transient event-processing failures, and transition to `Error/Timeout` + quarantine/manual-hold when the configured scan timeout window elapses without a clean verdict
- **Max file size**: 50 MB photos, 500 MB videos
- **Upload quota enforcement**: Blob SAS cannot hard-cap object size, so client-declared `requestedSizeBytes` alone is only a **reactive** completion check—not hard abuse containment. Before minting a SAS the API therefore **reserves the per-type maximum** for the declared media type (photo 50 MB / video 500 MB, or the avatar photo max) as `Media.ReservedBytes` (or `AvatarUploads.ReservedBytes`)—not the possibly-tiny client declaration—and **atomically** debits that full per-type max against `Users.PendingReservedBytes` with one conditional update that only succeeds when `PendingReservedBytes + reservedMax <=` the configured pending-upload quota; a 0-row update is a hard refusal. `requestedSizeBytes` remains required (must be > 0 and ≤ that per-type max) as the expected size for completion validation and UX, but quota accounting always charges the type maximum so a client cannot reserve one byte and stream a much larger block blob under the pending cap. Do not enforce the cap by reading `SUM(ReservedBytes)` under default `READ COMMITTED`. Idempotent retries of the same live `ClientRequestId` return the existing reservation without a second counter debit. **`PendingReservedBytes` is not sufficient by itself against chunked-upload abuse**: a caller holding create+write SAS can issue many large `Put Block` requests and never call `Put Block List`; uncommitted blocks do **not** emit `BlobCreated`, are **not** covered by committed-length checks, and can remain stored for days under platform GC—so one 500 MB reservation could otherwise consume far more storage. A direct create+write SAS path therefore offers only **bounded/reactive cleanup**, not a strict hard byte ceiling before storage accepts the data. Routes that must promise a hard per-upload cap (especially avatars and any single-shot photo path) must use a **size-enforcing upload proxy/gateway** that counts cumulative request body bytes and rejects further writes at `ReservedBytes`/per-type max. Retained direct-to-Blob chunked SAS is acceptable only for flows that explicitly tolerate that bounded/reactive posture (for example large video), and then only with an **early uncommitted-block inspection/cleanup** job that, on a sub-minute cadence for every staging key whose user-delegation write SAS has not yet expired—including rows already transitioned to `Rejected`/`Revoked` while the SAS TTL remains—calls `Get Block List` (`blocklisttype=uncommitted`, and committed when present), sums staged block sizes, and when the sum exceeds `ReservedBytes`/per-type max—or the reservation is expired/revoked/rejected—**deletes the staging blob** (discarding uncommitted blocks) and transitions the media or `AvatarUploads` row to `Rejected` with `RejectedUtc` set atomically when not already rejected. **Do not release `Users.PendingReservedBytes` immediately on inspector reject while the already-issued write SAS is still valid**: keep the reservation charged and the key under cleanup until `UploadExpiresUtc` / SAS `se` elapses (or use a genuinely revocable/proxied upload path instead). Only after the SAS can no longer write may the janitor release the counter and drop residual staging data. Platform multi-day uncommitted-block garbage collection is **not** an accepted control. Additionally pair with (a) **immediate committed-blob cleanup**: Event Grid / storage listener on staging-blob create or a sub-minute reaper that deletes any staging object whose **committed** length exceeds the reservation or per-type max (and rejects the media/`AvatarUploads` row, releasing counter bytes) without waiting for client completion or the longer expiry janitor, (b) **short write-SAS TTL** (minutes, always ≤ `UploadExpiresUtc`) so abandoned staging windows close quickly, and (c) **storage-level limits**: container/account capacity alerts plus per-user rate limits on upload init and direct PUT bandwidth where available. Photos that fit in a single `Put Blob` should prefer single-shot upload so uncommitted-block staging is unnecessary; chunked `Put Block` remains only where required (large video) and then only under proxy or uncommitted-block inspection above. Completion still rejects blobs larger than `requestedSizeBytes`/`ReservedBytes`/per-type max, releases or converts the reserved amount by decrementing `Users.PendingReservedBytes` in the same transaction as the status transition, and the short-cadence janitor remains a backstop for expired pending rows/blobs
- **Upload identity**: the initial upload call is author-only and requires a client-generated `ClientRequestId` idempotency key unique per caller + data point while the reservation is live plus `requestedSizeBytes`/media type; the API persists that key and per-type-max `ReservedBytes` on the pending reservation, returns the same unexpired `UploadId`/SAS/`ReservedBytes` instead of creating a duplicate row or double-debiting quota when the key is retried, stores both the SAS-writable staging blob key and sealed canonical blob key/expiry on that reservation, and completion is idempotent on `UploadId`; terminal `Ready`/`Rejected` states are no-ops on retry, invalid validation outcomes transition directly to `Rejected` unless an operator explicitly places the upload in a time-bounded `Quarantined` manual hold, duplicate completion attempts must not enqueue duplicate work, oversize **committed** staging blobs are deleted immediately on create/detect, **uncommitted** staged bytes are bounded by the size-enforcing proxy or sub-minute uncommitted-block inspector (delete staging blob on oversize/expiry), expired pending uploads are reaped so abandoned blobs and uncommitted blocks do not accumulate, and rejected/quarantined blobs/metadata are retained only for a bounded audit/review window before janitor cleanup
- **Avatar uploads**: profile avatars prefer the **size-enforcing API proxy** (hard body-size limit) and may use direct-to-Blob SAS only under the same uncommitted-block inspector + committed-oversize cleanup rules as media; malware-scan, validation, per-type-max quota-counter, and signed-read rules apply via `POST /api/users/me/avatar` + `POST /api/users/me/avatar/complete` (photo types only; paths `avatars/{userId}/staging/{uploadId}/...` and sealed `avatars/{userId}/{uploadId}/...`). Reservation state is stored only in `dbo.AvatarUploads`—never as a lone `Users.AvatarUploadId`—so client idempotency key, reserved bytes, expiry, status, staging/canonical paths, and concurrency (`RowVersion`) survive concurrent replacements; a new init cancels prior non-terminal owner reservations and releases their counter bytes before debiting the new row. `PUT /api/users/me` never accepts `AvatarBlobPath` or other client blob paths—only completion may set `Users.AvatarBlobPath` after the new object is Ready. Replacing an avatar seals the new object first, then deletes the previous canonical blob (and any abandoned `AvatarUploads` rows for that user). Profile reads expose a short-lived signed read URL derived from the server path; user erasure and the media janitor delete pending and canonical avatar objects along with other user blobs
- **Media outbox**: record thumbnail/moderation work in the same SQL transaction as the media row; a retrying dispatcher publishes to the queue so a crash between commit and enqueue cannot leave media permanently unprocessed, and a unique outbox deduplication key (`UploadId` + work type) prevents duplicate work rows
- **Thumbnail worker**: queue delivery is at-least-once, so thumbnail blob paths must be deterministic (`media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`) across the producer, worker, and signed-read URL builder; the worker runs only after a clean malware-scan verdict and must refuse to parse or write when the user/media deleting lease/version is set; if the thumbnail already exists, treat that as success after verifying/upserting metadata, otherwise create it and then upsert metadata without duplicate side effects
- **Blob SAS**: mint user-delegation SAS tokens with the API managed identity (no retained storage account keys); uploads are constrained to a single staging blob path with create + write permissions so chunked `Put Block` / `Put Block List` uploads work, and with a **short TTL (minutes, ≤ pending-row expiry)** so write capability cannot outlive the reservation the uncommitted-block inspector and expiry janitor enforce. **Accidental-overwrite client hint (not a security boundary)**: clients (and any SDK wrapper) should send `If-None-Match: *` on the commit that creates the blob—`Put Block List` for chunked uploads and `Put Blob` for single-shot uploads—to reduce accidental self-overwrite. `If-None-Match` is supplied by the untrusted client and is **not** enforced by SAS permissions, so a malicious caller can omit it and overwrite the staging blob until the SAS expires; treat it only as an accidental-overwrite safeguard. The **security boundary** is: (1) a server-write-only **write-once canonical destination** sealed at completion via conditional copy of the exact validated staging ETag/version the API observed, and (2) malware-scan verdicts and Ready finalization accepted **only** for that sealed canonical ETag/version (correlate Defender/Event Grid results to the canonical path + recorded ETag/`UploadId`—never to a mutable staging object that may have been replaced after a stale clean scan). Uncommitted `Put Block` calls only stage blocks and do not create the blob. Because those same uncommitted blocks also bypass `BlobCreated`/committed-length quota checks, any create+write SAS path **must** run the §6 uncommitted-block inspector (or use a size-enforcing proxy instead of direct chunked SAS)—see **Upload quota enforcement**. Completion validates the staging blob's ETag/size/hash then conditionally seals that exact validated ETag by copying it to the server-write-only canonical blob key before any processing or publish step; workers/read URLs never use the staging object, and Key Vault remains only for secrets that cannot use identity-based access
- **Upload networking**: because web/mobile clients upload directly, the Blob service endpoint must remain publicly reachable for the upload container, but anonymous blob access stays disabled and Blob service CORS is configured in IaC for each allowed SPA origin plus the required `PUT`/preflight headers/methods—never wildcard origins—while SAS scope/TTL still restrict access to the intended blob path
- **CDN**: Azure Front Door Premium (`azurerm_cdn_frontdoor_*`) fronts media delivery; visibility-protected media/avatar routes are **not cacheable**, so configure those routes/origins with `Cache-Control: no-store` (or equivalent AFD no-cache policy) and forward the full SAS query string to Blob Storage on every request so Blob authorization is re-evaluated per access. A fixed cache TTL cannot track remaining SAS lifetime, so protected reads must always revalidate at the origin. Only future explicitly public-media routes may enable edge caching, and then only with route-specific authorization semantics whose cache key still varies on the full authorization query string
- **Moderation**: Azure Content Safety or manual review queue for uploaded media while Status remains `PendingModeration` / non-Ready; admin approval records moderation state, operators may reject from either `PendingModeration` or `Quarantined` into `Rejected` (always setting `RejectedUtc` in the same transaction). **Quarantine terminal path**: expired manual holds must transition `Quarantined` → `Rejected` with `RejectedUtc` (then follow the 30-day rejected retention from that timestamp)—never delete quarantine rows as a separate terminal path that races the rejected-retention policy. A finalizer performs the single idempotent transition to `Ready` only after a clean `MalwareScanVerdict`, `ModerationApprovedUtc`, and `ThumbnailReadyUtc` are all present for the sealed canonical ETag
- **Thumbnails**: Azure Function on queue trigger using `SixLabors.ImageSharp` for images and FFmpeg or Azure Video Indexer for MP4/MOV frame thumbnails

## 7. Data Point User Experience

This section is the product contract for create/search flows on web and mobile.

### 7.1 Creating a data point

1. **Location**
   - **Map pin**: user pans/zooms the map and drops a pin (default create path).
   - **GPS**: user opts in; client reads device coordinates only after `POST /api/users/me/consents` shows grant for the current location-policy version (or upserts a new grant), then exchanges that device fix for a short-lived `gpsFixId` via `POST /api/users/me/gps-fixes`. Revocation blocks new handle minting and causes any still-unexpired `gpsFixId` redemption to fail until consent is re-granted.
2. **Near / association radius**
   - UI control (slider or presets, e.g. 50 m / 100 m / 250 m / 1 km) sets `associationRadiusMeters`.
   - Client calls `GET /api/datapoints/chain-candidates` for a dropped pin or `GET /api/datapoints/chain-candidates/gps?gpsFixId=...` for a consent-validated fix; GPS-assisted candidate lookups never accept raw device coordinates on their own route.
     - chains that already have a node within the radius
     - nearby standalone points that can seed a new chain
   - User may: create unlinked, join an existing chain, or link to a standalone neighbor (server creates chain and attaches both).
3. **Historical date/time**
   - Date-time picker allows past `eventDate` values (`DATETIMEOFFSET`); validation rejects impossible calendar values but not “old” dates.
4. **Visibility**
   - Explicit control: Public / Followers-only / Private (maps to `Visibility` tinyint; required on create via request-binding validation/required JSON-member handling—for example, a required nullable enum + validator—with no database default as defense in depth, so omitted/null JSON is rejected instead of silently publishing as Public). Followers-only is gated by **Accepted** follows only—open/pending follow requests do not unlock those posts.
5. **Tags**
   - Multi-select from predetermined system tags plus typeahead to add custom tags (multiple allowed).
6. **Media (optional)**
   - After create, attach photos/videos via SAS upload + complete flow; detail view shows only Ready media.
7. **Add-from-chain**
   - From any timeline node, “Add entry to this chain” opens create with `chainId` preset, location defaulting to the current pin/GPS, still allowing radius-based confirmation against chain nodes.

### 7.2 Searching and exploring data points

1. **Near a location**
   - Search center = map center / dropped pin through `GET /api/datapoints/nearby`, or a consent-validated GPS fix through `GET /api/datapoints/nearby/gps`.
   - User-selected `radiusMeters` (same bounds/default as create association).
   - Search APIs return only points the caller may view, plus continuation cursors; broad map windows may return clusters before raw markers.
2. **Map + list**
   - Results render as map markers and a selectable list synchronized with marker selection.
3. **Place / address search**
   - Free-text address, city, state, or country → `GET /api/datapoints/search/place`.
   - API geocodes via Azure Maps, runs the spatial query, and returns bounded points/clusters + `mapBounds` + continuation cursor.
   - Client fits the map to `mapBounds` so all returned points are visible.
4. **Viewport refine**
   - After place search or nearby search, panning/zooming calls `GET /api/datapoints/in-bounds` (debounced). Narrowing the viewport refines the result set; expanding widens it, and broad views may cluster results until the zoom threshold is met.
5. **Detail + media**
   - Selecting a point opens detail: title, description, event date, visibility badge (if owner), tags, author, and attached Ready/approved media with signed read URLs.
6. **Chain timeline**
   - If the point has a `chainId`, detail defaults to the **most recent visible** event in that chain and exposes a horizontal/vertical timeline scrubber.
   - User scrolls backward/forward through chain nodes ordered most-recent-first by `EventDate`/`DataPointId` (`GET /api/chains/{chainId}/timeline`).
   - Each node shows its own detail and media; “Add entry” remains available on every node.

### 7.3 Client responsibilities

| Concern | Web (React) | Mobile (React Native) |
|---|---|---|
| Map SDK | Mapbox GL or Leaflet + tile provider | Mapbox / Apple Maps / Google Maps |
| GPS | Browser Geolocation API after consent | OS location permissions + consent record |
| Geocoding UI | Place search box calling API (server holds Azure Maps key) | Same |
| Timeline | Chain timeline component bound to cursor API | Same patterns |
| Offline | Post-MVP | Post-MVP |

## 8. Operational Defaults

Unless an environment override is documented in app configuration, implementations **must** use these bounds so clients, API validation, quotas, and janitors agree:

| Parameter | Default | Bounds / notes |
|---|---|---|
| Association / search radius | 250 m | min 1 m, max 50_000 m |
| Nearby / chain-candidates `pageSize` | 25 | min 1, max 100 |
| In-bounds `pageSize` | 200 | min 1, max 500 |
| Comments `pageSize` | 50 | min 1, max 100 |
| Feed `pageSize` | 25 | min 1, max 100 |
| Follow-requests `pageSize` | 25 | min 1, max 100; cursor on `(CreatedUtc ASC, FollowerId ASC)` |
| Data-point description length | 4,000 chars | enforce in request validation and `NVARCHAR(4000)` |
| Photo max / type reservation | 50 MB | `ReservedBytes` charges this max |
| Video max / type reservation | 500 MB | `ReservedBytes` charges this max |
| Avatar max / type reservation | 10 MB | photo types only; prefer size-enforcing proxy |
| Per-user pending upload quota (`PendingReservedBytes` cap) | 2 GB | conditional counter refuses when exceeded |
| Write SAS TTL | 15 minutes | must be ≤ `UploadExpiresUtc`; never days |
| Pending upload / avatar reservation expiry | 30 minutes | janitor releases counter + deletes staging |
| Uncommitted-block inspector cadence | ≤ 60 seconds | while any write SAS for the key is valid |
| Committed oversize reaper cadence | ≤ 60 seconds | Event Grid preferred; poller backup |
| `gpsFixId` TTL | 5 minutes | redeem re-checks consent; janitor purges expired |
| GpsFixes janitor cadence | 1 minute | |
| Opaque cursor TTL | 10 minutes | Data Protection payload expiry |
| Erasure/export receipt entropy | ≥ 256 bits | CSPRNG; show once; store only SHA-256 hash |
| Erasure/export receipt status rate limit | 30 req/min per IP + per `{id}` | constant-time hash compare |
| Export download window | 72 hours after Ready | then delete blob; keep a scrubbed audit row (no downloadable personal data). Erasure must fence/cancel export jobs, delete any archive blob, and scrub/reassign or delete the request row before `Users` deletion so the `ON DELETE NO ACTION` FK cannot block scrubbing |
| Quarantine manual-hold max | 7 days | on expiry the janitor **must** transition `Quarantined` → `Rejected` (set `RejectedUtc = SYSUTCDATETIME()` atomically) and keep blobs/metadata for the rejected-retention window; do not delete directly from quarantine |
| Rejected media retention | 30 days from `RejectedUtc` | audit/review then hard-delete blobs + row; `RejectedUtc` is required whenever `Status = Rejected` (operator reject, malware reject, inspector reject, or quarantine-hold expiry) |
| Data-point retention | 10 years from `DataPoints.CreatedUtc` | retention anchor is **upload/create time** (`CreatedUtc`), never `EventDate` (intentionally historical) or `UpdatedUtc`; after that deadline, purge or irreversibly anonymize location/title/description within the grace window below |
| Retention purge grace window | 30 days after the `CreatedUtc` + 10y deadline | soft-delete may be an intermediate operational flag only, never the terminal retention state |
| Signed media/avatar read URL TTL | ≤ 15 minutes | Front Door caching disabled / no-store for visibility-protected SAS media routes |
| Admin role claim | `roles` contains `Admin` | assigned only via Entra app role / group—no self-service |

## 9. Social Features

### MVP Scope

- User profiles (display name, avatar via caller-scoped upload/complete + signed reads, bio)
- Follow request / accept / reject / unfollow (FollowersOnly requires Accepted; self-follow rejected)
- Comments on data points
- Reactions (like)
- Activity feed (chronological, Accepted followed users' posts)
- Content reporting
- Data point tags (system + custom) and location chains/timelines (see §7)

### Post-MVP

- Push notifications (Azure Notification Hubs)
- Direct messaging
- Cross-user chain invitations (explicit tokenized API—not implied by MVP `linkToDataPointId`)
- Full-text / tag-faceted global search beyond map context
- Trending locations
- Collections / curated maps

## 10. Infrastructure (Terraform Updates)

### New Resources

| Resource | Purpose |
|---|---|
| `azurerm_storage_account` (media) | User-uploaded photos/videos |
| `azurerm_storage_queue` (or Service Bus queue/topic) | Durable media-work queue consumed by the outbox dispatcher and thumbnail/moderation workers |
| `azurerm_eventgrid_system_topic` + event subscription (or equivalent blob event source) | Deliver staging-blob create events and Defender-for-Storage malware scan results to the cleanup and scan-result workers |
| Microsoft Defender for Storage (or equivalent malware scanner configuration) | Produce authoritative malware verdicts for staged/canonical uploads before parsing or publication |
| `azurerm_role_assignment` on the media storage account | Grant `ReMind.Api` least-privilege SAS issuance roles (`Storage Blob Delegator` plus blob data role needed for existence/metadata checks) and grant the thumbnail worker blob read/write rights |
| `azurerm_cdn_frontdoor_profile` + endpoint/origin-group/origin/route (Premium tier) | Front Door entry for both media delivery and the public `ReMind.Api` hostname, with the API origin group probing anonymous `GET /healthz/live` and SAS-protected media routes configured without caching (or, if caching is later enabled for signed media, the cache key must vary on the full SAS query string per §6) |
| `azurerm_cdn_frontdoor_firewall_policy` + `azurerm_cdn_frontdoor_security_policy` | Front Door WAF rules plus association of that WAF policy to the API and media routes/domains |
| `azurerm_key_vault` | Secrets that cannot use identity-based access (e.g. Azure Maps key if required; versioned `SubjectPseudonymKey` material for `ErasedSubjectHashes` HMAC; not storage account keys — Blob SAS uses user-delegation via managed identity) |
| `azurerm_application_insights` | Monitoring and diagnostics |
| `azurerm_log_analytics_workspace` | Centralized logging |
| `azurerm_virtual_network` + subnets | Private network boundary for SQL private endpoint and app integration |
| `azurerm_private_endpoint` + `azurerm_private_dns_zone` + **zone VNet link** + **private DNS zone group** on the endpoint | Private SQL connectivity and name resolution from the integrated VNet |
| `azurerm_app_service_virtual_network_swift_connection` | Allow `ReMind.Api` (and thumbnail Function if applicable) to reach SQL over the private endpoint |
| `azurerm_user_assigned_identity` (or system-assigned) + KV access policies/RBAC + SQL AAD admin/user + Blob/Queue RBAC | Managed identities for API and Function; no plaintext `SqlConnectionString` in app settings; grant the API `Storage Blob Delegator` + least-privilege blob access to mint user-delegation SAS, and grant dispatcher/thumbnail workers only the blob/queue roles required to read source uploads, write thumbnails, ack queue work, and record scan outcomes |
| Azure Maps account (or equivalent geocoder) | Server-side place/address/city/state/country search |

### Modified Resources

| Resource | Change |
|---|---|
| `azurerm_service_plan` | Upgrade from B1 to **P1v3** (definitive production compute host for `ReMind.Api`; do not dual-track Container Apps in this design) |
| `azurerm_linux_web_app` | Repurpose for `ReMind.Api` on the P1v3 plan; add a separate Static Web Apps/Storage static website/Front Door origin (or documented external host) for `ReMind.Web`; Key Vault references + VNet integration; expose an anonymous `/healthz/live` endpoint for Front Door origin probes; restrict direct origin ingress so only this deployment's Front Door reaches the public API origin by requiring the `AzureFrontDoor.Backend` source service tag **and** the matching `X-Azure-FDID` profile header together, then deny unmatched traffic (header-only checks are forgeable; service-tag-only checks still admit other tenants' Front Door profiles) |
| `azurerm_linux_function_app` | Keep for thumbnail generation, outbox dispatcher (if not in-process), and background jobs; managed identity + deterministic thumbnail paths + app settings / trigger bindings that point the workers at the provisioned media-work queue |
| `azurerm_mssql_database` | Upgrade SKU from Basic to S0+ for spatial index performance |
| `azurerm_mssql_server` | Disable public network access when the private endpoint is live |

### Manual/External Resources (Not in Terraform)

- Entra External ID tenant creation (Azure Portal / CLI)
- Microsoft Defender for Storage malware scanning enablement/policy (if not managed through Terraform in the chosen subscription baseline)
- Social identity provider app registrations (Google Cloud Console, Apple Developer, Meta for Developers)
- DNS / custom domain configuration

## 11. DevOps & Testing

### CI/CD Updates

- Add Node.js build step for React SPA (`npm ci`, `npm run build`, `npm test`)
- Add React Native build validation (TypeScript check, Jest tests)
- Build the EF Core migration bundle in CI, then execute it from a VNet-connected/self-hosted runner or Azure-side migration job that can reach private SQL; remove the `ReMind.Database` `azure/sql-action` path before the first database deployment
- Playwright tests target React SPA (not Razor), including create (map pin, tags, visibility, chain link) and search (nearby, place, timeline) flows
- Load testing for proximity and viewport queries (k6 or Azure Load Testing)
- Fail CI on `dotnet format` / ESLint / TypeScript errors for touched projects

### Test Strategy

| Layer | Tool | Scope |
|---|---|---|
| Unit tests | xUnit + Moq | Business logic, validators, spatial query builders, chain association, tag normalization, receipt hashing, quota counter |
| Integration tests | xUnit + TestContainers | EF Core + SQL Server with spatial types, chain timeline cursors, outbox commit, erasure blocklist, upload reservation races |
| E2E tests | Playwright | React SPA create/search/timeline/media flows |
| Mobile tests | Jest + Detox/Maestro | React Native screens and API integration |

## 12. Security & Compliance

### Data Privacy (GDPR / CCPA)

- Location data is personal data.
- **Consent**: `UserConsents` stores `UserId`, `Purpose` (`LocationGps`), `PolicyVersion`, `Granted`, `RecordedUtc`, `ClientUtc`, `RevokedUtc`, with one mutable current-state row per (`UserId`, `Purpose`, `PolicyVersion`). GPS-assisted create/search requires a non-revoked grant for the current policy version at both `gpsFixId` mint and redeem. Map-pin coordinates remain personal data under the privacy policy and retention/erasure flows.
- **GPS fix retention**: `GpsFixes` expire per §8; a scheduled janitor purges expired rows; erasure deletes any remaining fixes immediately.
- **Data retention**: at the §8 retention deadline measured from each data point’s **`CreatedUtc`** (not `EventDate` or `UpdatedUtc`), rows must be permanently purged or irreversibly anonymized within the configured grace window; soft-delete is allowed only as that short-lived operational precursor, not as the terminal retained state. Document the retention worker alongside media janitors.
- **Right to erasure** (ordered workflow):
  1. `POST /api/users/me/erasure-requests` (authenticated) records `UserErasureRequests`, returns a one-time ≥256-bit opaque status receipt (body only; never URLs/logs), persists only `StatusReceiptHash = SHA256(receipt)`.
  2. Mark user/media/`AvatarUploads` deleting (lease/version); stop issuing new signed URLs (short TTLs drain old ones).
  3. Tombstone/cancel queued media work; fence dispatchers/workers on the deleting lease before publish/thumbnail writes. **Fence export jobs the same way**: cancel any in-flight `UserExportRequests` for the user, refuse new export downloads, and clear `DownloadBlobPath` after deleting the archive blob so no personal-data archive remains.
  4. Delete blobs/thumbnails; purge Front Door cache; final blob sweep after drain (include export archive blobs).
  5. FK-safe SQL cleanup: `GpsFixes`, `UserConsents`, `Follows`, `Reactions`, `Reports`, custom-tag ownership/unused custom tags, `Media`/`AvatarUploads`, comments on deleted data points, the user's own `DataPoints`, and `UserExportRequests` handling that satisfies both the `UserId … ON DELETE NO ACTION` FK and §8’s “keep audit row” rule—delete any associated export archive blob under `DownloadBlobPath` first, then either (a) delete export request rows when no audit retention is required in that environment, or (b) **scrub** the audit row in place (`UserId` set NULL only if the column is widened to NULL with the FK dropped/adjusted, or reassign to the system tombstone user and clear receipt hashes, download paths, and payload metadata that identify the erased subject) **before** deleting `Users`. Leaving a live `UserId` FK to the erased user blocks deletion and can leave a personal-data archive behind. Comments that still survive on retained data points are reassigned to the deploy-time system tombstone user before deleting `Users`, related `OutboxMessages`, or retained ownership on `DataPointChains`/`Tags`.
  6. **Before** scrubbing `Users`, insert `ErasedSubjectHashes` for `HMAC-SHA-256(SubjectPseudonymKey[current], Issuer || 0x00 || ExternalSubject)` with the current `KeyVersion`, keyed to the erasure request (idempotent).
  7. Scrub/delete the `Users` row (clear `UserErasureRequests.UserId` if needed); mark request Completed.
  8. **Status**: `GET /api/erasure-requests/{id}` is `AllowAnonymous` + receipt-only (constant-time hash compare), rate-limited per §8. JWT must not authorize, JIT, or recreate profiles. Never authorize by path id alone or tombstone-subject matching.
- **Data export**: full portable archive of the caller’s profile, consents, data points (including locations/descriptions/tags/chain ids), Ready media metadata (not unbounded binary duplication beyond originals the user uploaded), comments, reactions, and follows. `POST` returns one-time download receipt; store only `DownloadReceiptHash`. Job-state GET requires authenticated owner; signed download URL only when `DownloadExpiresUtc` is valid **and** `X-Export-Download-Receipt` (or body) matches—never path id or ownership alone, never receipt in query strings. Janitor deletes the blob at expiry and may retain a scrubbed audit row without downloadable personal data. Erasure must fence/cancel outstanding export jobs as in step 3–5 above so export rows cannot block `Users` deletion.

### Application Security

- Secrets in Key Vault (including versioned `SubjectPseudonymKey` for erased-subject HMACs). Rotation may add a new current version for newly minted hashes, but **every `KeyVersion` still referenced by `ErasedSubjectHashes` must remain retrievable**—do not disable or delete a referenced key merely because a rotation window elapsed, or anti-reprovisioning breaks for identities that can no longer be re-hashed. A key version may be retired only after zero rows reference it (or after a deliberate redesign that can safely re-key without plaintext subjects). Managed identities for API and workers; Entra-only SQL preferred; break-glass SQL creds only outside Terraform state if unavoidable
- CORS allow-list of known SPA origins (no `*`); Blob CORS mirrors the same origins for upload methods/headers only
- Input validation: HTML sanitize descriptions/comments, enforce the 4,000-char data-point description cap and existing comment bounds, validate uploads by signature, bound radii/page sizes (§8), normalize tags
- SQL: EF Core parameterization; migration raw SQL only for controlled DDL such as `CREATE SPATIAL INDEX` in a **dedicated subsequent migration** (`suppressTransaction: true` + existence guard)—never mixed into the initial transactional schema migration
- Rate limiting: auth token endpoints, place search, media/avatar init, erasure/export status (§8)
- Security headers on SPA/API responses: CSP, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` (or strict-origin) on pages that may touch signed URLs, frame ancestors deny as appropriate
- Soft-delete query filters on `DataPoints`/`Comments`; sequential IDs never distinguishable via different error codes for missing vs forbidden (shared not-found)
- Receipts and cursors: CSPRNG/high-entropy; hashed at rest where required; constant-time compare; short TTL

### Network Security

- SQL public network access disabled in production; VNet, private endpoint, private DNS zone, **VNet link**, **DNS zone group**, and API VNet integration
- Blob direct upload only with §6 proxy **or** uncommitted-block inspector; anonymous blob access disabled; short-lived create+write SAS (TTL §8, ≤ pending expiry); per-blob path scope
- Narrow IP firewall when public access is required; do not enable “Allow Azure services”
- Front Door WAF (OWASP) on API **and** media hostnames; App Service origin allow only `AzureFrontDoor.Backend` **and** matching `X-Azure-FDID`, then deny (header-only is forgeable; tag-only admits other tenants’ Front Door)

## 13. Implementation Phases

### Pre-Phase 1: Solution and platform refactor
- Scaffold `ReMind.Api`, `ReMind.Web`, `ReMind.Core`, `ReMind.Data`, and `ReMind.Mobile` per §1 decisions
- Remove `ReMind.Database` from deploy/CI before first DB deployment; keep Razor/Functions HTTP only until API/SPA cutover, then delete
- Wire CI for .NET + Node early

### Phase 1: Foundation
- Entra External ID tenant + app registrations (API + SPA + mobile) and Admin app role
- Initial EF schema from §2 (including `ErasedSubjectHashes`, media, privacy tables) + spatial index migration rules
- `ReMind.Api` JWT auth, fallback policy, Problem Details, EF Core + NetTopologySuite
- Scaffold `ReMind.Web` map shell (Leaflet/Mapbox)

### Phase 2: Core
- Create flow: map pin / GPS, `eventDate`, visibility, tags, chain candidates + owner-only `linkToDataPointId`
- Search: nearby, place geocode, map+list, `/in-bounds`
- Detail + chain timeline + add-from-chain
- Profiles + consent + `gpsFixId` APIs
- Upload pipeline with quota counter, proxy/inspector, Defender/Event Grid scan-result flow (or equivalent), outbox, thumbnails, and non-cached SAS reads
- Admin role assignment + moderation queue (data-point media Ready gate)
- Avatar upload via `AvatarUploads` (+ preferred size-capped proxy)
- Export + erasure (receipt-only status, `ErasedSubjectHashes` blocklist) + janitors

### Phase 3: Social
- Follow request/accept/reject/unfollow (Accepted-only visibility; no self-follow)
- Comments and reactions (visibility-gated)
- Activity feed
- Content reporting

### Phase 4: Mobile + Polish
- React Native parity for create/search/timeline
- Push notifications
- Moderation automation/polish
- Performance (caching, CDN, spatial index tuning)
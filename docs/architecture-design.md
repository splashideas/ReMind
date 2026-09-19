# ReMind Architecture Design

## Overview

ReMind is a map-based social platform that lets users pin historical data points (text, dates, photos, videos, tags) to real-world locations, link related events into location chains/timelines, and explore history on a map. Users interact through a React web app or a React Native mobile app (iOS/Android). The backend is a .NET API on Azure, using Azure SQL spatial types for proximity queries, Azure Maps for place geocoding, and Microsoft Entra External ID for authentication.

## Current State vs. Target

| Aspect | Current | Target |
|---|---|---|
| Frontend | ASP.NET Core Razor Pages (`ReMind.Frontend`) | React SPA + React Native mobile app |
| API | Azure Functions with function-key auth | ASP.NET Core Web API on App Service / Container Apps with JWT |
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

**Decisions to make:**
- Keep or retire `ReMind.Frontend` (Razor) and `ReMind.Functions` once the new projects are stable.
- Adopt EF Core migrations in `ReMind.Data` as the schema source of truth and retire `ReMind.Database` before the first database deployment.

## 2. Spatial Data Model

### DataPoints Table (Redesigned)

```sql
CREATE TABLE dbo.Users (
    UserId           UNIQUEIDENTIFIER PRIMARY KEY,
    Issuer           NVARCHAR(200) NOT NULL,
    ExternalSubject  NVARCHAR(200) NOT NULL,
    DisplayName      NVARCHAR(200) NOT NULL,
    AvatarBlobPath   NVARCHAR(400) NULL,
    Bio              NVARCHAR(1000) NULL,
    CreatedUtc       DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    DeletedUtc       DATETIME2(7) NULL,
    CONSTRAINT UQ_Users_Issuer_ExternalSubject UNIQUE (Issuer, ExternalSubject)
);

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
    Description     NVARCHAR(MAX) NOT NULL,
    EventDate       DATETIMEOFFSET NOT NULL, -- may be historical (past) or near-present
    Location        GEOGRAPHY NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedUtc      DATETIME2(7) NULL,
    Visibility      TINYINT NOT NULL DEFAULT 0,  -- 0=Public, 1=FollowersOnly, 2=Private
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
```

### Supporting Tables

- **Users**: profile info, persisted external identity pair (`Issuer`, `ExternalSubject`) mapped from the validated token (`iss` + `oid`, or another explicitly linked provider subject when `oid` is unavailable), display name, avatar blob path, bio; user erasure stays asynchronous and only deletes or scrubs the `Users` row after dependent SQL rows/blob artifacts are removed or reassigned in order (consents, follows, reactions, comments, reports, custom-tag ownership, media/data points, then any retained `DataPointChains.CreatedByUserId` / `Tags.CreatedByUserId` references reassigned to a non-authenticating tombstone user row such as `Deleted User`)
- **UserConsents**: auditable GPS/location consent grants and revocations (`Purpose`, `PolicyVersion`, `Granted`, timestamps) enforced server-side for GPS-assisted operations
- **GpsFixes**: short-lived server-issued handles that bind a consent-validated GPS coordinate fix to the current user (`GpsFixId`, `UserId`, `Location`, `CapturedUtc`, `AccuracyMeters`, `ExpiresUtc`); create/search flows reference `gpsFixId` instead of trusting a client-supplied `"source": "Gps"` flag, expired rows are purged by a scheduled janitor on a short cadence, and any still-present fixes are deleted during user erasure
- **DataPointChains**: logical timeline grouping related events at/near a place; membership is via `DataPoints.ChainId`
- **Tags** / **DataPointTags**: many-to-many labels; `IsSystem = 1` rows are the predetermined catalog (seeded), custom tags are user-created with unique normalized names
- **Media**: `MediaId`, `DataPointId`, `OwnerUserId`, `UploadId`, `ClientRequestId`, `BlobPath`, `ThumbnailPath`, `MediaType` (Photo/Video), `Status` (`PendingUpload` / `Quarantined` / `Processing` / `PendingModeration` / `Ready` / `Rejected`), `ModerationApprovedUtc`, `ThumbnailReadyUtc`, `UploadExpiresUtc`, `SortOrder`; unique constraints on `UploadId`, (`OwnerUserId`, `DataPointId`, `ClientRequestId`), and (`DataPointId`, `BlobPath`); issuing an upload URL creates or refreshes the caller-owned pending reservation for the parent data point, an expiry reaper deletes abandoned pending uploads/blobs, moderation/thumbnail gates are recorded separately, and only an idempotent finalization step may promote a row to `Ready` once both gates are satisfied
- **OutboxMessages**: transactional outbox rows written in the same SQL transaction as media state changes (`OutboxId`, `Type`, `DedupKey`, `Payload`, `CreatedUtc`, `ProcessedUtc`, `Attempts`) with a unique `DedupKey` per upload/work type so queue publication is durable, retryable, and idempotent
- **UserExportRequests** / **UserErasureRequests**: durable per-user async-job records (`RequestId`, `UserId`, `Status`, `RequestedUtc`, `StartedUtc`, `CompletedUtc`, `FailureCode`, `AuditJson`, optional `DownloadBlobPath` for exports) that own the documented request/status endpoints and enforce caller-scoped reads, retries, and auditable completion
- **Comments**: `CommentId`, `DataPointId`, `UserId`, `Text`, `CreatedUtc`, nullable `ParentCommentId` (threading); writes must enforce that any parent comment belongs to the same `DataPointId` (for example via a composite `(CommentId, DataPointId)` relationship or equivalent transactional validation), and parent-comment deletes use `ON DELETE SET NULL` plus tombstone/anonymize the erased parent before any optional hard delete so other users' replies remain intact
- **Reactions**: `ReactionId`, `DataPointId`, `UserId`, `ReactionType` (Like, Love, etc.); unique constraint on (`DataPointId`, `UserId`) means each user has exactly one current reaction per data point and a new reaction replaces the prior value
- **Follows**: `FollowerId`, `FolloweeId`, `CreatedUtc`; unique constraint on (`FollowerId`, `FolloweeId`)
- **Reports**: `ReportId`, `ReporterId`, nullable `DataPointId`, nullable `CommentId`, `Reason`, `Status` (`Open` / `Resolved` / `Dismissed`), `CreatedUtc`; foreign keys enforce valid targets and a CHECK constraint requires exactly one of `DataPointId` or `CommentId` to be non-null

### Chain Association Rules

- A **chain** is an ordered set of data points (by `EventDate DESC`, then `DataPointId DESC`) that share a `ChainId`.
- When creating a point at location *L* with association radius *R* (meters, user-selected), the API proposes:
  1. Existing chains that have **any node visible to the caller** within `STDistance(node.Location, L) <= R`; returned chain labels/counts are derived only from that visible subset and never reveal hidden members
  2. Standalone (unchained) visible data points within *R* that can be merged into a **new** chain with the new point
- `chainId` and `linkToDataPointId` are mutually exclusive in create/update requests; supplying both is a validation error (HTTP 400).
- Joining a chain attaches the new row’s `ChainId`; optionally promotes a selected standalone neighbor into the same new chain in one transaction.
- If empty chains are supported, only the chain creator may add the first node without an existing visible-node proximity match; all later additions use the normal association rules.
- Chain membership does not require identical coordinates—only proximity of at least one node within the chosen radius at link time.
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
- Association and search “near” radius is always caller-supplied within documented bounds (default 250 m, min 1 m, max 50_000 m)
## 3. EF Core + NetTopologySuite

### Package

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite" Version="10.0.0" />
<PackageReference Include="Microsoft.Identity.Web" Version="3.8.3" />
```

`Microsoft.Identity.Web` provides `AddMicrosoftIdentityWebApi` for JWT bearer validation. Pin versions to the chosen .NET TFM during implementation.
### DbContext Setup

```csharp
builder.Services.AddDbContext<ReMindDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.UseNetTopologySuite()));

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<DataPoint>()
        .Property(d => d.Location)
        .HasColumnType("geography");
}
```

### Caller Identity

Never accept `userId` from the client request body or query string for authorization. Resolve the internal `Users.UserId` server-side from the validated JWT (`iss` + `oid` → the stable user identity key, or another explicitly linked provider key when `oid` is unavailable), creating the profile row on first authenticated request if needed. All visibility, ownership, and follow-graph checks use that server-resolved ID only.

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
    return Results.BadRequest("Invalid nearby search parameters.");
}

// Resolve caller identity server-side from the validated token (iss + oid → the stable user identity key).
// Never accept currentUserId from the request body/query; clients must not supply caller identity.
Guid currentUserId = await userDirectory.GetCurrentUserIdAsync(HttpContext.User, cancellationToken);
if (request.Cursor is not null
    && (request.Cursor.DistanceMeters is null || request.Cursor.DataPointId is null))
{
    return Results.BadRequest("Nearby cursor must include both distance and dataPointId.");
}
double? cursorDistance = request.Cursor?.DistanceMeters;
long? cursorDataPointId = request.Cursor?.DataPointId;
var userLocation = new Point(longitude, latitude) { SRID = 4326 };

// Keep the follow visibility test in SQL via a correlated EXISTS; do not materialize
// followee ids into application memory and feed them back through Contains(...).
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
                && f.FolloweeId == x.DataPoint.UserId)))
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
//         distanceMeters: nearby.Last().DistanceMeters,
//         dataPointId: nearby.Last().DataPoint.DataPointId,
//         latitude: latitude,
//         longitude: longitude,
//         radiusMeters: radiusMeters,
//         pageSize: pageSize))
```

Chain-candidate lookup for create uses the same visibility predicate and `Distance <= associationRadiusMeters`, grouping matches by `ChainId` (plus standalone neighbors). Timeline queries filter `ChainId == chainId`, apply visibility, and order by `EventDate DESC` / `DataPointId DESC` with a date cursor—not distance.

### Migration Strategy

- **Selected approach (Option A)**: EF Core migrations are the schema source of truth.
- No database has been created yet, so there is no existing `dbo.DataPoints` table or application data to migrate, backfill, or preserve.
- Before deploying the database resources, remove the old create-if-missing SQL path and create the initial schema from the first EF Core migration/bundle.
- Ensure the initial EF Core migration explicitly emits `CREATE SPATIAL INDEX IX_DataPoints_Location ...` because the `Location` mapping alone does not create the spatial index.
- If the schema design changes again before first deployment, update the initial migration and redeploy; no rollback/data-disposition process is required until persisted data exists.

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
    options.AddPolicy("CanViewFriendsOnly", p => p.RequireAuthenticatedUser());
    options.AddPolicy("CanViewPrivate", p => p.RequireAuthenticatedUser());
    options.AddPolicy("CanModerate", p => p.RequireRole("Admin"));
});
```

JWT validation alone is not enough: register authorization with a **fallback authenticated policy** so endpoints are not anonymously callable by default, and call `app.UseAuthentication();` plus `app.UseAuthorization();` before mapping endpoints/controllers so the fallback policy is enforced. Apply explicit endpoint policies for visibility decisions (`CanViewFriendsOnly` / `CanViewPrivate`, backed by resource-based authorization handlers that evaluate the caller against the target author/chain) and a separate admin-role policy for moderation routes (`CanModerate`, which is intentionally admin-only in this design). Map the stable caller identity (`iss` + `oid`, or another explicitly linked provider key when `oid` is unavailable) to `Users.UserId` inside the API boundary; do not trust client-supplied user IDs.

### Authorization Model

| Action | Rule |
|---|---|
| View public posts | Any authenticated user |
| View followers-only posts | Follower of the author |
| View private posts | Author only |
| Create post | Any authenticated user (GPS/location consent recorded when using device GPS) |
| Join an existing chain | Any authenticated user who can view at least one proposed chain node |
| Create a new chain from nearby standalone points | Any authenticated user may create the chain row, but attaching an existing standalone point requires that the caller own that point or have an explicit owner approval/invitation; the new chain row records that caller as `CreatedByUserId` |
| Attach media to own post | Author only; upload reservation/media row stores `OwnerUserId` matching the parent data point owner |
| Edit/delete own post | Author only |
| Comment | Any authenticated user who can view the target post |
| React | Any authenticated user who can view the target post |
| Tag own post | Author only |
| Report | Any authenticated user |
| Moderate | Admin role (custom claim) |
## 5. API Design

### Endpoints

```
POST   /api/datapoints                  # Create: title, description, eventDate (past allowed), visibility, exactly one of mapLocation or gpsFixId, optional chainId XOR linkToDataPointId, associationRadiusMeters, tagIds[] + customTagNames[]
GET    /api/datapoints/nearby           # Proximity search around explicit map-selected coordinates (lat [-90,90], lng [-180,180], radiusMeters 1-50000 default 250, opaque route-specific cursor bound to /nearby + the same center/radius/pageSize, pageSize default 25 max 100); visibility via JWT user + follow graph
GET    /api/datapoints/nearby/gps       # Proximity search around a previously consent-validated gpsFixId; same opaque route-specific cursor/pageSize contract as /nearby and the cursor is rejected if reused with a different fix, radius, or route
GET    /api/datapoints/in-bounds        # Map viewport query (north/south/east/west, zoom, mode=clusters|points, viewportToken, cursor, pageSize default 200 max 500); same visibility rules; cluster pages use a stable cluster cursor, point pages use a stable leaf-point cursor, and any viewport/mode change invalidates the prior cursor
GET    /api/datapoints/search/place     # Geocode address/city/state/country via Azure Maps, then return the first bounded nearby/in-bounds result page plus suggested map bounds and continuation cursor
GET    /api/datapoints/{id}             # Detail + Ready media + tags + visibility-filtered chain summary (only caller-visible neighbor counts / adjacent timeline cursors)
PUT    /api/datapoints/{id}             # Update own data point (including visibility, eventDate, tags, optional chain relink within rules)
DELETE /api/datapoints/{id}             # Soft-delete own data point

GET    /api/datapoints/chain-candidates # Given explicit map-selected lat/lng + associationRadiusMeters, list joinable chains and nearby standalone points the caller can see (opaque cursor + pageSize default 25 max 100)
GET    /api/datapoints/chain-candidates/gps # Same lookup around a previously consent-validated gpsFixId with the same opaque cursor/pageSize contract; never accept raw device GPS coordinates on this route
GET    /api/chains/{chainId}/timeline   # Chain events ordered by EventDate DESC, DataPointId DESC; cursor = (eventDate, dataPointId) and the seek predicate is < on that descending tuple; includes detail payload suitable for scrubbing a timeline
POST   /api/chains                      # Explicitly create an empty/titled chain (optional; only the creator may add the first node before any visible-node proximity check exists)
POST   /api/chains/{chainId}/datapoints # Add a new data point into an existing chain; request must include exactly one of mapLocation or gpsFixId and still satisfy association radius vs some visible node, except for the creator-only first-node add to an empty chain

GET    /api/tags                        # List system tags + caller's recent custom tags (search q= optional)
POST   /api/tags                        # Create custom tag (idempotent on NormalizedName)

POST   /api/datapoints/{id}/media       # Author only: requires a client-generated idempotency key; create/refresh the caller-owned PendingUpload row (expiry + canonical blob key) and return the same UploadId/SAS reservation when that key is retried before expiry
POST   /api/datapoints/{id}/media/complete # Validate the existing PendingUpload row/blob; failed validation transitions it to Quarantined/Rejected, successful validation in one SQL transaction moves it to Processing/PendingModeration as appropriate, writes one deduplicated OutboxMessages row, and lets the retrying dispatcher publish queue work
POST   /api/datapoints/{id}/comments    # Add comment (caller must be allowed to view the post)
GET    /api/datapoints/{id}/comments    # List comments oldest-first; cursor = (createdUtc, commentId) and the seek predicate is > on that ascending tuple; pageSize default 50 max 100
POST   /api/datapoints/{id}/reactions   # Create or replace the caller's single current reaction for the post (caller must be allowed to view it)
DELETE /api/datapoints/{id}/reactions   # Remove caller's reaction

GET    /api/users/{id}                  # Get user profile
PUT    /api/users/me                    # Update own profile
POST   /api/users/me/gps-fixes          # Bind a freshly captured device GPS fix to the authenticated user after verifying current consent; returns short-lived gpsFixId
POST   /api/users/me/consents           # Record location/GPS consent (purpose, policyVersion, granted, client timestamp)
GET    /api/users/me/consents           # Current consent state used to gate GPS-assisted create/search
POST   /api/users/me/export-requests    # Request a full user-data export job
GET    /api/users/me/export-requests/{id} # Check the durable export-request state machine and download when ready
POST   /api/users/me/erasure-requests   # Request async user-data erasure across SQL, blobs, thumbnails, and CDN caches
GET    /api/users/me/erasure-requests/{id} # Check the durable erasure-request state machine until auditable completion
PUT    /api/users/{id}/follow           # Follow
DELETE /api/users/{id}/follow           # Unfollow

GET    /api/feed                        # Activity feed (followed users' posts), newest-first; cursor = (createdUtc, dataPointId) and the seek predicate is < on that descending tuple; pageSize default 25 max 100
POST   /api/reports                     # Report content
GET    /api/moderation/queue            # Admin: moderation queue (CanModerate policy)
POST   /api/moderation/datapoints/{id}/hide # Admin: hide/tombstone a reported data point and resolve linked open reports idempotently
POST   /api/moderation/comments/{id}/hide   # Admin: hide/tombstone a reported comment and resolve linked open reports idempotently
POST   /api/moderation/media/{mediaId}/approve # Admin: idempotently record moderation approval; a separate finalization step promotes media to Ready only after validation + thumbnail success are already complete
POST   /api/moderation/media/{mediaId}/reject  # Admin: idempotently transition PendingModeration media to Rejected and trigger bounded-retention cleanup
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

Create accepts exactly one of `mapLocation` or a short-lived server-issued `gpsFixId`. `gpsFixId` values come only from `POST /api/users/me/gps-fixes`, which verifies the caller has a stored, non-revoked location consent for the current policy version before binding the coordinates to that user, and every later redeem of that handle re-checks that the same consent is still current/non-revoked before the server uses the GPS fix. Map-pin-only creates do not require device GPS consent but still treat coordinates as personal data under the privacy policy.
The request intentionally omits any client-declared `location.source` flag: the server derives GPS-vs-map-pin handling solely from `gpsFixId` versus `mapLocation`.

### Nearby search request contract (illustrative)

```http
GET /api/datapoints/nearby?lat=52.52&lng=13.405&radiusMeters=250&pageSize=25&cursor=opaque.signed.shortlived.token
```

`/nearby` is for explicit map-selected coordinates. GPS-assisted nearby search uses `GET /api/datapoints/nearby/gps?gpsFixId=...` with a short-lived `gpsFixId`, and redeeming that handle re-checks current GPS consent so revocation blocks later searches even before the handle expires. The continuation cursor is an opaque, server-issued, signed short-lived token (or equivalent authenticated server-stored handle); it binds the route/search mode, the `(distanceMeters, dataPointId)` seek boundary, and the original bound inputs (map center or `gpsFixId`, radius, page size), and the API rejects any cursor that is expired, forged, or replayed with different route or bound inputs.

### Design Notes

- Pagination is cursor-based (not OFFSET) for large result sets; nearby and chain-candidate cursors are opaque, signed short-lived tokens (or equivalent authenticated handles) that bind the route/search mode plus the seek boundary to the original center/`gpsFixId`, radius, and page size before applying the next seek, timeline cursors are `(eventDate, dataPointId)` with `EventDate DESC, DataPointId DESC` and seek `<`, feed cursors are `(createdUtc, dataPointId)` with `CreatedUtc DESC, DataPointId DESC` and seek `<`, comments cursors are `(createdUtc, commentId)` with `CreatedUtc ASC, CommentId ASC` and seek `>`, and empty pages return no continuation cursor
- Reject invalid coordinates/radius/page size/bounds with HTTP 400 before `Point(...)`, `STDistance`, and `Take(...)`
- Bounding-box pre-filter before `STDistance` for map viewport and place-search result windows; when a viewport crosses the antimeridian (`west > east`), split the longitude predicate into west→180 and -180→east ranges while keeping the same viewport token/cursor contract
- Place search: Azure Maps Search (or equivalent) geocodes the query string → center/bbox → same visibility-filtered spatial query; response includes `mapBounds` so the client can fit all returned points, plus bounded result items (`pageSize` + `cursor`) and clusters when the viewport is too broad for raw markers
- Viewport refine: client debounces `moveend`/`zoomend`, calls `/in-bounds`, replaces markers + list, and keeps following continuation cursors while the viewport is unchanged; `/in-bounds` explicitly paginates either `mode=clusters` with a stable `(clusterSortKey, clusterId)` cursor or `mode=points` with a stable `(eventDate, dataPointId)` cursor bound to the same `viewportToken`; narrowing the map narrows the query, while low-zoom/world-scale boxes return clusters or a capped page instead of an unbounded raw point list
- Comments and feed reads are also cursor-bounded so no single request materializes an arbitrarily large thread or followed-user history
- API/application-layer rate limiting via `AspNetCoreRateLimit` or gateway quotas; Azure Front Door WAF is complementary edge protection, not a substitute for per-user/per-token throttling
- All read paths (including `GET /nearby`, `GET /nearby/gps`, `/in-bounds`, and place search) evaluate visibility with the authenticated caller ID resolved from the JWT (not a client-supplied user id) plus follow relationships
- `/nearby` responses retain the SQL-computed `DistanceMeters` so the server can build the next opaque cursor from `(distance, DataPointId)` without recomputing the boundary client-side
- Detail and chain-summary responses are visibility-filtered end to end: neighbor counts, adjacent timeline cursors, and chain-candidate summaries are computed only from members the caller may currently view and never reveal hidden/deleted nodes
- GPS-assisted create/search flows rely on server-issued `gpsFixId` handles, not on a client-declared `"source"` enum, as the enforcement boundary for consent
- OpenAPI/Swagger for API documentation
- SignalR hub for real-time map updates (post-MVP)

## 6. Media Storage

### Architecture

```
Client → API (request upload URL)
       → API confirms the caller owns the target data point, persists the caller-supplied idempotency key on a PendingUpload media row,
        returns the existing unexpired reservation when that key is retried, otherwise creates/refreshes the row with bounded expiry/retention
        plus canonical blob/thumbnail paths, then uses its managed identity to obtain a user-delegation key and mint a short-lived,
        create-only Blob SAS for that server-owned uploadId/blob key
       → Client uploads directly to Blob Storage at a SAS-writable staging key
       → Client confirms upload → API validates the staging blob exists and matches expected ETag/size/hash/signature,
        then seals that exact validated version by conditionally copying the validated ETag to the server-write-only canonical blob key that
        workers and read URLs use, and in one SQL transaction updates the existing media row to Quarantined (manual-hold only with `QuarantineReviewByUtc`)
        or Processing/PendingModeration/Rejected as appropriate
        and, only on the first `PendingUpload` → `Processing` / `PendingModeration` transition, inserts exactly one transactional outbox row for successful thumbnail/moderation work keyed by `UploadId` + work type
       → Retrying outbox dispatcher publishes the queue message (at-least-once) until acknowledged
       → Expired `PendingUpload` rows/blobs, expired `Quarantined` manual holds, and bounded-retention `Rejected` artifacts are removed by a janitor that deletes both blob objects and media metadata
       → Azure Function (queue trigger) generates thumbnail idempotently to the canonical path `media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`, updates media thumbnail fields, and records `ThumbnailReadyUtc`
       → Media.Status becomes Ready only in a separate idempotent finalization transaction after validation + thumbnail success + moderation approval are all recorded; until then it remains non-public
        (`PendingModeration` or `Rejected` as appropriate), and read APIs / signed URLs only expose Ready media
        for callers allowed to see the parent data point
       → Front Door / signed URLs serve approved thumbnails and media from those canonical paths
```

### Decisions

- **Container structure**: SAS-writable staging originals `media/{userId}/{dataPointId}/staging/{uploadId}/{fileName}`, sealed canonical originals `media/{userId}/{dataPointId}/{uploadId}/{fileName}`, and thumbnails `media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg` (canonical paths derived from `UploadId`, not random names)
- **Allowed types**: JPEG, PNG, WebP, MP4, MOV (validate blob signatures/content server-side, not only client-supplied MIME + extension, before publishing)
- **Max file size**: 50 MB photos, 500 MB videos
- **Upload quota enforcement**: Blob SAS cannot hard-cap object size, so before minting a SAS the API reserves bytes against a bounded per-user pending-upload quota and refuses new reservations once that quota is exhausted; completion rejects oversize blobs, and a short-cadence janitor deletes expired or oversize pending blobs/rows to bound residual storage/bandwidth cost
- **Upload identity**: the initial upload call is author-only and requires a client-generated `ClientRequestId` idempotency key unique per caller + data point while the reservation is live; the API persists that key on the pending reservation, returns the same unexpired `UploadId`/SAS instead of creating a duplicate row when the key is retried, stores both the SAS-writable staging blob key and sealed canonical blob key/expiry on that reservation, and completion is idempotent on `UploadId`; terminal `Ready`/`Rejected` states are no-ops on retry, invalid validation outcomes transition directly to `Rejected` unless an operator explicitly places the upload in a time-bounded `Quarantined` manual hold, duplicate completion attempts must not enqueue duplicate work, expired pending uploads are reaped so abandoned blobs do not accumulate, and rejected/quarantined blobs/metadata are retained only for a bounded audit/review window before janitor cleanup
- **Media outbox**: record thumbnail/moderation work in the same SQL transaction as the media row; a retrying dispatcher publishes to the queue so a crash between commit and enqueue cannot leave media permanently unprocessed, and a unique outbox deduplication key (`UploadId` + work type) prevents duplicate work rows
- **Thumbnail worker**: queue delivery is at-least-once, so thumbnail blob paths must be deterministic (`media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`) across the producer, worker, and signed-read URL builder; if the thumbnail already exists, treat that as success after verifying/upserting metadata, otherwise create it and then upsert metadata without duplicate side effects
- **Blob SAS**: mint user-delegation SAS tokens with the API managed identity (no retained storage account keys); uploads are constrained to a single staging blob path with create + write permissions so chunked `Put Block` / `Put Block List` uploads work, the initial create must use `If-None-Match: *`, and completion validates the staging blob's ETag/size/hash then conditionally seals that exact validated ETag by copying it to the server-write-only canonical blob key before any processing or publish step; workers/read URLs never use the staging object, and Key Vault remains only for secrets that cannot use identity-based access
- **Upload networking**: because web/mobile clients upload directly, the Blob service endpoint must remain publicly reachable for the upload container, but anonymous blob access stays disabled and Blob service CORS is configured in IaC for each allowed SPA origin plus the required `PUT`/preflight headers/methods—never wildcard origins—while SAS scope/TTL still restrict access to the intended blob path
- **CDN**: Azure Front Door Premium (`azurerm_cdn_frontdoor_*`) for media delivery; the API issues short-lived, visibility-checked read URLs whose query string carries a blob-read SAS for the single canonical object, Front Door forwards that full SAS query string to Blob Storage over the selected origin route and, if caching remains enabled for these media routes, the cache key must vary on the entire SAS query so requests without that authorization cannot reuse an authorized response; cache TTL never exceeds the SAS expiry so stale authorized objects are not served after revocation
- **Moderation**: Azure Content Safety or manual review queue for uploaded media while Status remains `PendingModeration` / non-Ready; admin approval records moderation state, and a finalizer performs the single idempotent transition to `Ready` only after `ModerationApprovedUtc` and `ThumbnailReadyUtc` are both present
- **Thumbnails**: Azure Function on queue trigger using `SixLabors.ImageSharp` for images and FFmpeg or Azure Video Indexer for MP4/MOV frame thumbnails

## 7. Data Point User Experience

This section is the product contract for create/search flows on web and mobile.

### 7.1 Creating a data point

1. **Location**
   - **Map pin**: user pans/zooms the map and drops a pin (default create path).
   - **GPS**: user opts in; client reads device coordinates only after `POST /api/users/me/consents` shows grant for the current location-policy version (or records a new grant), then exchanges that device fix for a short-lived `gpsFixId` via `POST /api/users/me/gps-fixes`. Revocation blocks new handle minting and causes any still-unexpired `gpsFixId` redemption to fail until consent is re-granted.
2. **Near / association radius**
   - UI control (slider or presets, e.g. 50 m / 100 m / 250 m / 1 km) sets `associationRadiusMeters`.
   - Client calls `GET /api/datapoints/chain-candidates` for a dropped pin or `GET /api/datapoints/chain-candidates/gps?gpsFixId=...` for a consent-validated fix; GPS-assisted candidate lookups never accept raw device coordinates on their own route.
     - chains that already have a node within the radius
     - nearby standalone points that can seed a new chain
   - User may: create unlinked, join an existing chain, or link to a standalone neighbor (server creates chain and attaches both).
3. **Historical date/time**
   - Date-time picker allows past `eventDate` values (`DATETIMEOFFSET`); validation rejects impossible calendar values but not “old” dates.
4. **Visibility**
   - Explicit control: Public / Friends-only / Private (maps to `Visibility` tinyint).
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

## 8. Social Features

### MVP Scope

- User profiles (display name, avatar, bio)
- Follow/unfollow
- Comments on data points
- Reactions (like)
- Activity feed (chronological, followed users' posts)
- Content reporting
- Data point tags (system + custom) and location chains/timelines (see §7)

### Post-MVP

- Push notifications (Azure Notification Hubs)
- Direct messaging
- Full-text / tag-faceted global search beyond map context
- Trending locations
- Collections / curated maps

## 9. Infrastructure (Terraform Updates)

### New Resources

| Resource | Purpose |
|---|---|
| `azurerm_storage_account` (media) | User-uploaded photos/videos |
| `azurerm_storage_queue` (or Service Bus queue/topic) | Durable media-work queue consumed by the outbox dispatcher and thumbnail/moderation workers |
| `azurerm_role_assignment` on the media storage account | Grant `ReMind.Api` least-privilege SAS issuance roles (`Storage Blob Delegator` plus blob data role needed for existence/metadata checks) and grant the thumbnail worker blob read/write rights |
| `azurerm_cdn_frontdoor_profile` + endpoint/origin-group/origin/route (Premium tier) | Front Door entry for both media delivery and the public `ReMind.Api` hostname |
| `azurerm_cdn_frontdoor_firewall_policy` + `azurerm_cdn_frontdoor_security_policy` | Front Door WAF rules plus association of that WAF policy to the API and media routes/domains |
| `azurerm_key_vault` | Secrets that cannot use identity-based access (e.g. Azure Maps key if required; not storage account keys — Blob SAS uses user-delegation via managed identity) |
| `azurerm_application_insights` | Monitoring and diagnostics |
| `azurerm_log_analytics_workspace` | Centralized logging |
| `azurerm_virtual_network` + subnets | Private network boundary for SQL private endpoint and app integration |
| `azurerm_private_endpoint` + `azurerm_private_dns_zone` + **zone VNet link** + **private DNS zone group** on the endpoint | Private SQL connectivity and name resolution from the integrated VNet |
| `azurerm_app_service_virtual_network_swift_connection` (or Container Apps VNet integration) | Allow `ReMind.Api` (and thumbnail Function if applicable) to reach SQL over the private endpoint |
| `azurerm_user_assigned_identity` (or system-assigned) + KV access policies/RBAC + SQL AAD admin/user + Blob/Queue RBAC | Managed identities for API and Function; no plaintext `SqlConnectionString` in app settings; grant the API `Storage Blob Delegator` + least-privilege blob access to mint user-delegation SAS, and grant dispatcher/thumbnail workers only the blob/queue roles required to read source uploads, write thumbnails, and ack queue work |
| Azure Maps account (or equivalent geocoder) | Server-side place/address/city/state/country search |

### Modified Resources

| Resource | Change |
|---|---|
| `azurerm_service_plan` | Upgrade from B1 to P1v3 or switch to Container Apps |
| `azurerm_linux_web_app` | Repurpose for `ReMind.Api`; add a separate Static Web Apps/Storage static website/Front Door origin (or documented external host) for `ReMind.Web`; Key Vault references + VNet integration; restrict direct origin ingress so only Front Door reaches the public API origin |
| `azurerm_linux_function_app` | Keep for thumbnail generation, outbox dispatcher (if not in-process), and background jobs; managed identity + deterministic thumbnail paths + app settings / trigger bindings that point the workers at the provisioned media-work queue |
| `azurerm_mssql_database` | Upgrade SKU from Basic to S0+ for spatial index performance |
| `azurerm_mssql_server` | Disable public network access when the private endpoint is live |

### Manual/External Resources (Not in Terraform)

- Entra External ID tenant creation (Azure Portal / CLI)
- Social identity provider app registrations (Google Cloud Console, Apple Developer, Meta for Developers)
- DNS / custom domain configuration

## 10. DevOps & Testing

### CI/CD Updates

- Add Node.js build step for React SPA (`npm ci`, `npm run build`, `npm test`)
- Add React Native build validation (TypeScript check, Jest tests)
- Build the EF Core migration bundle in CI, then execute it from a VNet-connected/self-hosted runner or Azure-side migration job that can reach private SQL; remove the `ReMind.Database` `azure/sql-action` path before the first database deployment.
- Playwright tests target React SPA (not Razor), including create (map pin, tags, visibility, chain link) and search (nearby, place, timeline) flows
- Load testing for proximity and viewport queries (k6 or Azure Load Testing)

### Test Strategy

| Layer | Tool | Scope |
|---|---|---|
| Unit tests | xUnit + Moq | Business logic, validators, spatial query builders, chain association, tag normalization |
| Integration tests | xUnit + TestContainers | EF Core + SQL Server with spatial types, chain timeline cursors, outbox commit |
| E2E tests | Playwright | React SPA create/search/timeline/media flows |
| Mobile tests | Jest + Detox/Maestro | React Native screens and API integration |

## 11. Security & Compliance

### Data Privacy (GDPR / CCPA)

- Location data is personal data.
- **Consent**: `UserConsents` (or equivalent) stores `UserId`, `Purpose` (`LocationGps`), `PolicyVersion`, `Granted`, `RecordedUtc`, `ClientUtc`, `RevokedUtc`. GPS-assisted create/search requires a non-revoked grant for the current policy version, and the server enforces that rule both when minting short-lived `gpsFixId` handles and again whenever one is redeemed (client prompts alone are not sufficient). Map-pin coordinates remain personal data covered by the privacy policy and retention/erasure flows.
- **GPS fix retention**: `GpsFixes` are transient personal-location records; each handle expires quickly, a scheduled janitor purges expired rows on a short cadence, and the erasure workflow deletes any remaining fixes immediately.
- Data retention policy: auto-delete posts after N years (configurable)
- Right to erasure: `POST /api/users/me/erasure-requests` starts an asynchronous, retryable cleanup workflow recorded in `UserErasureRequests` that stores an opaque status receipt hash plus the request's ownership on a non-authenticating tombstone subject so status remains readable through completion even after the original `Users` row is scrubbed; the workflow (1) stops issuing new signed media URLs, relies on short token TTLs for any already-issued URLs, deletes blobs/thumbnails, and purges Front Door cache entries (with account-wide user-delegation-key revocation reserved for exceptional blast-radius events), (2) tombstones/cancels any queued or yet-to-be-published media work, requires dispatchers/workers to re-read current media/user deletion state before publishing messages or writing thumbnails, and deletes or anonymizes dependent SQL rows in FK-safe order—`GpsFixes`, `UserConsents`, `Follows`, `Reactions`, `Reports`, `Comments`, custom-tag ownership/unused custom tags, the user’s `Media`/`DataPoints`, corresponding `OutboxMessages`, retained `UserExportRequests` audit rows, then empty or ownership-reassigned `DataPointChains` / retained `Tags` moved to the non-authenticating tombstone user row—and only then (3) deletes or scrubs the original `Users` row; `GET /api/users/me/erasure-requests/{id}` (authenticated caller plus the receipt, or an already-authenticated pre-erasure session) reports auditable completion status and the durable job state
- Data export: `POST /api/users/me/export-requests` starts a full export job recorded in `UserExportRequests`, persists only the minimum audit metadata plus an opaque download receipt hash, stamps each generated archive with `DownloadExpiresUtc`, serves it only through an authenticated short-lived read URL while that receipt is valid, and has the janitor delete the export blob at expiry while retaining/anonymizing only the audit row; `GET /api/users/me/export-requests/{id}` returns the durable job state plus the authenticated download when ready

### Application Security

- All secrets in Key Vault; grant ReMind.Api and the thumbnail Function managed identities least-privilege access and use Key Vault references or managed-identity-based SQL connections instead of plaintext app settings. Prefer Entra-only SQL access; if a bootstrap/break-glass SQL admin credential is still required, provision and rotate it outside Terraform state as an explicit operational exception.
- CORS restricted to known frontend origins
- Input validation: sanitize HTML in descriptions, validate file uploads, bound radii/page sizes, normalize tags
- SQL injection: EF Core parameterization for runtime/ad-hoc queries; allow migration-time raw SQL only for controlled schema operations such as `CREATE SPATIAL INDEX`
- Rate limiting on auth endpoints, place search, and media upload
- Content Security Policy headers on the React SPA

### Network Security

- SQL Server public network access disabled in production; provision a VNet, private endpoint, private DNS zone, **VNet link**, **DNS zone group**, and API/App Service/Container Apps VNet integration so the API can resolve and reach SQL
- Blob uploads remain direct-from-client, so the upload endpoint must stay publicly reachable; rely on anonymous-access disabled, strict CORS, short-lived create-only SAS, and per-blob scoping instead of a private-only Blob endpoint for that path
- For environments that require public access, use narrow explicit IP firewall rules and do not enable “Allow Azure services”
- Front Door WAF rules (OWASP top 10) protect both the API and media hostnames, and the API origin rejects direct public ingress that does not arrive from Front Door

## 12. Implementation Phases

### Pre-Phase 1: Solution and platform refactor
- Finalize the target solution ownership boundaries before feature work: `ReMind.Api`, `ReMind.Web`, `ReMind.Core`, `ReMind.Data`, and `ReMind.Mobile`
- Decide the retirement path for `ReMind.Frontend`, `ReMind.Functions`, and `ReMind.Database` so the new API/data/frontend layers become the only source of truth
- Stand up the new frontend/API/data projects and CI wiring early so later phases refactor into the correct seams rather than retrofitting them afterward

### Phase 1: Foundation
- Set up Entra External ID tenant and app registrations (API resource app + SPA + mobile)
- Redesign schema: `GEOGRAPHY` data points, chains, tags, consents, media status, outbox
- Create `ReMind.Api` with JWT auth, fallback authorization policy, EF Core + NetTopologySuite
- Scaffold `ReMind.Web` React SPA with map component (Leaflet/Mapbox)

### Phase 2: Data point UX
- Create data point flow: map pin / GPS, historical `eventDate`, visibility, tags, chain candidates + association radius
- Search flows: nearby radius, place/address geocoding (Azure Maps), map markers + list, `/in-bounds` viewport refine
- Detail view with chain timeline scrubber and add-entry-from-chain
- User profiles + consent APIs needed to support authenticated create/search flows and GPS-based entry points

### Phase 3: Media and user lifecycle
- Blob Storage media upload pipeline with transactional outbox and idempotent thumbnails
- Minimal admin/user-management support for Phase 3 validation: admin role assignment plus the moderation queue/approve/reject surfaces and owner/admin pending-media preview paths
- Admin moderation queue + approve/reject transitions so Phase 3 uploads can move from `PendingModeration` to `Ready` or `Rejected`
- Keep media non-public until moderation approval exists; owner/admin preview paths may exist, but normal read APIs expose only `Ready` media
- User data export + erasure request/status flows with background cleanup workers

### Phase 4: Social
- Follow/unfollow
- Comments and reactions (gated by post visibility)
- Activity feed
- Content reporting

### Phase 5: Mobile + Polish
- React Native app with the same create/search/timeline UX
- Push notifications
- Content moderation automation/polish
- Performance optimization (caching, CDN, spatial index tuning)
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
    ExternalObjectId NVARCHAR(100) NOT NULL UNIQUE,
    DisplayName      NVARCHAR(200) NOT NULL,
    AvatarBlobPath   NVARCHAR(400) NULL,
    Bio              NVARCHAR(1000) NULL,
    CreatedUtc       DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    DeletedUtc       DATETIME2(7) NULL
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
    Visibility      TINYINT NOT NULL DEFAULT 0,  -- 0=Public, 1=FriendsOnly, 2=Private
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

- **Users**: profile info, Entra External ID object ID, display name, avatar blob path, bio; user erasure stays asynchronous and only deletes the `Users` row after dependent posts/media cleanup finishes
- **UserConsents**: auditable GPS/location consent grants and revocations (`Purpose`, `PolicyVersion`, `Granted`, timestamps) enforced server-side for GPS-sourced operations
- **DataPointChains**: logical timeline grouping related events at/near a place; membership is via `DataPoints.ChainId`
- **Tags** / **DataPointTags**: many-to-many labels; `IsSystem = 1` rows are the predetermined catalog (seeded), custom tags are user-created with unique normalized names
- **Media**: `MediaId`, `DataPointId`, `UploadId`, `BlobPath`, `ThumbnailPath`, `MediaType` (Photo/Video), `Status` (`PendingUpload` / `Quarantined` / `Processing` / `Ready` / `Rejected`), `SortOrder`; unique constraints on `UploadId` and (`DataPointId`, `BlobPath`); only `Ready` media is returned on read paths
- **OutboxMessages**: transactional outbox rows written in the same SQL transaction as media state changes (`OutboxId`, `Type`, `Payload`, `CreatedUtc`, `ProcessedUtc`, `Attempts`) so queue publication is durable and retryable
- **Comments**: `CommentId`, `DataPointId`, `UserId`, `Text`, `CreatedUtc`, `ParentCommentId` (threading)
- **Reactions**: `ReactionId`, `DataPointId`, `UserId`, `ReactionType` (Like, Love, etc.); unique constraint on (`DataPointId`, `UserId`)
- **Follows**: `FollowerId`, `FolloweeId`, `CreatedUtc`; unique constraint on (`FollowerId`, `FolloweeId`)
- **Reports**: `ReportId`, `ReporterId`, `DataPointId`/`CommentId`, `Reason`, `Status`, `CreatedUtc`

### Chain Association Rules

- A **chain** is an ordered set of data points (by `EventDate`, then `DataPointId`) that share a `ChainId`.
- When creating a point at location *L* with association radius *R* (meters, user-selected), the API proposes:
  1. Existing chains that have **any visible node** within `STDistance(node.Location, L) <= R`
  2. Standalone (unchained) visible data points within *R* that can be merged into a **new** chain with the new point
- Joining a chain attaches the new row’s `ChainId`; optionally promotes a selected standalone neighbor into the same new chain in one transaction.
- Chain membership does not require identical coordinates—only proximity of at least one node within the chosen radius at link time.
- Timeline reads for a chain return all non-deleted members the caller is allowed to see (visibility matrix), ordered by `EventDate`.

### Tag Rules

- Predetermined (system) tags are seeded and immutable in name; clients list them from `GET /api/tags`.
- Users may attach multiple tags per data point (system and/or custom).
- Custom tags are created on demand (`NormalizedName` = trimmed, case-folded); concurrent creates collide on the unique index and return the existing tag (idempotent).
- Tag attach/detach is author-only for the data point.

### Spatial Design Decisions

- Use `GEOGRAPHY` (not `GEOMETRY`) for real-world lat/long with `STDistance`, `STWithin`
- SRID 4326 (WGS 84) — the GPS standard
- `DATETIMEOFFSET` for `EventDate` so historical dates carry timezone context; clients may set past event times explicitly on create/update
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

Never accept `userId` from the client request body or query string for authorization. Resolve the internal `Users.UserId` server-side from the validated JWT (`oid` / `sub` → `Users.ExternalObjectId`), creating the profile row on first authenticated request if needed. All visibility, ownership, and follow-graph checks use that server-resolved ID only.

### Proximity Query Example

```csharp
var userLocation = new Point(longitude, latitude) { SRID = 4326 };
// Resolve caller identity server-side from the validated token (iss/sub → Users.ExternalObjectId).
// Never accept currentUserId from the request body/query; clients must not supply caller identity.
Guid currentUserId = await userDirectory.GetCurrentUserIdAsync(HttpContext.User, cancellationToken);
IReadOnlyCollection<Guid> followedAuthorIds =
    await followService.GetFolloweeIdsAsync(currentUserId, cancellationToken);
double? cursorDistance = request.Cursor?.DistanceMeters;
long? cursorDataPointId = request.Cursor?.DataPointId;

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
        || (x.DataPoint.Visibility == Visibility.FriendsOnly
            && followedAuthorIds.Contains(x.DataPoint.UserId)))
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
    .ToListAsync();

// nextCursor = nearby.Count == 0
//     ? null
//     : (nearby.Last().DistanceMeters, nearby.Last().DataPoint.DataPointId)
```

Chain-candidate lookup for create uses the same visibility predicate and `Distance <= associationRadiusMeters`, grouping matches by `ChainId` (plus standalone neighbors). Timeline queries filter `ChainId == chainId`, apply visibility, and order by `EventDate` / `DataPointId` with a date cursor—not distance.

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
    options.AddPolicy("CanModerate", p => p.RequireRole("Moderator", "Admin"));
});
```

JWT validation alone is not enough: register authorization with a **fallback authenticated policy** so endpoints are not anonymously callable by default. Apply endpoint metadata / policies for visibility checks (resource-based handlers) and admin-only routes (`CanModerate`). Map `User.GetObjectId()` (or equivalent claim) to `Users.UserId` inside the API boundary; do not trust client-supplied user IDs.

### Authorization Model

| Action | Rule |
|---|---|
| View public posts | Any authenticated user |
| View friends-only posts | Follower of the author |
| View private posts | Author only |
| Create post | Any authenticated user (GPS/location consent recorded when using device GPS) |
| Link post to chain | Any authenticated user who can view at least one proposed chain node; new chain creator becomes `CreatedByUserId` |
| Edit/delete own post | Author only |
| Comment | Any authenticated user who can view the target post |
| React | Any authenticated user who can view the target post |
| Tag own post | Author only |
| Report | Any authenticated user |
| Moderate | Admin role (custom claim) |
## 5. API Design

### Endpoints

```
POST   /api/datapoints                  # Create: title, description, eventDate (past allowed), visibility, lat/lng OR map-picked point, optional chainId / linkToDataPointId, associationRadiusMeters, tagIds[] + customTagNames[]
GET    /api/datapoints/nearby           # Proximity search (lat [-90,90], lng [-180,180], radiusMeters 1-50000 default 250, cursor, pageSize default 25 max 100); visibility via JWT user + follow graph
GET    /api/datapoints/in-bounds        # Map viewport query (north/south/east/west, optional zoom); same visibility rules; used when the user pans/zooms to refine results
GET    /api/datapoints/search/place     # Geocode address/city/state/country via Azure Maps, then return matching nearby/in-bounds points + suggested map bounds
GET    /api/datapoints/{id}             # Detail + Ready media + tags + chain summary (neighbor counts / adjacent timeline cursors)
PUT    /api/datapoints/{id}             # Update own data point (including visibility, eventDate, tags, optional chain relink within rules)
DELETE /api/datapoints/{id}             # Soft-delete own data point

GET    /api/datapoints/chain-candidates # Given lat/lng + associationRadiusMeters, list joinable chains and nearby standalone points the caller can see
GET    /api/chains/{chainId}/timeline   # Chain events ordered by EventDate (default most-recent first); cursor = (eventDate, dataPointId); includes detail payload suitable for scrubbing a timeline
POST   /api/chains                      # Explicitly create an empty/titled chain (optional; usually created inline on first linked post)
POST   /api/chains/{chainId}/datapoints # Add a new data point into an existing chain (location defaults to selected map/GPS point; must satisfy association radius vs some visible node unless author override policy says otherwise)

GET    /api/tags                        # List system tags + caller's recent custom tags (search q= optional)
POST   /api/tags                        # Create custom tag (idempotent on NormalizedName)

POST   /api/datapoints/{id}/media       # Issue a server-generated uploadId/blob key plus a short-lived, write-only user-delegation Blob SAS (minted via the API managed identity) for media owned by the caller
POST   /api/datapoints/{id}/media/complete # Validate blob; in one SQL transaction upsert media Status=Quarantined/Processing and write OutboxMessages; retrying dispatcher publishes queue work
POST   /api/datapoints/{id}/comments    # Add comment (caller must be allowed to view the post)
GET    /api/datapoints/{id}/comments    # List comments
POST   /api/datapoints/{id}/reactions   # Add/update reaction (caller must be allowed to view the post)

GET    /api/users/{id}                  # Get user profile
PUT    /api/users/me                    # Update own profile
POST   /api/users/me/consents           # Record location/GPS consent (purpose, policyVersion, granted, client timestamp)
GET    /api/users/me/consents           # Current consent state used to gate GPS-assisted create/search
POST   /api/users/me/export-requests    # Request a full user-data export job
GET    /api/users/me/export-requests/{id} # Check export status and download when ready
POST   /api/users/me/erasure-requests   # Request async user-data erasure across SQL, blobs, thumbnails, and CDN caches
GET    /api/users/me/erasure-requests/{id} # Check erasure status until auditable completion
PUT    /api/users/{id}/follow           # Follow
DELETE /api/users/{id}/follow           # Unfollow

GET    /api/feed                        # Activity feed (followed users' posts)
POST   /api/reports                     # Report content
GET    /api/moderation/queue            # Admin: moderation queue (CanModerate policy)
```

### Create request contract (illustrative)

```json
{
  "title": "Market square, 1890",
  "description": "...",
  "eventDate": "1890-06-12T00:00:00+01:00",
  "visibility": "Public",
  "location": { "latitude": 52.52, "longitude": 13.405, "source": "MapPin" },
  "associationRadiusMeters": 200,
  "chainId": null,
  "linkToDataPointId": 12345,
  "tagIds": ["..."],
  "customTagNames": ["Cobblestones"]
}
```

`location.source` is `MapPin` or `Gps`. GPS-sourced creates require a stored, non-revoked location consent for the current policy version; map-pin-only creates do not require device GPS consent but still treat coordinates as personal data under the privacy policy.

### Design Notes

- Pagination via cursor-based (not OFFSET) for large result sets; nearby cursors are `(distanceMeters, dataPointId)`, timeline cursors are `(eventDate, dataPointId)`
- Reject invalid coordinates/radius/page size/bounds with HTTP 400 before `Point(...)`, `STDistance`, and `Take(...)`
- Bounding-box pre-filter before `STDistance` for map viewport and place-search result windows
- Place search: Azure Maps Search (or equivalent) geocodes the query string → center/bbox → same visibility-filtered spatial query; response includes `mapBounds` so the client can fit all returned points
- Viewport refine: client debounces `moveend`/`zoomend`, calls `/in-bounds`, replaces markers + list; narrowing the map narrows the query
- Rate limiting via `AspNetCoreRateLimit` or Azure Front Door WAF
- All read paths (including `/nearby`) evaluate visibility with the authenticated caller ID resolved from the JWT (not a client-supplied user id) plus follow relationships
- `/nearby` responses retain the SQL-computed `DistanceMeters` so continuation cursors use `(distance, DataPointId)` from the last returned row
- OpenAPI/Swagger for API documentation
- SignalR hub for real-time map updates (post-MVP)

## 6. Media Storage

### Architecture

```
Client → API (request upload URL)
       → API uses its managed identity to obtain a user-delegation key and mints a short-lived, write-only Blob SAS for a server-owned uploadId/blob key
       → Client uploads directly to Blob Storage
       → Client confirms upload → API validates the blob exists and matches expected size/signature,
         then in one SQL transaction creates or returns the unique media record (Status = Quarantined/Processing)
         and inserts a transactional outbox row for thumbnail work
       → Retrying outbox dispatcher publishes the queue message (at-least-once) until acknowledged
       → Azure Function (queue trigger) generates thumbnail idempotently to a deterministic path and creates or updates media thumbnail fields
       → Media.Status becomes Ready only after validation + thumbnail success; read APIs and signed URLs
         only expose Ready media the caller is allowed to see for the parent data point
       → Front Door / signed URLs serve approved thumbnails and media
```

### Decisions

- **Container structure**: `media/{userId}/{dataPointId}/{uploadId}/{fileName}` with deterministic thumbnail path `.../thumb.jpg` derived from `UploadId` (not random names)
- **Allowed types**: JPEG, PNG, WebP, MP4, MOV (validate blob signatures/content server-side, not only client-supplied MIME + extension, before publishing)
- **Max file size**: 50 MB photos, 500 MB videos
- **Upload identity**: the initial upload call issues the `UploadId` and canonical blob key; completion is idempotent on that `UploadId` (unique constraint)
- **Media outbox**: record thumbnail/moderation work in the same SQL transaction as the media row; a retrying dispatcher publishes to the queue so a crash between commit and enqueue cannot leave media permanently unprocessed
- **Thumbnail worker**: queue delivery is at-least-once, so thumbnail blob paths must be deterministic (`media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`); the Function must short-circuit if the thumbnail already exists, idempotently overwrite/create the blob, and upsert media thumbnail metadata without duplicate side effects
- **Blob SAS**: mint user-delegation SAS tokens with the API managed identity (no retained storage account keys); reserve Key Vault for secrets that cannot use identity-based access
- **CDN**: Azure Front Door Standard/Premium (`azurerm_cdn_frontdoor_*`) for media delivery; keep the blob origin private where supported and issue short-lived, visibility-checked signed/authenticated read URLs (never store long-lived public CDN URLs as the sole access control)
- **Moderation**: Azure Content Safety or manual review queue for uploaded media while Status remains non-Ready
- **Thumbnails**: Azure Function on queue trigger using `SixLabors.ImageSharp` for images and FFmpeg or Azure Video Indexer for MP4/MOV frame thumbnails

## 7. Data Point User Experience

This section is the product contract for create/search flows on web and mobile.

### 7.1 Creating a data point

1. **Location**
   - **Map pin**: user pans/zooms the map and drops a pin (default create path).
   - **GPS**: user opts in; client reads device coordinates only after `POST /api/users/me/consents` shows grant for the current location-policy version (or records a new grant). Revocation blocks further GPS use until re-granted.
2. **Near / association radius**
   - UI control (slider or presets, e.g. 50 m / 100 m / 250 m / 1 km) sets `associationRadiusMeters`.
   - Client calls `GET /api/datapoints/chain-candidates` for the pin + radius and shows:
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
   - Search center = map center, dropped pin, or GPS (with consent).
   - User-selected `radiusMeters` (same bounds/default as create association).
   - `GET /api/datapoints/nearby` returns points the caller may view.
2. **Map + list**
   - Results render as map markers and a selectable list synchronized with marker selection.
3. **Place / address search**
   - Free-text address, city, state, or country → `GET /api/datapoints/search/place`.
   - API geocodes via Azure Maps, runs the spatial query, returns points + `mapBounds`.
   - Client fits the map to `mapBounds` so all returned points are visible.
4. **Viewport refine**
   - After place search or nearby search, panning/zooming calls `GET /api/datapoints/in-bounds` (debounced). Narrowing the viewport refines the result set; expanding widens it.
5. **Detail + media**
   - Selecting a point opens detail: title, description, event date, visibility badge (if owner), tags, author, and attached Ready media with signed read URLs.
6. **Chain timeline**
   - If the point has a `chainId`, detail defaults to the **most recent visible** event in that chain and exposes a horizontal/vertical timeline scrubber.
   - User scrolls backward/forward through chain nodes ordered by `EventDate` (`GET /api/chains/{chainId}/timeline`).
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
| `azurerm_cdn_frontdoor_profile` + endpoint/origin-group/origin/route (Standard/Premium) | Media delivery |
| `azurerm_key_vault` | Secrets that cannot use identity-based access (e.g. Azure Maps key if required; not storage account keys — Blob SAS uses user-delegation via managed identity) |
| `azurerm_application_insights` | Monitoring and diagnostics |
| `azurerm_log_analytics_workspace` | Centralized logging |
| `azurerm_virtual_network` + subnets | Private network boundary for SQL private endpoint and app integration |
| `azurerm_private_endpoint` + `azurerm_private_dns_zone` + **zone VNet link** + **private DNS zone group** on the endpoint | Private SQL connectivity and name resolution from the integrated VNet |
| `azurerm_app_service_virtual_network_swift_connection` (or Container Apps VNet integration) | Allow `ReMind.Api` (and thumbnail Function if applicable) to reach SQL over the private endpoint |
| `azurerm_user_assigned_identity` (or system-assigned) + KV access policies/RBAC + SQL AAD admin/user | Managed identities for API and Function; no plaintext `SqlConnectionString` in app settings |
| Azure Maps account (or equivalent geocoder) | Server-side place/address/city/state/country search |

### Modified Resources

| Resource | Change |
|---|---|
| `azurerm_service_plan` | Upgrade from B1 to P1v3 or switch to Container Apps |
| `azurerm_linux_web_app` | Repurpose for `ReMind.Api`; add a separate Static Web Apps/Storage static website/Front Door origin (or documented external host) for `ReMind.Web`; Key Vault references + VNet integration |
| `azurerm_linux_function_app` | Keep for thumbnail generation, outbox dispatcher (if not in-process), and background jobs; managed identity + deterministic thumbnail paths |
| `azurerm_mssql_database` | Upgrade SKU from Basic to S0+ for spatial index performance; disable public access when private endpoint is live |

### Manual/External Resources (Not in Terraform)

- Entra External ID tenant creation (Azure Portal / CLI)
- Social identity provider app registrations (Google Cloud Console, Apple Developer, Meta for Developers)
- DNS / custom domain configuration

## 10. DevOps & Testing

### CI/CD Updates

- Add Node.js build step for React SPA (`npm ci`, `npm run build`, `npm test`)
- Add React Native build validation (TypeScript check, Jest tests)
- Build and execute the EF Core migration bundle in the database deployment workflow; remove the `ReMind.Database` `azure/sql-action` path before the first database deployment.
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
- **Consent**: `UserConsents` (or equivalent) stores `UserId`, `Purpose` (`LocationGps`), `PolicyVersion`, `Granted`, `RecordedUtc`, `ClientUtc`, `RevokedUtc`. GPS-assisted create/search requires a non-revoked grant for the current policy version; enforcement is server-side when `location.source = Gps` (client prompts alone are not sufficient). Map-pin coordinates remain personal data covered by the privacy policy and retention/erasure flows.
- Data retention policy: auto-delete posts after N years (configurable)
- Right to erasure: `POST /api/users/me/erasure-requests` starts an asynchronous, retryable cleanup workflow that deletes user data, media/thumbnail blobs, and cached media, while `GET /api/users/me/erasure-requests/{id}` reports auditable completion status
- Data export: `POST /api/users/me/export-requests` starts a full export job and `GET /api/users/me/export-requests/{id}` returns status plus the authenticated download when ready

### Application Security

- All secrets in Key Vault; grant ReMind.Api and the thumbnail Function managed identities least-privilege access and use Key Vault references or managed-identity-based SQL connections instead of plaintext app settings
- CORS restricted to known frontend origins
- Input validation: sanitize HTML in descriptions, validate file uploads, bound radii/page sizes, normalize tags
- SQL injection: EF Core parameterization for runtime/ad-hoc queries; allow migration-time raw SQL only for controlled schema operations such as `CREATE SPATIAL INDEX`
- Rate limiting on auth endpoints, place search, and media upload
- Content Security Policy headers on the React SPA

### Network Security

- SQL Server public network access disabled in production; provision a VNet, private endpoint, private DNS zone, **VNet link**, **DNS zone group**, and API/App Service/Container Apps VNet integration so the API can resolve and reach SQL
- For environments that require public access, use narrow explicit IP firewall rules and do not enable “Allow Azure services”
- Front Door WAF rules (OWASP top 10)

## 12. Implementation Phases

### Phase 1: Foundation
- Set up Entra External ID tenant and app registrations (API resource app + SPA + mobile)
- Redesign schema: `GEOGRAPHY` data points, chains, tags, consents, media status, outbox
- Create `ReMind.Api` with JWT auth, fallback authorization policy, EF Core + NetTopologySuite
- Scaffold `ReMind.Web` React SPA with map component (Leaflet/Mapbox)

### Phase 2: Core map UX
- Create data point flow: map pin / GPS, historical `eventDate`, visibility, tags, chain candidates + association radius
- Search flows: nearby radius, place/address geocoding (Azure Maps), map markers + list, `/in-bounds` viewport refine
- Detail view with Ready media and chain timeline scrubber; add-entry-from-chain
- Blob Storage media upload pipeline with transactional outbox and idempotent thumbnails
- User profiles + consent APIs

### Phase 3: Social
- Follow/unfollow
- Comments and reactions (gated by post visibility)
- Activity feed
- User data export + erasure request/status flows with background cleanup workers

### Phase 4: Mobile + Polish
- React Native app with the same create/search/timeline UX
- Push notifications
- Content moderation
- Performance optimization (caching, CDN, spatial index tuning)
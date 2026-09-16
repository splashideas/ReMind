# ReMind Architecture Design

## Overview

ReMind is a map-based social platform that lets users pin historical data points (text, dates, photos, videos) to real-world locations. Users interact through a React web app or a React Native mobile app (iOS/Android). The backend is a .NET API on Azure, using Azure SQL spatial types for proximity queries and Microsoft Entra External ID for authentication.

## Current State vs. Target

| Aspect | Current | Target |
|---|---|---|
| Frontend | ASP.NET Core Razor Pages (`ReMind.Frontend`) | React SPA + React Native mobile app |
| API | Azure Functions with function-key auth | ASP.NET Core Web API on App Service / Container Apps with JWT |
| Database | `dbo.DataPoints` with `Location NVARCHAR(200)` | Azure SQL with `GEOGRAPHY` spatial column + spatial index |
| Data Access | Raw `SqlConnection` + inline SQL | EF Core + `Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite` |
| Auth | Function keys | Microsoft Entra External ID (JWT bearer tokens) |
| Media | None | Azure Blob Storage + CDN |
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

CREATE TABLE dbo.DataPoints (
    DataPointId     BIGINT IDENTITY(1,1) PRIMARY KEY,
    UserId          UNIQUEIDENTIFIER NOT NULL,
    Title           NVARCHAR(200) NOT NULL,
    Description     NVARCHAR(MAX) NOT NULL,
    EventDate       DATETIMEOFFSET NOT NULL,
    Location        GEOGRAPHY NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedUtc      DATETIME2(7) NULL,
    Visibility      TINYINT NOT NULL DEFAULT 0,  -- 0=Public, 1=FriendsOnly, 2=Private
    IsDeleted       BIT NOT NULL DEFAULT 0,
    CONSTRAINT CK_DataPoints_Location_SRID CHECK (Location.STSrid = 4326),
    CONSTRAINT CK_DataPoints_Visibility CHECK (Visibility IN (0, 1, 2)),
    CONSTRAINT FK_DataPoints_Users FOREIGN KEY (UserId)
        REFERENCES dbo.Users (UserId)
        ON DELETE NO ACTION
);

CREATE SPATIAL INDEX IX_DataPoints_Location
ON dbo.DataPoints (Location)
USING GEOGRAPHY_AUTO_GRID;
```

### Supporting Tables

- **Users**: profile info, Entra External ID object ID, display name, avatar blob path, bio; user erasure stays asynchronous and only deletes the `Users` row after dependent posts/media cleanup finishes
- **Media**: `MediaId`, `DataPointId`, `UploadId`, `BlobPath`, `ThumbnailPath`, `MediaType` (Photo/Video), `SortOrder`; unique constraints on `UploadId` and (`DataPointId`, `BlobPath`)
- **Comments**: `CommentId`, `DataPointId`, `UserId`, `Text`, `CreatedUtc`, `ParentCommentId` (threading)
- **Reactions**: `ReactionId`, `DataPointId`, `UserId`, `ReactionType` (Like, Love, etc.); unique constraint on (`DataPointId`, `UserId`)
- **Follows**: `FollowerId`, `FolloweeId`, `CreatedUtc`; unique constraint on (`FollowerId`, `FolloweeId`)
- **Reports**: `ReportId`, `ReporterId`, `DataPointId`/`CommentId`, `Reason`, `Status`, `CreatedUtc`

### Spatial Design Decisions

- Use `GEOGRAPHY` (not `GEOMETRY`) for real-world lat/long with `STDistance`, `STWithin`
- SRID 4326 (WGS 84) — the GPS standard
- `DATETIMEOFFSET` for `EventDate` so historical dates carry timezone context
- Spatial index is critical for proximity query performance

## 3. EF Core + NetTopologySuite

### Package

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer.NetTopologySuite" Version="10.0.0" />
```

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

### Proximity Query Example

```csharp
var userLocation = new Point(longitude, latitude) { SRID = 4326 };
// Resolve caller identity server-side from the validated token (iss/sub → Users.ExternalObjectId).
// Never accept currentUserId from the request body/query; clients must not supply caller identity.
Guid currentUserId = await userDirectory.GetCurrentUserIdAsync(HttpContext.User, cancellationToken);
IReadOnlyCollection<Guid> followedAuthorIds = await followService.GetFolloweeIdsAsync(currentUserId, cancellationToken);
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

// nextCursor = (nearby.Last.DistanceMeters, nearby.Last.DataPoint.DataPointId)
```

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
```

### Authorization Model

| Action | Rule |
|---|---|
| View public posts | Any authenticated user |
| View friends-only posts | Follower of the author |
| View private posts | Author only |
| Create post | Any authenticated user |
| Edit/delete own post | Author only |
| Comment | Any authenticated user who can view the target post |
| React | Any authenticated user who can view the target post |
| Report | Any authenticated user |
| Moderate | Admin role (custom claim) |

## 5. API Design

### Endpoints

```
POST   /api/datapoints                  # Create a data point
GET    /api/datapoints/nearby           # Proximity search (latitude [-90,90], longitude [-180,180], radiusMeters 1-50000, cursor, pageSize default 25 max 100, visibility enforced against caller + follow graph)
GET    /api/datapoints/{id}             # Get single data point with media
PUT    /api/datapoints/{id}             # Update own data point
DELETE /api/datapoints/{id}             # Soft-delete own data point

POST   /api/datapoints/{id}/media       # Issue a server-generated uploadId/blob key plus a short-lived, write-only user-delegation Blob SAS (minted via the API managed identity) for media owned by the caller
POST   /api/datapoints/{id}/media/complete # Validate uploaded blob metadata for the uploadId; in one SQL transaction create-or-return the unique media record and write a transactional outbox row; a retrying dispatcher publishes queue work from the outbox
POST   /api/datapoints/{id}/comments    # Add comment
GET    /api/datapoints/{id}/comments    # List comments
POST   /api/datapoints/{id}/reactions   # Add/update reaction

GET    /api/users/{id}                  # Get user profile
PUT    /api/users/me                    # Update own profile
POST   /api/users/me/export-requests    # Request a full user-data export job
GET    /api/users/me/export-requests/{id} # Check export status and download when ready
POST   /api/users/me/erasure-requests   # Request async user-data erasure across SQL, blobs, thumbnails, and CDN caches
GET    /api/users/me/erasure-requests/{id} # Check erasure status until auditable completion
PUT    /api/users/{id}/follow           # Follow
DELETE /api/users/{id}/follow           # Unfollow

GET    /api/feed                        # Activity feed (followed users' posts)
POST   /api/reports                     # Report content
GET    /api/moderation/queue            # Admin: moderation queue
```

### Design Notes

- Pagination via cursor-based (not OFFSET) for large result sets
- Reject invalid nearby-query coordinates/radius/page size with HTTP 400 and enforce the documented bounds before `Point(...)`, `STDistance`, and `Take(...)`
- Bounding-box pre-filter before `STDistance` for map viewport queries
- Rate limiting via `AspNetCoreRateLimit` or Azure Front Door WAF
- `/nearby` always evaluates visibility with the authenticated caller ID resolved from the JWT (not a client-supplied user id) plus follow relationships before returning rows
- `/nearby` responses retain the SQL-computed `DistanceMeters` so continuation cursors use `(distance, DataPointId)` from the last returned row- OpenAPI/Swagger for API documentation
- SignalR hub for real-time map updates (post-MVP)

## 6. Media Storage

### Architecture

```
Client → API (request upload URL)
       → API uses its managed identity to obtain a user-delegation key and mints a short-lived, write-only Blob SAS for a server-owned uploadId/blob key
       → Client uploads directly to Blob Storage
       → Client confirms upload → API validates the blob exists and matches expected size/signature, then in one SQL transaction create-or-returns the unique media record for that uploadId and inserts a transactional outbox row for thumbnail work
       → Retrying outbox dispatcher publishes the queue message (at-least-once) until acknowledged
       → Azure Function (queue trigger) generates thumbnail idempotently to a deterministic path and create/updates the media thumbnail fields
       → CDN serves approved thumbnails and media
```

### Decisions

- **Container structure**: `media/{userId}/{dataPointId}/{fileName}`
- **Allowed types**: JPEG, PNG, WebP, MP4, MOV (validate blob signatures/content server-side, not only client-supplied MIME + extension, before publishing)
- **Max file size**: 50 MB photos, 500 MB videos
- **Upload identity**: the initial upload call issues the `UploadId` and canonical blob key, and completion is idempotent on that `UploadId`
- **Media outbox**: record thumbnail/moderation work in the same SQL transaction as the media row; a retrying dispatcher publishes to the queue so a crash between commit and enqueue cannot leave media permanently unprocessed
- **Thumbnail worker**: queue delivery is at-least-once, so thumbnail blob paths must be deterministic (`media/{userId}/{dataPointId}/thumbnails/{uploadId}.jpg`) and the Function must idempotently overwrite/create the blob and upsert media thumbnail metadata without duplicate side effects
- **Blob SAS**: mint user-delegation SAS tokens with the API managed identity (no retained storage account keys); reserve Key Vault for secrets that cannot use identity-based access
- **CDN**: Azure Front Door Standard/Premium (`azurerm_cdn_frontdoor_*`) for media delivery; keep the blob origin private where supported and issue short-lived, visibility-checked signed/authenticated read URLs
- **Moderation**: Azure Content Safety or manual review queue for uploaded media
- **Thumbnails**: Azure Function on queue trigger using `SixLabors.ImageSharp` for images and FFmpeg or Azure Video Indexer for MP4/MOV thumbnails

## 7. Social Features

### MVP Scope

- User profiles (display name, avatar, bio)
- Follow/unfollow
- Comments on data points
- Reactions (like)
- Activity feed (chronological, followed users' posts)
- Content reporting

### Post-MVP

- Push notifications (Azure Notification Hubs)
- Direct messaging
- Hashtags / search
- Trending locations
- Collections / curated maps

## 8. Infrastructure (Terraform Updates)

### New Resources

| Resource | Purpose |
|---|---|
| `azurerm_storage_account` (media) | User-uploaded photos/videos |
| `azurerm_cdn_frontdoor_profile` + endpoint/origin-group/origin/route (Standard/Premium) | Media delivery |
| `azurerm_key_vault` | Secrets that cannot use identity-based access (not storage account keys; Blob SAS uses user-delegation via managed identity) |
| `azurerm_application_insights` | Monitoring and diagnostics |
| `azurerm_log_analytics_workspace` | Centralized logging |
| `azurerm_virtual_network` + subnets | Private network boundary for SQL private endpoint and app integration |
| `azurerm_private_endpoint` + `azurerm_private_dns_zone` | Private SQL connectivity and name resolution |
| `azurerm_app_service_virtual_network_swift_connection` (or Container Apps VNet integration) | Allow `ReMind.Api` to reach SQL over the private endpoint |

### Modified Resources

| Resource | Change |
|---|---|
| `azurerm_service_plan` | Upgrade from B1 to P1v3 or switch to Container Apps |
| `azurerm_linux_web_app` | Repurpose for `ReMind.Api`; add a separate Static Web Apps/Storage static website/Front Door origin (or documented external host) for `ReMind.Web` |
| `azurerm_linux_function_app` | Keep for thumbnail generation + background jobs |
| `azurerm_mssql_database` | Upgrade SKU from Basic to S0+ for spatial index performance |

### Manual/External Resources (Not in Terraform)

- Entra External ID tenant creation (Azure Portal / CLI)
- Social identity provider app registrations (Google Cloud Console, Apple Developer, Meta for Developers)
- DNS / custom domain configuration

## 9. DevOps & Testing

### CI/CD Updates

- Add Node.js build step for React SPA (`npm ci`, `npm run build`, `npm test`)
- Add React Native build validation (TypeScript check, Jest tests)
- Build and execute the EF Core migration bundle in the database deployment workflow; remove the `ReMind.Database` `azure/sql-action` path before the first database deployment.
- Playwright tests target React SPA (not Razor)
- Load testing for proximity queries (k6 or Azure Load Testing)

### Test Strategy

| Layer | Tool | Scope |
|---|---|---|
| Unit tests | xUnit + Moq | Business logic, validators, spatial query builders |
| Integration tests | xUnit + TestContainers | EF Core + SQL Server with spatial types |
| E2E tests | Playwright | React SPA user flows |
| Mobile tests | Jest + Detox/Maestro | React Native screens and API integration |

## 10. Security & Compliance

### Data Privacy (GDPR / CCPA)

- Location data is personal data — require explicit consent before collecting GPS
- Data retention policy: auto-delete posts after N years (configurable)
- Right to erasure: `POST /api/users/me/erasure-requests` starts an asynchronous, retryable cleanup workflow that deletes user data, media/thumbnail blobs, and cached media, while `GET /api/users/me/erasure-requests/{id}` reports auditable completion status
- Data export: `POST /api/users/me/export-requests` starts a full export job and `GET /api/users/me/export-requests/{id}` returns status plus the authenticated download when ready

### Application Security

- All secrets in Key Vault; grant ReMind.Api and the thumbnail Function managed identities least-privilege access and use Key Vault references or managed-identity-based connections instead of plaintext app settings
- CORS restricted to known frontend origins
- Input validation: sanitize HTML in descriptions, validate file uploads
- SQL injection: EF Core parameterization for runtime/ad-hoc queries; allow migration-time raw SQL only for controlled schema operations such as `CREATE SPATIAL INDEX`
- Rate limiting on auth endpoints and media upload
- Content Security Policy headers on the React SPA

### Network Security

- SQL Server public network access disabled in production; provision a VNet/private endpoint/private DNS and API/App Service/Container Apps VNet integration so the API can reach SQL
- For environments that require public access, use narrow explicit IP firewall rules and do not enable “Allow Azure services”
- Front Door WAF rules (OWASP top 10)

## 11. Implementation Phases

### Phase 1: Foundation
- Set up Entra External ID tenant and app registrations
- Redesign `dbo.DataPoints` with `GEOGRAPHY` type
- Create `ReMind.Api` with JWT auth and EF Core + NetTopologySuite
- Scaffold `ReMind.Web` React SPA with map component (Leaflet/Mapbox)

### Phase 2: Core Features
- Proximity search API endpoint
- Create/view data points from web app
- Blob Storage media upload pipeline
- User profiles

### Phase 3: Social
- Follow/unfollow
- Comments and reactions
- Activity feed
- User data export + erasure request/status flows with background cleanup workers

### Phase 4: Mobile + Polish
- React Native app with map
- Push notifications
- Content moderation
- Performance optimization (caching, CDN, spatial index tuning)

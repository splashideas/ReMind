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
    IsDeleted       BIT NOT NULL DEFAULT 0
);

CREATE SPATIAL INDEX IX_DataPoints_Location
ON dbo.DataPoints (Location)
USING GEOGRAPHY_AUTO_GRID;
```

### Supporting Tables

- **Users**: profile info, Entra External ID object ID, display name, avatar URL, bio
- **Media**: `MediaId`, `DataPointId`, `BlobUrl`, `ThumbnailUrl`, `MediaType` (Photo/Video), `SortOrder`
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

var nearby = await db.DataPoints
    .Where(d => !d.IsDeleted && d.Visibility == Visibility.Public)
    .Where(d => d.Location.Distance(userLocation) <= radiusMeters)
    .OrderBy(d => d.Location.Distance(userLocation))
    .Take(pageSize)
    .ToListAsync();
```

### Migration Strategy

- **Selected approach (Option A)**: EF Core migrations are the schema source of truth.
- No database has been created yet, so there is no existing `dbo.DataPoints` table or application data to migrate, backfill, or preserve.
- Before deploying the database resources, remove the old create-if-missing SQL path and create the initial schema from the first EF Core migration/bundle.
- If the schema design changes again before first deployment, update the initial migration and redeploy; no rollback/data-disposition process is required until persisted data exists.

## 4. Authentication & Authorization

### Entra External ID Setup

1. Create an Entra External ID tenant (separate from the existing Entra ID on the SQL server)
2. Register two applications:
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
| Comment | Any authenticated user |
| React | Any authenticated user |
| Report | Any authenticated user |
| Moderate | Admin role (custom claim) |

## 5. API Design

### Endpoints

```
POST   /api/datapoints                  # Create a data point
GET    /api/datapoints/nearby           # Proximity search (lat, long, radius, page)
GET    /api/datapoints/{id}             # Get single data point with media
PUT    /api/datapoints/{id}             # Update own data point
DELETE /api/datapoints/{id}             # Soft-delete own data point

POST   /api/datapoints/{id}/media       # Short-lived, write-only SAS for media owned by the caller
POST   /api/datapoints/{id}/comments    # Add comment
GET    /api/datapoints/{id}/comments    # List comments
POST   /api/datapoints/{id}/reactions   # Add/update reaction

GET    /api/users/{id}                  # Get user profile
PUT    /api/users/me                    # Update own profile
POST   /api/users/{id}/follow           # Follow/unfollow

GET    /api/feed                        # Activity feed (followed users' posts)
POST   /api/reports                     # Report content
GET    /api/moderation/queue            # Admin: moderation queue
```

### Design Notes

- Pagination via cursor-based (not OFFSET) for large result sets
- Bounding-box pre-filter before `STDistance` for map viewport queries
- Rate limiting via `AspNetCoreRateLimit` or Azure Front Door WAF
- OpenAPI/Swagger for API documentation
- SignalR hub for real-time map updates (post-MVP)

## 6. Media Storage

### Architecture

```
Client → API (request upload URL)
       → API generates SAS token for Blob Storage
       → Client uploads directly to Blob Storage
       → Client confirms upload → API saves media record
       → Azure Function (queue trigger) generates thumbnail
       → CDN serves thumbnails and media
```

### Decisions

- **Container structure**: `media/{userId}/{dataPointId}/{fileName}`
- **Allowed types**: JPEG, PNG, WebP, MP4, MOV (validate blob signatures/content server-side, not only client-supplied MIME + extension, before publishing)
- **Max file size**: 50 MB photos, 500 MB videos
- **CDN**: Azure Front Door or Azure CDN for media delivery
- **Moderation**: Azure Content Safety or manual review queue for uploaded media
- **Thumbnails**: Azure Function on queue trigger using `SixLabors.ImageSharp`

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
| `azurerm_cdn_profile` + endpoint | Media delivery |
| `azurerm_key_vault` | Secrets (SQL connection string, SAS keys) |
| `azurerm_application_insights` | Monitoring and diagnostics |
| `azurerm_log_analytics_workspace` | Centralized logging |

### Modified Resources

| Resource | Change |
|---|---|
| `azurerm_service_plan` | Upgrade from B1 to P1v3 or switch to Container Apps |
| `azurerm_linux_web_app` | Repurpose for `ReMind.Api` (not Razor frontend) |
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
- EF Core migration bundle generation in CI
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
- Right to erasure: delete user data, media/thumbnail blobs, and cached media through an asynchronous, retryable cleanup workflow with auditable completion status
- Data export: allow users to download all their data

### Application Security

- All secrets in Key Vault (no connection strings in app settings)
- CORS restricted to known frontend origins
- Input validation: sanitize HTML in descriptions, validate file uploads
- SQL injection: EF Core parameterization (eliminate remaining raw SQL)
- Rate limiting on auth endpoints and media upload
- Content Security Policy headers on the React SPA

### Network Security

- SQL Server firewall: allow only Azure services + specific IPs
- Private endpoints for SQL and Blob Storage (production)
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

### Phase 4: Mobile + Polish
- React Native app with map
- Push notifications
- Content moderation
- Performance optimization (caching, CDN, spatial index tuning)

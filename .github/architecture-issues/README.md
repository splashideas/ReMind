# Architecture work items

Issue definitions in this folder are derived from [`docs/architecture-design.md`](../../docs/architecture-design.md) (§13 implementation phases and supporting sections).

## Files

| File | Purpose |
| --- | --- |
| `issues.json` | Ordered work items to build the target architecture |
| `labels.json` | Phase/area labels applied to those issues |
| `seed-issues.sh` | Idempotent `gh` seeder (create-if-missing by title) |

## Creating issues in GitHub

### Option A — workflow (recommended)

1. Ensure `.github/workflows/seed-architecture-issues.yml` is present.
2. Run **Actions → seed-architecture-issues → Run workflow**, or push changes under this folder to `copilot/create-issues-for-architecture`.
3. The job uses `GITHUB_TOKEN` with `issues: write` and skips titles that already exist.

### Option B — local CLI

```bash
# requires gh auth with repo issue permissions
# defaults to the current checkout's GitHub origin; set GITHUB_REPOSITORY to override
./.github/architecture-issues/seed-issues.sh
```

## Tracking

Issues are labeled `architecture` plus `phase:*` and `area:*` for board filtering. Bodies link back to architecture sections and list acceptance criteria.

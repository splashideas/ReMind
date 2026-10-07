# Architecture work items

Issue definitions in this folder are derived from [`docs/architecture-design.md`](../../docs/architecture-design.md) (§13 implementation phases and supporting sections).

## Files

| File | Purpose |
| --- | --- |
| `issues.json` | Ordered work items to build the target architecture |
| `labels.json` | Phase/area labels applied to those issues |
| `seed-issues.sh` | `gh` seeder: create missing titles, update open issue bodies, add missing labels |
| `list-ready.sh` | Print issues whose `blockedBy` titles are all closed |

## Creating issues in GitHub

### Option A — workflow (recommended)

1. Merge this catalog to `main`. The workflow does not run on pull requests or feature branches.
2. Run **Actions → seed-architecture-issues → Run workflow** from `main`, or push catalog changes to `main`.
3. The job uses `GITHUB_TOKEN` with `issues: write`. It creates missing titles and rewrites the body of an existing **open** issue when the catalog body changed. Closed issues are not reopened or rewritten.
4. In Actions the script refuses any ref other than `refs/heads/main` unless `SEED_ALLOW_NON_MAIN=1` is set on purpose.

### Option B — local CLI

From a checkout of `main`, with `gh` authenticated (`repo` scope), `jq`, and `python3`:

```bash
# defaults to the current checkout's GitHub origin; set GITHUB_REPOSITORY to override
./.github/architecture-issues/seed-issues.sh
./.github/architecture-issues/list-ready.sh
```

Pushes to the seed concurrency group are serialized. A failed issue listing aborts instead of treating the repository as empty.

## Automating execution

Run **one unblocked issue at a time**. Do not fan out a phase.

1. Seed from `main`, then run `list-ready.sh` and pick one `READY` issue.
2. Assign it to Copilot cloud agent (`copilot-swe-agent`) from the issue sidebar, or call the agent tasks API with a **user** token. `GITHUB_TOKEN` cannot assign Copilot. Repository rulesets that require the agent come from a user or a classic PAT with `repo` scope, not from a GitHub App.
3. The agent prompt should say: implement only this issue; follow `docs/architecture-design.md` when it disagrees with the issue; follow `docs/operations-setup.md` for anything that cannot be IaC; do not start the next issue.
4. Review the PR, merge it, close the issue, then repeat.

GitHub issue automations that assign Copilot exist only for private and internal repositories. This public repository uses the assignment above. Do not add a workflow that assigns every open architecture issue on each seed run.

## Tracking

Issues are labeled `architecture` plus `phase:*` and `area:*` for board filtering. Each body has ordered steps, contracts, tests, manual setup, and acceptance criteria. Manual GitHub Actions and Azure setup that Terraform cannot perform is in [`docs/operations-setup.md`](../../docs/operations-setup.md).

## Intentionally not issues

Post-MVP items in architecture-design §13 that are not in this catalog: direct messages, cross-user chain invites, full-text search, trending, collections, SignalR, and offline sync. Add an issue before building any of them.

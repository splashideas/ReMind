#!/usr/bin/env bash
# Seed GitHub labels + issues from architecture-issues/*.json
# Requires: gh authenticated with issues:write (and ideally repo for labels).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ISSUE_DIR="${ROOT_DIR}/.github/architecture-issues"

if ! command -v gh >/dev/null 2>&1; then
  echo "gh CLI is required" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required" >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required" >&2
  exit 1
fi

resolve_repo() {
  if [[ -n "${GITHUB_REPOSITORY:-}" ]]; then
    printf '%s\n' "${GITHUB_REPOSITORY}"
    return 0
  fi

  local remote_url repo
  remote_url="$(git -C "${ROOT_DIR}" config --get remote.origin.url || true)"

  case "${remote_url}" in
    git@github.com:*)
      repo="${remote_url#git@github.com:}"
      ;;
    https://github.com/*)
      repo="${remote_url#https://github.com/}"
      ;;
    ssh://git@github.com/*)
      repo="${remote_url#ssh://git@github.com/}"
      ;;
    *)
      echo "Set GITHUB_REPOSITORY or use a checkout with a GitHub origin remote." >&2
      return 1
      ;;
  esac

  printf '%s\n' "${repo%.git}"
}

REPO="$(resolve_repo)"

if [[ -n "${GITHUB_ACTIONS:-}" && -n "${GITHUB_REF:-}" && "${GITHUB_REF}" != "refs/heads/main" && "${SEED_ALLOW_NON_MAIN:-}" != "1" ]]; then
  echo "Refusing to seed from ${GITHUB_REF}. Seed from main, or set SEED_ALLOW_NON_MAIN=1 for a deliberate override." >&2
  exit 1
fi

if ! jq -e '
  (type == "array") and (length > 0)
  and all(.[];
    (.title | type == "string" and length > 0)
    and (.body | type == "string" and length > 0)
    and (.labels | type == "array" and length > 0)
  )
' "${ISSUE_DIR}/issues.json" >/dev/null; then
  echo "issues.json must be a non-empty array of objects with title, body, and labels" >&2
  exit 1
fi

duplicate_titles="$(jq -r '[.[].title] | group_by(.) | map(select(length > 1) | .[0]) | .[]' "${ISSUE_DIR}/issues.json")"
if [[ -n "${duplicate_titles}" ]]; then
  echo "duplicate issue titles in catalog:" >&2
  printf '%s\n' "${duplicate_titles}" >&2
  exit 1
fi

if ! jq -e '
  [.[].title] as $titles
  | all(.[]; ((.blockedBy // []) | all(. as $dep | ($titles | index($dep)) != null)))
' "${ISSUE_DIR}/issues.json" >/dev/null; then
  echo "issues.json blockedBy references a title that is not in the catalog" >&2
  exit 1
fi

echo "Loading existing labels..."
existing_labels="$(
  gh api \
    --paginate \
    -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/labels?per_page=100" \
    --jq '.[].name'
)"
EXISTING_LABELS=()
if [[ -n "${existing_labels}" ]]; then
  mapfile -t EXISTING_LABELS <<<"${existing_labels}"
fi

label_exists() {
  local needle="$1"
  local label
  for label in "${EXISTING_LABELS[@]:-}"; do
    if [[ "${label}" == "${needle}" ]]; then
      return 0
    fi
  done
  return 1
}

echo "Seeding labels into ${REPO}..."
while IFS= read -r label; do
  name="$(jq -r '.name' <<<"${label}")"
  color="$(jq -r '.color' <<<"${label}")"
  description="$(jq -r '.description' <<<"${label}")"
  if label_exists "${name}"; then
    gh label edit "${name}" --repo "${REPO}" --color "${color}" --description "${description}" >/dev/null
    echo "  updated label: ${name}"
  else
    gh label create "${name}" --repo "${REPO}" --color "${color}" --description "${description}" >/dev/null
    echo "  created label: ${name}"
    EXISTING_LABELS+=("${name}")
  fi
done < <(jq -c '.[]' "${ISSUE_DIR}/labels.json")

echo "Loading existing open+closed issue titles for idempotency..."
existing_titles="$(
  gh api \
    --paginate \
    -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/issues?state=all&per_page=100" \
    --jq '.[] | select(.pull_request | not) | .title'
)"
EXISTING_TITLES=()
if [[ -n "${existing_titles}" ]]; then
  mapfile -t EXISTING_TITLES <<<"${existing_titles}"
fi

title_exists() {
  local needle="$1"
  local t
  for t in "${EXISTING_TITLES[@]:-}"; do
    if [[ "${t}" == "${needle}" ]]; then
      return 0
    fi
  done
  return 1
}

remote_title_exists() {
  local needle="$1"
  # -f/-F imply POST unless the method is forced. search/issues is GET-only;
  # a failed POST inside the caller `if` is treated as "not found" and duplicates the issue.
  gh api \
    --method GET \
    --paginate \
    -H "Accept: application/vnd.github+json" \
    search/issues \
    -f q="repo:${REPO} is:issue in:title \"${needle}\"" \
    -F per_page=100 \
    --jq '.items[].title' \
    | jq -e --raw-input --slurp --arg needle "${needle}" '
        split("\n")
        | map(select(. != ""))
        | any(. == $needle)
      ' >/dev/null
}

created=0
skipped=0
updated=0

snapshot="$(mktemp)"
cleanup() {
  rm -f "${snapshot}"
}
trap cleanup EXIT

echo "Loading issue snapshots for body sync..."
gh api \
  --paginate \
  -H "Accept: application/vnd.github+json" \
  "/repos/${REPO}/issues?state=all&per_page=100" \
  --jq '.[] | select(.pull_request | not) | {number, title, state, body, labels: [.labels[].name]}' \
  | jq -c . >"${snapshot}"

echo "Seeding issues into ${REPO}..."
while IFS= read -r issue; do
  title="$(jq -er '.title' <<<"${issue}")"
  body="$(jq -er '.body' <<<"${issue}")"
  label_args=()
  while IFS= read -r lbl; do
    [[ -n "${lbl}" ]] && label_args+=(--label "${lbl}")
  done < <(jq -r '.labels[]?' <<<"${issue}")

  match_count="$(jq -s --arg title "${title}" '[.[] | select(.title == $title)] | length' "${snapshot}")"
  if [[ "${match_count}" -gt 1 ]]; then
    echo "multiple issues titled '${title}'; refusing to edit" >&2
    exit 1
  fi

  if [[ "${match_count}" -eq 1 ]]; then
    number="$(jq -er --arg title "${title}" '. | select(.title == $title) | .number' "${snapshot}")"
    state="$(jq -er --arg title "${title}" '. | select(.title == $title) | .state' "${snapshot}")"
    if [[ "${state}" != "open" ]]; then
      echo "  skip (closed): ${title}"
      skipped=$((skipped + 1))
      continue
    fi

    remote_body="$(mktemp)"
    catalog_body="$(mktemp)"
    jq -er --arg title "${title}" '. | select(.title == $title) | .body // ""' "${snapshot}" >"${remote_body}"
    printf '%s\n' "${body}" >"${catalog_body}"
    if ! python3 - "${remote_body}" "${catalog_body}" <<'PY'
import pathlib, sys
def norm(path):
    return pathlib.Path(path).read_text().replace("\r\n", "\n").replace("\r", "\n").strip()
sys.exit(0 if norm(sys.argv[1]) == norm(sys.argv[2]) else 1)
PY
    then
      gh issue edit "${number}" --repo "${REPO}" --body-file "${catalog_body}" >/dev/null
      echo "  updated body: ${title}"
      updated=$((updated + 1))
    else
      echo "  skip (unchanged): ${title}"
      skipped=$((skipped + 1))
    fi
    rm -f "${remote_body}" "${catalog_body}"

    while IFS= read -r lbl; do
      [[ -z "${lbl}" ]] && continue
      if ! jq -e --arg title "${title}" --arg lbl "${lbl}" \
        'select(.title == $title) | .labels | index($lbl) != null' "${snapshot}" >/dev/null; then
        gh issue edit "${number}" --repo "${REPO}" --add-label "${lbl}" >/dev/null
        echo "  added label ${lbl}: ${title}"
      fi
    done < <(jq -r '.labels[]?' <<<"${issue}")
    continue
  fi

  if title_exists "${title}" || remote_title_exists "${title}"; then
    echo "  skip (exists after refresh): ${title}"
    skipped=$((skipped + 1))
    EXISTING_TITLES+=("${title}")
    continue
  fi

  gh issue create --repo "${REPO}" --title "${title}" --body "${body}" "${label_args[@]}" >/dev/null
  echo "  created: ${title}"
  created=$((created + 1))
  EXISTING_TITLES+=("${title}")
done < <(jq -c '.[]' "${ISSUE_DIR}/issues.json")

echo "Done. created=${created} updated=${updated} skipped=${skipped}"

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

echo "Seeding labels into ${REPO}..."
while IFS= read -r label; do
  name="$(jq -r '.name' <<<"${label}")"
  color="$(jq -r '.color' <<<"${label}")"
  description="$(jq -r '.description' <<<"${label}")"
  if gh label list --repo "${REPO}" --limit 200 --json name --jq '.[].name' | grep -Fxq "${name}"; then
    gh label edit "${name}" --repo "${REPO}" --color "${color}" --description "${description}" >/dev/null
    echo "  updated label: ${name}"
  else
    gh label create "${name}" --repo "${REPO}" --color "${color}" --description "${description}" >/dev/null
    echo "  created label: ${name}"
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

created=0
skipped=0

echo "Seeding issues into ${REPO}..."
while IFS= read -r issue; do
  title="$(jq -r '.title' <<<"${issue}")"
  body="$(jq -r '.body' <<<"${issue}")"
  label_args=()
  while IFS= read -r lbl; do
    [[ -n "${lbl}" ]] && label_args+=(--label "${lbl}")
  done < <(jq -r '.labels[]?' <<<"${issue}")

  if title_exists "${title}"; then
    echo "  skip (exists): ${title}"
    skipped=$((skipped + 1))
    continue
  fi

  gh issue create --repo "${REPO}" --title "${title}" --body "${body}" "${label_args[@]}" >/dev/null
  echo "  created: ${title}"
  created=$((created + 1))
  EXISTING_TITLES+=("${title}")
done < <(jq -c '.[]' "${ISSUE_DIR}/issues.json")

echo "Done. created=${created} skipped=${skipped}"

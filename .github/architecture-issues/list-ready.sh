#!/usr/bin/env bash
# Print architecture issues whose catalog blockers are all closed.
# With no GitHub access, prints issues that have no blockers.
set -euo pipefail

ISSUE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required" >&2
  exit 1
fi

repo="${GITHUB_REPOSITORY:-}"
if [[ -z "${repo}" ]]; then
  if git -C "${ISSUE_DIR}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    origin="$(git -C "${ISSUE_DIR}" remote get-url origin 2>/dev/null || true)"
    case "${origin}" in
      git@github.com:*)
        repo="${origin#git@github.com:}"
        ;;
      https://github.com/*)
        repo="${origin#https://github.com/}"
        ;;
      ssh://git@github.com/*)
        repo="${origin#ssh://git@github.com/}"
        ;;
      *)
        repo=""
        ;;
    esac
    repo="${repo%.git}"
  fi
fi

closed_file="$(mktemp)"
cleanup() {
  rm -f "${closed_file}"
}
trap cleanup EXIT
printf '[]\n' >"${closed_file}"

if [[ -n "${repo}" ]] && command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
  gh api \
    --paginate \
    -H "Accept: application/vnd.github+json" \
    "/repos/${repo}/issues?state=closed&per_page=100" \
    --jq '.[] | select(.pull_request | not) | .title' \
    | jq -R . | jq -s . >"${closed_file}"
fi

jq -r --slurpfile closed "${closed_file}" '
  ($closed[0] // []) as $done
  | .[]
  | . as $item
  | select(($done | index($item.title)) == null)
  | select((($item.blockedBy // []) | all(. as $dep | ($done | index($dep)) != null)))
  | "READY  \($item.title)"
' "${ISSUE_DIR}/issues.json"

#!/usr/bin/env bash
# test-relabel-atlas-issues.sh - harness for relabel-atlas-issues.sh
# Shims `gh` via a bash function so we can drive the script offline.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="$HERE/relabel-atlas-issues.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# State file: JSON array of {number, labels}
STATE="$WORK/state.json"
cat > "$STATE" <<'JSON'
[
  {"number": 101, "labels": ["atlas-submission"]},
  {"number": 102, "labels": ["atlas-submission", "community-pack"]},
  {"number": 103, "labels": ["atlas-submission", "needs-triage"]}
]
JSON
export STATE

# gh shim: emulates the two subcommands the script uses.
gh() {
  case "$1 $2" in
    "issue list")
      # Emit JSON matching --label atlas-submission --state open --json number
      jq '[.[] | select(.labels | index("atlas-submission")) | {number}]' "$STATE"
      ;;
    "issue edit")
      local num="$3"
      # Parse --add-label VALUE
      shift 3
      local label=""
      while [ $# -gt 0 ]; do
        if [ "$1" = "--add-label" ]; then label="$2"; shift 2; else shift; fi
      done
      # Mutate state: append label if not already present.
      jq --argjson n "$num" --arg L "$label" \
        '(.[] | select(.number == $n) | .labels) |= (. + [$L] | unique)' \
        "$STATE" > "$STATE.tmp" && mv "$STATE.tmp" "$STATE"
      ;;
    *) echo "gh shim: unhandled: $*" >&2; return 2 ;;
  esac
}
export -f gh

# First run
bash "$SCRIPT"
first="$(cat "$STATE")"

# Every atlas-submission issue must now carry community-pack
missing="$(jq '[.[] | select(.labels | index("atlas-submission")) | select(.labels | index("community-pack") | not)] | length' <<<"$first")"
if [ "$missing" != "0" ]; then
  echo "FAIL: $missing atlas-submission issues missing community-pack" >&2
  exit 1
fi

# Second run - idempotent
bash "$SCRIPT"
second="$(cat "$STATE")"
if [ "$first" != "$second" ]; then
  echo "FAIL: second run mutated state (non-idempotent)" >&2
  diff <(echo "$first") <(echo "$second") >&2
  exit 1
fi

# Duplicate-label guard
dupes="$(jq '[.[] | .labels | (length - (unique | length))] | add' <<<"$second")"
if [ "$dupes" != "0" ]; then
  echo "FAIL: duplicate labels present after run" >&2
  exit 1
fi

echo "PASS: relabel-atlas-issues.sh idempotent + additive"

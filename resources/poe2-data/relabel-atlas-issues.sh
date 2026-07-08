#!/usr/bin/env bash
# relabel-atlas-issues.sh
# One-shot production reconciliation for the SL #12/#16 label migration:
# every OPEN issue currently carrying `atlas-submission` also gets `community-pack`.
# Idempotent: `gh issue edit --add-label` is a no-op if the label is already applied.
# Run once in prod after CF-TEMPLATES merges; ordering §5 #9 requires this land
# before CF-DEPRECATE-ATLAS ships.
#
# Usage:
#   ./relabel-atlas-issues.sh              # apply
#   ./relabel-atlas-issues.sh -n           # dry-run (preview only)
#   ./relabel-atlas-issues.sh --dry-run    # dry-run (preview only)
set -euo pipefail

DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    -n|--dry-run) DRY_RUN=1 ;;
    -h|--help)
      sed -n '2,15p' "$0"
      exit 0
      ;;
    *) echo "unknown arg: $arg" >&2; exit 2 ;;
  esac
done

if [ "$DRY_RUN" -eq 1 ]; then
  echo "[dry-run] Would relabel open atlas-submission issues -> community-pack ..." >&2
else
  echo "Relabelling open atlas-submission issues -> community-pack ..." >&2
fi

gh issue list --label atlas-submission --state open --json number \
  | jq -r '.[].number' \
  | while read -r n; do
      [ -z "$n" ] && continue
      if [ "$DRY_RUN" -eq 1 ]; then
        echo "  [dry-run] #$n would receive +community-pack" >&2
      else
        echo "  #$n +community-pack" >&2
        gh issue edit "$n" --add-label community-pack
      fi
    done

echo "Done." >&2

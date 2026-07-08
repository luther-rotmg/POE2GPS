#!/usr/bin/env bash
# POE2GPS Worker smoke - CF-WORKER verify gate + PMS-13 deploy gate.
# Usage: bash resources/poe2-data/smoke-worker.sh https://poe2gps-contribute.<you>.workers.dev
# Exit 0 iff:
#   - /submit-atlas returns 200 on a v0.20.x-shape payload (backward-compat),
#   - a 10-shot burst from a single IP saturates the rate limit (<=5 allowed, >=5 blocked),
#   - /submit-preload rejects a bare .dds path with 400,
#   - /submit-buffs accepts a valid buff pack with 200.
# Intended to be run by LO before opening the CF-DASH-BUTTONS PR (ordering gate).

set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <worker-url>" >&2
  exit 2
fi
URL="${1%/}"

fail() { echo "FAIL: $*" >&2; exit 1; }

TMPDIR="${TMPDIR:-/tmp}"

# -- 1. Backward-compat POST to /submit-atlas with v0.20.x-shape body --
echo "== atlas backward-compat =="
ATLAS_PAYLOAD='{"names":{"Metadata/Test/Smoke":"Smoke Test Marker"},"objectives":[{"label":"Smoke","category":"test","priority":1,"enabled":true}]}'
CODE=$(curl -sS -o "$TMPDIR/smoke-atlas.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' \
  --data "$ATLAS_PAYLOAD" "$URL/submit-atlas" || echo "000")
[ "$CODE" = "200" ] || fail "/submit-atlas returned $CODE (want 200); body: $(cat "$TMPDIR/smoke-atlas.json" 2>/dev/null || echo none)"
grep -q '"ok":true' "$TMPDIR/smoke-atlas.json" || fail "/submit-atlas 200 but body missing ok:true"
echo "  atlas OK (200 + ok:true)"

# -- 2. Rate-limit smoke: 10 back-to-back POSTs, expect 6-10 to return 429 --
echo "== rate limit =="
# Wait for the previous atlas request to age out of the 60s window.
echo "  waiting 65s for previous request to age out of rate window..."
sleep 65

BLOCKED=0
ALLOWED=0
for i in $(seq 1 10); do
  CODE=$(curl -sS -o /dev/null -w '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    --data "$ATLAS_PAYLOAD" "$URL/submit-atlas" || echo "000")
  if [ "$CODE" = "429" ]; then BLOCKED=$((BLOCKED+1)); fi
  if [ "$CODE" = "200" ]; then ALLOWED=$((ALLOWED+1)); fi
  echo "  req $i: $CODE"
done
[ "$ALLOWED" -le 5 ] || fail "rate limit too lax: $ALLOWED allowed (want <=5)"
[ "$BLOCKED" -ge 5 ] || fail "rate limit too strict/broken: only $BLOCKED blocked (want >=5)"
echo "  rate limit OK ($ALLOWED allowed, $BLOCKED blocked)"

# Age out again before continuing with the non-rate checks.
echo "  waiting 65s before route-shape checks..."
sleep 65

# -- 3. /submit-preload rejects bare .dds path with 400 --
echo "== preload bare-asset reject =="
BARE_DDS_PAYLOAD='{"preloads":[{"path":"foo.dds","freq":3}]}'
CODE=$(curl -sS -o "$TMPDIR/smoke-preload-bare.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' \
  --data "$BARE_DDS_PAYLOAD" "$URL/submit-preload" || echo "000")
[ "$CODE" = "400" ] || fail "/submit-preload bare .dds returned $CODE (want 400); body: $(cat "$TMPDIR/smoke-preload-bare.json" 2>/dev/null || echo none)"
echo "  preload bare-asset reject OK (400)"

# -- 4. /submit-preload accepts qualified metadata path with 200 --
echo "== preload qualified path =="
GOOD_PRELOAD_PAYLOAD='{"preloads":[{"path":"Metadata/Monsters/Goatman.ao","freq":9}]}'
CODE=$(curl -sS -o "$TMPDIR/smoke-preload-good.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' \
  --data "$GOOD_PRELOAD_PAYLOAD" "$URL/submit-preload" || echo "000")
[ "$CODE" = "200" ] || fail "/submit-preload qualified path returned $CODE (want 200); body: $(cat "$TMPDIR/smoke-preload-good.json" 2>/dev/null || echo none)"
grep -q '"ok":true' "$TMPDIR/smoke-preload-good.json" || fail "/submit-preload 200 but body missing ok:true"
echo "  preload qualified OK (200 + ok:true)"

# -- 5. /submit-buffs accepts valid buff pack with 200 --
echo "== buffs valid pack =="
BUFFS_PAYLOAD='{"buffs":[{"path":"Metadata/Buffs/Aura/Grace","tier":2}]}'
CODE=$(curl -sS -o "$TMPDIR/smoke-buffs.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' \
  --data "$BUFFS_PAYLOAD" "$URL/submit-buffs" || echo "000")
[ "$CODE" = "200" ] || fail "/submit-buffs returned $CODE (want 200); body: $(cat "$TMPDIR/smoke-buffs.json" 2>/dev/null || echo none)"
grep -q '"ok":true' "$TMPDIR/smoke-buffs.json" || fail "/submit-buffs 200 but body missing ok:true"
echo "  buffs OK (200 + ok:true)"

echo "== SMOKE PASS =="

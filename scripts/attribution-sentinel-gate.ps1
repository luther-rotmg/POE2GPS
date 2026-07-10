<#
.SYNOPSIS
  Attribution sentinel gate for the EC2 (ExileCampaigns2) port.

.DESCRIPTION
  Two-mode gate protecting the syrairc attribution surface.

  Mode Draft (this task, EC2-ATTR-DRAFT) — enforces DRAFT-phase invariants:
    1. Every "sentinel-required" file contains BOTH `TODO(syrairc-license)` and
       `TODO(syrairc-hash)` at least once (readme, changelog, HEADER.md, the four
       ported .cs files, DashboardHtml, ApiServer, and scratchpad/discord-v0.21.md).
    2. No touched public surface contains a bare `<license>` or `<hash>` angle-bracket
       token (case-insensitive) — those are the sloppy placeholder shape we reject
       in favour of the load-bearing `TODO(syrairc-*)` sentinels.
    3. No touched public surface contains any `superpowers/`, `.superpowers/`, or
       `docs/superpowers/` path substring — internal-tooling paths must never leak
       into README, CHANGELOG, HEADER.md, in-app strings, ISSUE_TEMPLATE, etc.

  Mode Formalize — flipped by EC2-ATTR-FORMALIZE once the syrairc DM (PMS-12)
    lands with the real license terms + pinned commit hash. Rejects any surviving
    `TODO(syrairc-license)` or `TODO(syrairc-hash)` sentinel in the same file set.

  Exit 0 = pass. Exit 1 = fail (with a per-hit listing).

.PARAMETER Root
  Repository root. Defaults to the parent of the script directory when omitted, so
  `pwsh -File scripts/attribution-sentinel-gate.ps1` "just works" from CI.

.PARAMETER Mode
  Draft (default) or Formalize. Task 9 (EC2-ATTR-FORMALIZE) either bumps the CI
  invocation to `-Mode Formalize` or flips the default here.

.NOTES
  Compatible with Windows PowerShell 5.1 and PowerShell 7+. CI uses pwsh.
  Complements — never replaces — `scripts/compliance-gate.ps1` and
  `scripts/scrub-strings.ps1`, which continue to enforce the read-only /
  no-input-emission contract on shipped source.
#>
[CmdletBinding()]
param(
  [string]$Root,
  [ValidateSet('Draft','Formalize')][string]$Mode = 'Draft'
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Root) { $Root = Split-Path -Parent $scriptDir }
$rootFull = (Resolve-Path -LiteralPath $Root).Path

# ── Sentinel-required set ────────────────────────────────────────────────────
# Files that MUST carry both `TODO(syrairc-license)` and `TODO(syrairc-hash)` in
# Draft mode. In Formalize mode: same set, but neither token may survive.
#
# README.md is deliberately NOT in this set — LO removed the "Powered by /
# Credits" section from README on origin (commit 2d5af3d, 2026-07-09). The
# no-forbidden-tokens sweep below still scans README, so a stray bare `<license>`
# / `<hash>` token still trips the gate; only the presence requirement was lifted.
$sentinelRequired = @(
  'CHANGELOG.md',
  'src/POE2Radar.Core/Campaign/Guide/Data/poe2/HEADER.md',
  'src/POE2Radar.Core/Campaign/Guide/RouteModel.cs',
  'src/POE2Radar.Core/Campaign/Guide/AdvanceEngine.cs',
  'src/POE2Radar.Core/Campaign/Guide/StepMeta.cs',
  'src/POE2Radar.Core/Campaign/Guide/PatternMatcher.cs',
  'src/POE2Radar.Overlay/Web/DashboardHtml.cs',
  'src/POE2Radar.Overlay/Web/ApiServer.cs'
)

# Optional-required: enforce sentinels when the file exists, but don't fail on
# absence. `scratchpad/` is .gitignored (LO's Discord-draft workspace), so CI
# cloning the repo never sees `discord-v0.21.md`. Running the gate locally after
# writing the draft still catches sentinel/regression damage.
$sentinelOptional = @(
  'scratchpad/discord-v0.21.md'
)

# ── Public-surface set (bare-token + no-superpowers scan) ────────────────────
# Fixed public-facing files always scanned when present.
$publicSurfaces = @(
  'README.md',
  'CHANGELOG.md',
  'CONTRIBUTING.md',
  'docs/community-pipeline.md',
  'src/POE2Radar.Core/Campaign/Guide/Data/poe2/HEADER.md',
  'scratchpad/discord-v0.21.md'
)
# Globs additionally swept.
$globs = @(
  '.github/ISSUE_TEMPLATE/*.yml',
  'src/POE2Radar.Overlay/Web/DashboardHtml.cs',
  'src/POE2Radar.Overlay/Web/ApiServer.cs',
  'cloudflare-worker/**/*.html',
  'cloudflare-worker/**/*.js'
)

$fails = New-Object System.Collections.ArrayList
function Fail([string]$msg) { [void]$fails.Add($msg) }

$licSentinel  = 'TODO(syrairc-license)'
$hashSentinel = 'TODO(syrairc-hash)'

# ── 1. Sentinel-required file checks ─────────────────────────────────────────
function CheckSentinels([string]$rel, [bool]$required) {
  $p = Join-Path $rootFull $rel
  if (-not (Test-Path -LiteralPath $p)) {
    if ($required -and $Mode -eq 'Draft') { Fail "missing file (sentinel-required): $rel" }
    return
  }
  $text = Get-Content -LiteralPath $p -Raw
  if ($null -eq $text) { $text = '' }
  if ($Mode -eq 'Draft') {
    if (-not $text.Contains($licSentinel))  { Fail "missing sentinel TODO(syrairc-license) in $rel" }
    if (-not $text.Contains($hashSentinel)) { Fail "missing sentinel TODO(syrairc-hash) in $rel" }
  } else {
    if ($text.Contains($licSentinel))  { Fail "surviving sentinel TODO(syrairc-license) in $rel (Formalize)" }
    if ($text.Contains($hashSentinel)) { Fail "surviving sentinel TODO(syrairc-hash) in $rel (Formalize)" }
  }
}

foreach ($rel in $sentinelRequired) { CheckSentinels $rel $true  }
foreach ($rel in $sentinelOptional) { CheckSentinels $rel $false }

# ── 2 + 3. Bare-token + superpowers scan across the public-surface set ───────
$scanFiles = New-Object System.Collections.ArrayList
foreach ($rel in $publicSurfaces) {
  $p = Join-Path $rootFull $rel
  if (Test-Path -LiteralPath $p) { [void]$scanFiles.Add((Get-Item -LiteralPath $p)) }
}
foreach ($g in $globs) {
  $matched = Get-ChildItem -Path (Join-Path $rootFull $g) -File -ErrorAction SilentlyContinue
  if ($matched) { foreach ($m in $matched) { [void]$scanFiles.Add($m) } }
}

# Dedupe on full path.
$seen = @{}
$dedup = New-Object System.Collections.ArrayList
foreach ($f in $scanFiles) {
  if (-not $seen.ContainsKey($f.FullName)) { $seen[$f.FullName] = $true; [void]$dedup.Add($f) }
}

# Regex definitions:
#   bareLicense/bareHash — literal `<license>`/`<hash>` with word-boundary guards so
#     things like `<licenses>` or `xyz<hash>ee` don't false-match. Case-insensitive.
#   superpowers — matches `superpowers/`, `.superpowers/`, or `docs/superpowers/` when
#     preceded by a path/string delimiter, so an in-line word like "superpowersuit" is
#     never a hit but `docs/superpowers/plan.md` or a code-path string absolutely is.
$bareLicense = '(?i)(?<![A-Za-z_])<license>(?![A-Za-z_])'
$bareHash    = '(?i)(?<![A-Za-z_])<hash>(?![A-Za-z_])'
$superpowers = '(?i)(?:^|[\s"''`(/])(?:\.?superpowers/|docs/superpowers/)'

foreach ($f in $dedup) {
  $rel = ($f.FullName.Substring($rootFull.Length).TrimStart('\','/')) -replace '\\','/'
  $n = 0
  foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
    $n++
    if ($line -match $bareLicense) { Fail "bare token <license> in ${rel}:$n  $($line.Trim())" }
    if ($line -match $bareHash)    { Fail "bare token <hash> in ${rel}:$n  $($line.Trim())" }
    if ($line -match $superpowers) { Fail "superpowers path in public surface ${rel}:$n  $($line.Trim())" }
  }
}

if ($fails.Count -gt 0) {
  Write-Host "ATTRIBUTION-SENTINEL-GATE ($Mode): FAIL" -ForegroundColor Red
  foreach ($m in $fails) { Write-Host "  $m" }
  exit 1
}
Write-Host "ATTRIBUTION-SENTINEL-GATE ($Mode): PASS" -ForegroundColor Green
exit 0

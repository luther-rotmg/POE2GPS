#!/usr/bin/env pwsh
# v0.36 K1: verify the starter icon pack against its contract.
# Exits 0 on all-pass, 1 with a printed reason on any fail.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$iconDir  = Join-Path $repoRoot 'assets/starter-icons'
$attrPath = Join-Path $iconDir 'ATTRIBUTION.md'

$keys = @(
    'monster-normal','monster-magic','monster-rare','monster-unique',
    'chest-closed','chest-opened','npc','transition','waystone',
    'breach','boss','shrine','ritual',
    'currency-drop','unique-drop','rare-drop','magic-drop',
    'friendly','hostile','entity-generic'
)
$sizes = @(32, 64)

function Fail([string]$msg) { Write-Host "FAIL: $msg" -ForegroundColor Red; exit 1 }

# Check 1: 20 keys.
if ($keys.Count -ne 20) { Fail "Key list has $($keys.Count) entries, expected 20." }

# Checks 2 + 3: each PNG exists, non-empty, correct dimensions.
foreach ($key in $keys) {
    foreach ($size in $sizes) {
        $p = Join-Path $iconDir "$key@$size.png"
        if (-not (Test-Path $p)) { Fail "Missing: $p" }
        $len = (Get-Item $p).Length
        if ($len -le 0) { Fail "Empty PNG: $p" }
        $img = [System.Drawing.Image]::FromFile((Resolve-Path $p).Path)
        try {
            if ($img.Width -ne $size -or $img.Height -ne $size) {
                Fail "$p is $($img.Width)x$($img.Height), expected ${size}x${size}"
            }
        } finally {
            $img.Dispose()
        }
    }
}

# Check 4 + 5: ATTRIBUTION.md.
if (-not (Test-Path $attrPath)) { Fail "Missing: $attrPath" }
$attr = Get-Content $attrPath -Raw

if (-not $attr.StartsWith('# Starter Icon Pack Attribution')) {
    Fail "ATTRIBUTION.md does not start with '# Starter Icon Pack Attribution'"
}
if ($attr -notmatch '## Summary') { Fail "ATTRIBUTION.md missing '## Summary' block" }
if ($attr -notmatch 'game-icons\.net') { Fail "ATTRIBUTION.md missing 'game-icons.net' reference" }
if ($attr -notmatch 'https://creativecommons\.org/licenses/by/3\.0/') {
    Fail "ATTRIBUTION.md missing CC BY 3.0 URL"
}

foreach ($key in $keys) {
    $header = "### $key"
    if ($attr -notmatch [regex]::Escape($header)) {
        Fail "ATTRIBUTION.md missing header: $header"
    }
    # Bracket the section for label checks: find "### $key" line to next "### " or EOF
    $pattern = "### $([regex]::Escape($key))(.*?)(?=### |\z)"
    $m = [regex]::Match($attr, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $m.Success) { Fail "Could not extract section for $key" }
    $section = $m.Groups[1].Value
    foreach ($label in @('- Source:', '- Author:', '- License:', '- Modifications:')) {
        if ($section -notmatch [regex]::Escape($label)) {
            Fail "Section '$key' missing label: $label"
        }
    }
    $lm = [regex]::Match($section, '- License:\s*(.+)')
    if (-not $lm.Success) { Fail "Section '$key' has no License value" }
    $license = $lm.Groups[1].Value.Trim()
    if ($license -ne 'CC BY 3.0') {
        Fail "Section '$key' License is '$license', expected 'CC BY 3.0'"
    }
}

Write-Host "PASS: 20 keys x 2 sizes = 40 PNGs verified; ATTRIBUTION.md complete." -ForegroundColor Green
exit 0

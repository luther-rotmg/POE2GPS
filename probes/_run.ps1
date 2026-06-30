<#
  POE2GPS probe runner — shared by the double-click .bat launchers in this folder.
  Each .bat self-elevates to Admin (the probes OpenProcess/ReadProcessMemory on PoE2)
  and then calls:   powershell -File _run.ps1 -Label <label>
  This script: builds the Research probes once, runs the probe(s) for that label,
  shows the output live, AND saves it to probes\output\<label>.txt for Claude to read.
  STRICTLY READ-ONLY: these probes only read PoE2 memory; nothing is ever written or sent.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Label)
$ErrorActionPreference = 'Stop'

$repo   = Split-Path $PSScriptRoot -Parent
$exe    = Join-Path $repo 'src\POE2Radar.Research\bin\Release\net10.0-windows\POE2Radar.Research.exe'
$csproj = Join-Path $repo 'src\POE2Radar.Research\POE2Radar.Research.csproj'
$outDir = Join-Path $PSScriptRoot 'output'

# label -> { probes = one string per invocation (spaces ok for modifiers); long; doThis }
$map = @{
  core     = @{ probes=@('--chain','--info','--vitals','--rarity'); long=$false; doThis='Be loaded into ANY normal zone.' }
  items    = @{ probes=@('--inventory --itemmods');                 long=$false; doThis='Have some identified, modded gear in your inventory.' }
  atlas    = @{ probes=@('--atlas-probe','--atlas-graph');          long=$false; doThis='Open the endgame ATLAS MAP view first (this needs it open).' }
  camera   = @{ probes=@('--camera');                               long=$false; doThis='Stand in a zone (ideally near a monster pack).' }
  tiles    = @{ probes=@('--tiles');                                long=$false; doThis='Be in a non-town zone that has landmarks.' }
  xp       = @{ probes=@('--xp');                                   long=$false; doThis='Be actively killing monsters (run it mid-fight if you can).' }
  questbase= @{ probes=@('--quest');         long=$false; doThis='Run this BEFORE you complete the quest step (saves a baseline). Do NOT complete the step yet.' }
  questdiff= @{ probes=@('--quest --diff');  long=$false; doThis='Run this AFTER you complete the quest step (diffs against the baseline).' }
  watch    = @{ probes=@('--watch');                                long=$true;  doThis='Leave this running, then walk through 2-3 ZONE CHANGES.' }
  metadata = @{ probes=@('--rune-dump');                            long=$false; doThis='Stand near the mechanic whose audio cue did NOT fire.' }
  preload  = @{ probes=@('--preload');                              long=$false; doThis='Be standing in a zone that has a visible league mechanic (a Breach, Ritual, Expedition, Strongbox, etc.) loaded.' }
}

if (-not $map.ContainsKey($Label)) {
    Write-Host "Unknown label '$Label'. Known: $($map.Keys -join ', ')" -ForegroundColor Red
    Read-Host 'Press Enter to close'; exit 1
}
$info = $map[$Label]

# --- build if the exe is missing OR any source .cs is newer than it ------------
# (rebuild-on-change so iterating a probe never silently runs a stale exe)
$needBuild = -not (Test-Path $exe)
if (-not $needBuild) {
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $srcDirs = @((Join-Path $repo 'src\POE2Radar.Research'), (Join-Path $repo 'src\POE2Radar.Core'))
    $newest  = Get-ChildItem -Path $srcDirs -Recurse -Include *.cs -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTimeUtc -gt $exeTime) { $needBuild = $true }
}
if ($needBuild) {
    Write-Host 'Building the Research probes (source changed; ~15s, no game needed)...' -ForegroundColor Yellow
    dotnet build $csproj -c Release
    if (-not (Test-Path $exe)) {
        Write-Host 'Build failed. Copy the red error text and tell Claude.' -ForegroundColor Red
        Read-Host 'Press Enter to close'; exit 1
    }
}

# --- prep output file ----------------------------------------------------------
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir "$Label.txt"
Set-Content -Path $out -Value "POE2GPS probe run: $Label    $(Get-Date)" -Encoding UTF8

# --- what to be doing in-game --------------------------------------------------
Write-Host ''
Write-Host "===  POE2GPS probe:  $Label  ===" -ForegroundColor Cyan
Write-Host "BEFORE you continue: $($info.doThis)" -ForegroundColor Yellow
if ($info.long) {
    Write-Host 'This one runs continuously. When you have zoned a few times, just CLOSE this window.' -ForegroundColor Yellow
}
Write-Host ''

# --- run each probe, echo live + append to file (consistent UTF-8) -------------
foreach ($p in $info.probes) {
    $hdr = "`r`n======== POE2Radar.Research  $p ========"
    Write-Host $hdr -ForegroundColor Cyan
    Add-Content -Path $out -Value $hdr -Encoding UTF8
    # cmd does the 2>&1 merge so the "PoE2 not running" stderr line is captured too,
    # and PS 5.1 doesn't wrap native stderr as error records.
    cmd /c "`"$exe`" $p 2>&1" | ForEach-Object {
        Write-Host $_
        Add-Content -Path $out -Value $_ -Encoding UTF8
    }
}

# --- done ----------------------------------------------------------------------
Write-Host ''
Write-Host "Saved to:  probes\output\$Label.txt" -ForegroundColor Green
Write-Host "==>  Now just tell Claude:  '$Label done'   (Claude reads the file itself.)" -ForegroundColor Green
Read-Host 'Press Enter to close'

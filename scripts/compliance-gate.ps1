<#
.SYNOPSIS
  POE2GPS compliance gate. Fails (exit 1) if any input-emission or process-write Win32/.NET
  symbol appears in shipped source (src/, excluding the dev-only Research project + bin/obj).
  Reads (OpenProcess read-only, ReadProcessMemory, NtReadVirtualMemory, VirtualQueryEx,
  module enumeration) are allowed by design.
.NOTES
  Compatible with Windows PowerShell 5.1 and PowerShell 7+. Run locally with
  `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`; CI uses pwsh.
#>
[CmdletBinding()]
param(
  [string]$Root,
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Root) { $Root = Split-Path -Parent $scriptDir }
$rootFull = (Resolve-Path -LiteralPath $Root).Path

# Forbidden symbols. Reads are deliberately ABSENT (allowed).
$writeSymbols = @(
  'WriteProcessMemory','NtWriteVirtualMemory','ZwWriteVirtualMemory','VirtualAllocEx',
  'VirtualProtectEx','VirtualFreeEx','NtProtectVirtualMemory','CreateRemoteThread','CreateRemoteThreadEx',
  'RtlCreateUserThread','QueueUserAPC','NtQueueApcThread','SetWindowsHookEx','SetWindowsHookExW',
  'NtMapViewOfSection','ZwMapViewOfSection','PROCESS_VM_WRITE','PROCESS_VM_OPERATION','PROCESS_CREATE_THREAD'
)
$inputSymbols = @(
  'SendInput','keybd_event','mouse_event','PostMessage','PostMessageW','PostMessageA',
  'SendMessage','SendMessageW','SendMessageA','SendMessageTimeout','SendNotifyMessage',
  'SetCursorPos','BlockInput','SetKeyboardState'
)
# Word-boundary regex so managed File.WriteAllText / Console.WriteLine never match.
$pattern = '(?<![A-Za-z_])(' + (($writeSymbols + $inputSymbols) -join '|') + ')(?![A-Za-z_])'

# Allowlist of approved benign hits: "relpath:line:Symbol | justification"
$allow = @{}
$allowlistPath = Join-Path $scriptDir 'compliance-allowlist.txt'
if (Test-Path $allowlistPath) {
  foreach ($l in Get-Content $allowlistPath) {
    if (-not $l -or $l.TrimStart().StartsWith('#')) { continue }
    $left = ($l -split '\|')[0].Trim()
    if ($left) { $allow[$left] = $true }
  }
}

function Get-Rel([string]$full) {
  $r = $full.Substring($rootFull.Length).TrimStart('\','/')
  return ($r -replace '\\','/')
}

function Invoke-Scan([string]$dir) {
  $hits = New-Object System.Collections.ArrayList
  $files = Get-ChildItem -Path $dir -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -notmatch '[\\/]POE2Radar\.Research[\\/]' }
  foreach ($f in $files) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
      $n++
      foreach ($m in [regex]::Matches($line, $pattern)) {
        $rel = Get-Rel $f.FullName
        $key = $rel + ':' + $n + ':' + $m.Value
        if (-not $allow.ContainsKey($key)) {
          [void]$hits.Add([pscustomobject]@{ File = $rel; Line = $n; Symbol = $m.Value; Text = $line.Trim() })
        }
      }
    }
  }
  return $hits
}

if ($SelfTest) {
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gate-selftest-' + [guid]::NewGuid())
  New-Item -ItemType Directory -Path $tmp | Out-Null
  $script:rootFull = (Resolve-Path $tmp).Path
  Set-Content (Join-Path $tmp 'Bad.cs')  'class X { void M(){ SendInput(1, null, 0); } }'
  Set-Content (Join-Path $tmp 'Good.cs') 'class Y { void M(){ ReadProcessMemory(h, a, b, c, out var n); } }'
  $bad = @(Invoke-Scan $tmp)
  Remove-Item $tmp -Recurse -Force
  if ($bad.Count -ne 1 -or $bad[0].Symbol -ne 'SendInput') {
    Write-Error "Self-test FAILED: expected exactly one SendInput hit, got $($bad.Count)."; exit 2
  }
  Write-Host 'Compliance gate self-test PASSED (flags SendInput, ignores ReadProcessMemory).' -ForegroundColor Green
  exit 0
}

$srcDir = Join-Path $rootFull 'src'
if (-not (Test-Path -LiteralPath $srcDir)) { Write-Error "src directory not found at: $srcDir (Root=$rootFull)"; exit 3 }
$violations = @(Invoke-Scan $srcDir)

# OpenProcess access-mask check: OpenProcess is allowed, but never with write/operation access.
$openBad = New-Object System.Collections.ArrayList
$srcFiles = Get-ChildItem $srcDir -Recurse -Filter *.cs -File |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -notmatch '[\\/]POE2Radar\.Research[\\/]' }
if ($srcFiles) {
  foreach ($hit in (Select-String -Path $srcFiles.FullName -Pattern 'OpenProcess')) {
    if ($hit.Line -match 'PROCESS_VM_WRITE|PROCESS_VM_OPERATION') { [void]$openBad.Add($hit) }
  }
}

if ($violations.Count -gt 0 -or $openBad.Count -gt 0) {
  Write-Host 'COMPLIANCE GATE: FAIL' -ForegroundColor Red
  foreach ($v in $violations) { Write-Host ('  {0}:{1}  {2}   {3}' -f $v.File, $v.Line, $v.Symbol, $v.Text) }
  foreach ($o in $openBad)   { Write-Host ('  OpenProcess requests write access: {0}' -f $o.Line.Trim()) }
  exit 1
}
Write-Host 'COMPLIANCE GATE: PASS - no input-emission or process-write symbols in shipped source.' -ForegroundColor Green
exit 0

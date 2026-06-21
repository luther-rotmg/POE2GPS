<#
.SYNOPSIS
  Post-build identity-string scrub for the published POE2GPS exe. Same-length byte replacement
  (preserves single-file bundle offsets) of credit/URL identity tokens, in both ASCII and UTF-16LE.
.NOTES
  Runs under Windows PowerShell 5.1 and PowerShell 7+. Uses Latin1 (1:1 byte<->char) string
  replacement so it is fast even on a ~70 MB single-file exe.
  Default tokens are credit/author/URL strings only. "POE2Radar" is intentionally NOT scrubbed:
  it appears in .NET type metadata (namespaces/type names) and blanking it would break reflection.
  That residual is documented; removing it would require a full namespace rename.
#>
[CmdletBinding()]
param(
  [string]$ExePath,
  [string[]]$Tokens = @('Sikaka', 'NattKh', 'github.com/Sikaka', 'github.com/NattKh'),
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$enc = [System.Text.Encoding]::GetEncoding('iso-8859-1')  # Latin1: bytes 0-255 <-> chars 1:1

function Invoke-Scrub([string]$path, [string[]]$tokens) {
  $s = $enc.GetString([System.IO.File]::ReadAllBytes($path))
  $nul = [char]0
  foreach ($t in $tokens) {
    $s = $s.Replace($t, ('x' * $t.Length))                                    # ASCII form
    $u16 = ($t.ToCharArray() | ForEach-Object { "$_$nul" }) -join ''          # UTF-16LE form
    $s = $s.Replace($u16, (('x' + $nul) * $t.Length))
  }
  [System.IO.File]::WriteAllBytes($path, $enc.GetBytes($s))
}

function Test-HasToken([string]$path, [string]$t) {
  $s = $enc.GetString([System.IO.File]::ReadAllBytes($path))
  $nul = [char]0
  $u16 = ($t.ToCharArray() | ForEach-Object { "$_$nul" }) -join ''
  return ($s.Contains($t) -or $s.Contains($u16))
}

if ($SelfTest) {
  $tmp = [System.IO.Path]::GetTempFileName()
  [System.IO.File]::WriteAllBytes($tmp, [System.Text.Encoding]::Unicode.GetBytes('hello Sikaka world'))
  Invoke-Scrub $tmp @('Sikaka')
  $still = Test-HasToken $tmp 'Sikaka'
  Remove-Item $tmp -Force
  if ($still) { Write-Error 'scrub self-test FAILED'; exit 2 }
  Write-Host 'scrub self-test PASSED' -ForegroundColor Green
  exit 0
}

if (-not $ExePath -or -not (Test-Path -LiteralPath $ExePath)) { Write-Error "exe not found: $ExePath"; exit 1 }
Invoke-Scrub $ExePath $Tokens
foreach ($t in $Tokens) {
  if (Test-HasToken $ExePath $t) { Write-Error "token '$t' still present after scrub"; exit 1 }
}
Write-Host "scrubbed identity tokens in $ExePath" -ForegroundColor Green
exit 0

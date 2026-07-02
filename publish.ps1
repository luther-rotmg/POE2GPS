# Builds a self-contained, single-file Windows x64 release of the overlay and zips it.
# Usage:  ./publish.ps1            (version defaults to 'dev')
#         ./publish.ps1 v0.1.0
param([string]$Version = "dev")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet publish "$root/src/POE2Radar.Overlay/POE2Radar.Overlay.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false `
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true `
    -o "$root/publish"

# Belt-and-suspenders: never ship .pdb (they embed the build path / dev username).
Remove-Item "$root/publish/*.pdb" -Force -ErrorAction SilentlyContinue

# Identity hygiene: same-length scrub of credit/URL tokens baked into the published exe.
# (Compression is disabled above so the scrub can reach managed strings inside the bundle.)
& "$PSScriptRoot/scripts/scrub-strings.ps1" -ExePath "$root/publish/Overlay.exe"
if ($LASTEXITCODE -ne 0) { throw "string-scrub failed" }

Copy-Item "$root/README.md", "$root/LICENSE", "$root/CHANGELOG.md" "$root/publish/" -Force
$zip = "$root/POE2GPS-$Version-win-x64.zip"
Compress-Archive -Path "$root/publish/*" -DestinationPath $zip -Force
Write-Host "Built: $zip"

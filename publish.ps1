<#
.SYNOPSIS
  Builds WeatherFeeds.exe into .\publish

.PARAMETER SelfContained
  Bundle the .NET runtime so the exe runs on PCs without the .NET 10 Desktop Runtime (larger file).
#>
param([switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$sc = if ($SelfContained) { 'true' } else { 'false' }

dotnet publish "$PSScriptRoot\src\WeatherFeeds\WeatherFeeds.csproj" `
  -c Release -r win-x64 --self-contained $sc `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o "$PSScriptRoot\publish"

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Central-share setup files go alongside the exe.
Copy-Item "$PSScriptRoot\deploy\*" "$PSScriptRoot\publish\" -Force
Write-Host "`nBuilt: $PSScriptRoot\publish\WeatherFeeds.exe" -ForegroundColor Green

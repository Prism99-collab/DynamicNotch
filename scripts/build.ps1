<#
.SYNOPSIS
  Builds Dynamic Notch and its installers.

.DESCRIPTION
  1. Publishes a self-contained, single-file DynamicNotch.exe for win-x64 and win-arm64
     (no .NET installation needed on the target PC).
  2. Compiles installer\DynamicNotch.iss with Inno Setup into
     artifacts\DynamicNotch-Setup-<version>-x64.exe and ...-arm64.exe.
  3. Writes SHA-256 checksums to artifacts\SHA256SUMS.txt.

.EXAMPLE
  ./scripts/build.ps1
  ./scripts/build.ps1 -Version 1.2.0
#>
param(
  [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\DynamicNotch\DynamicNotch.csproj"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"

if (-not $Version) { $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH" }
  else { throw "The .NET 10 SDK is required: https://dotnet.microsoft.com/download" }
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is required: winget install JRSoftware.InnoSetup" }

Write-Host "Building Dynamic Notch $Version" -ForegroundColor Cyan
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

foreach ($arch in "x64", "arm64") {
  Write-Host "  [$arch] publishing..."
  dotnet publish $project -c Release -r "win-$arch" -p:PublishSingleFile=true -p:Version=$Version -o (Join-Path $publish "win-$arch") --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }

  Write-Host "  [$arch] packaging installer..."
  & $iscc /Q "/DAppVersion=$Version" "/DArch=$arch" "/DPublishDir=$publish" "/DOutputDir=$artifacts" (Join-Path $root "installer\DynamicNotch.iss")
  if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $arch" }
}

$setups = Get-ChildItem $artifacts -File -Filter "DynamicNotch-Setup-*.exe"
$setups | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
  Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { "  {0,-42} {1,7:N1} MB" -f $_.Name, ($_.Length / 1MB) }

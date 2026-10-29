<#
.SYNOPSIS
  Developer helper: builds the current source and installs it over your own copy for quick testing.
  For a normal install, use the setup from the Releases page (or scripts\build.ps1).
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "build.ps1")
$setup = Get-ChildItem (Join-Path $root "artifacts") -Filter "DynamicNotch-Setup-*.exe" | Select-Object -First 1
Write-Host "Installing $($setup.Name) silently..."
Start-Process $setup.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait
Write-Host "Installed. Dynamic Notch starts at the next sign-in, or open it from the Start menu now."

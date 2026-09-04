[CmdletBinding()]
param(
    [switch]$IncludeService
)

$ErrorActionPreference = 'Stop'
$appKey = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\LanPilot.exe'
if (Test-Path -LiteralPath $appKey) {
    Remove-Item -LiteralPath $appKey -Recurse -Force
}

if ($IncludeService) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell window to disable service dumps.'
    }

    $serviceKey = 'HKLM:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\LanPilot.Service.exe'
    if (Test-Path -LiteralPath $serviceKey) {
        Remove-Item -LiteralPath $serviceKey -Recurse -Force
    }
}

Write-Host 'LanPilot crash dump capture is disabled.'

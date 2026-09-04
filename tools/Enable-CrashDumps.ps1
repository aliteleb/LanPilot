[CmdletBinding()]
param(
    [switch]$IncludeService
)

$ErrorActionPreference = 'Stop'
$dumpFolder = Join-Path $env:LOCALAPPDATA 'LanPilot\Diagnostics\Dumps'
New-Item -ItemType Directory -Path $dumpFolder -Force | Out-Null

function Enable-ProcessDump {
    param(
        [Parameter(Mandatory)] [string]$RegistryRoot,
        [Parameter(Mandatory)] [string]$ExecutableName,
        [Parameter(Mandatory)] [string]$Destination
    )

    $key = Join-Path $RegistryRoot $ExecutableName
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name DumpFolder -PropertyType ExpandString -Value $Destination -Force | Out-Null
    New-ItemProperty -Path $key -Name DumpCount -PropertyType DWord -Value 10 -Force | Out-Null
    New-ItemProperty -Path $key -Name DumpType -PropertyType DWord -Value 2 -Force | Out-Null
}

$userRoot = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
Enable-ProcessDump -RegistryRoot $userRoot -ExecutableName 'LanPilot.exe' -Destination $dumpFolder

if ($IncludeService) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell window to enable service dumps.'
    }

    $serviceDumpFolder = Join-Path $env:ProgramData 'LanPilot\Diagnostics\Dumps'
    New-Item -ItemType Directory -Path $serviceDumpFolder -Force | Out-Null
    $machineRoot = 'HKLM:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
    Enable-ProcessDump -RegistryRoot $machineRoot -ExecutableName 'LanPilot.Service.exe' -Destination $serviceDumpFolder
}

Write-Host "Full crash dumps are enabled at $dumpFolder"
Write-Warning 'Dump files can contain private in-memory data. Do not upload them to a public issue.'

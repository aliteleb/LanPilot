[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $projectRoot 'LanPilot.slnx'

Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'LanPilot.exe' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

dotnet restore $solution --configfile (Join-Path $projectRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

dotnet build $solution --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$appDirectory = Join-Path $projectRoot 'src\LanPilot.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64'
$appExecutable = Join-Path $appDirectory 'LanPilot.exe'
if (-not (Test-Path -LiteralPath $appExecutable)) {
    throw "Debug executable was not produced at $appExecutable"
}

Start-Process -FilePath $appExecutable -WorkingDirectory $appDirectory
Write-Host "Debug LanPilot started without an IDE from $appExecutable"

[CmdletBinding()]
param(
    [switch]$WithoutVisualStudio
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $projectRoot 'LanPilot.slnx'

dotnet restore $solution --configfile (Join-Path $projectRoot 'NuGet.Config')
dotnet build $solution --configuration Debug --no-restore

$appDirectory = Join-Path $projectRoot 'src\LanPilot.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64'
$appExecutable = Join-Path $appDirectory 'LanPilot.exe'
if (-not (Test-Path -LiteralPath $appExecutable)) {
    throw "Debug executable was not produced at $appExecutable"
}

Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'LanPilot.exe' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if ($WithoutVisualStudio) {
    Start-Process -FilePath $appExecutable -WorkingDirectory $appDirectory
    Write-Host "Debug LanPilot started from $appExecutable"
    exit
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer discovery tool was not found. Use -WithoutVisualStudio to launch the Debug build directly.'
}

$visualStudio = & $vswhere -latest -products * -property productPath
if (-not $visualStudio -or -not (Test-Path -LiteralPath $visualStudio)) {
    throw 'A launchable Visual Studio IDE was not found. Use -WithoutVisualStudio to launch the Debug build directly.'
}

Start-Process -FilePath $visualStudio -ArgumentList @('/debugexe', $appExecutable)
Write-Host 'Visual Studio opened with the LanPilot Debug executable.'
Write-Host 'Press F5, then enable break-on-thrown for Common Language Runtime Exceptions in Exception Settings.'

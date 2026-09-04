[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'package'))
if (-not $artifactRoot.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The artifact directory must remain inside the LanPilot project.'
}
if (-not $packageRoot.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The package directory must remain inside the artifact directory.'
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
foreach ($oldReleaseFile in @('LanPilot-Setup-0.1.0.exe', 'LanPilot-Setup-0.1.1.exe', 'SHA256SUMS.txt')) {
    $oldReleasePath = Join-Path $artifactRoot $oldReleaseFile
    if (Test-Path -LiteralPath $oldReleasePath) {
        Remove-Item -LiteralPath $oldReleasePath -Force
    }
}

if (-not $SkipRestore) {
    dotnet restore (Join-Path $projectRoot 'LanPilot.slnx') --configfile (Join-Path $projectRoot 'NuGet.Config')
}
dotnet build (Join-Path $projectRoot 'LanPilot.slnx') --configuration Release --no-restore
dotnet test (Join-Path $projectRoot 'LanPilot.slnx') --configuration Release --no-build --no-restore

dotnet publish (Join-Path $projectRoot 'src\LanPilot.App\LanPilot.App.csproj') `
    --configuration Release --runtime win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    --output (Join-Path $packageRoot 'app')

dotnet publish (Join-Path $projectRoot 'src\LanPilot.Service\LanPilot.Service.csproj') `
    --configuration Release --runtime win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    --output (Join-Path $packageRoot 'service')

Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.txt') -Destination $artifactRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $artifactRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $artifactRoot

if (-not $SkipInstaller) {
    $iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    if (-not (Test-Path -LiteralPath $iscc)) {
        throw 'Inno Setup 6 was not found. Install it or run build.ps1 -SkipInstaller.'
    }
    & $iscc (Join-Path $projectRoot 'installer\LanPilot.iss')

    $installer = Get-Item -LiteralPath (Join-Path $artifactRoot 'LanPilot-Setup-0.1.1.exe')
    $hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $installer.Name |
        Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding ascii
}

Write-Host "LanPilot artifacts are ready at $artifactRoot"

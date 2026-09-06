[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$buildId = '{0}-{1}' -f $version, [Guid]::NewGuid().ToString('N')
$artifactRoot = Join-Path $projectRoot ('artifacts\releases\' + $buildId)
$packageRoot = Join-Path $artifactRoot 'package'
# Every build owns a new directory. Never delete or overwrite a previous build,
# particularly artifacts/package, which developers may currently be running.
New-Item -ItemType Directory -Path $packageRoot | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
function Invoke-DotNet {
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
if (-not $SkipRestore) {
    Invoke-DotNet restore (Join-Path $projectRoot 'LanPilot.slnx') --configfile (Join-Path $projectRoot 'NuGet.Config')
}
Invoke-DotNet build (Join-Path $projectRoot 'LanPilot.slnx') --configuration Release --no-restore
Invoke-DotNet test (Join-Path $projectRoot 'LanPilot.slnx') --configuration Release --no-build --no-restore
foreach ($component in @('App', 'Service')) {
    Invoke-DotNet publish (Join-Path $projectRoot "src\LanPilot.$component\LanPilot.$component.csproj") `
        --configuration Release --runtime win-x64 --self-contained true --no-restore `
        '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
        --output (Join-Path $packageRoot $component.ToLowerInvariant())
}
foreach ($notice in @('THIRD_PARTY_NOTICES.txt', 'LICENSE', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination $artifactRoot
}
if (-not $SkipInstaller) {
    $iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    if (-not (Test-Path -LiteralPath $iscc)) { throw 'Inno Setup 6 was not found.' }
    & $iscc "/DMyAppVersion=$version" "/DPackageRoot=$packageRoot" "/DOutputRoot=$artifactRoot" (Join-Path $projectRoot 'installer\LanPilot.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE" }
    $installer = Get-Item -LiteralPath (Join-Path $artifactRoot "LanPilot-Setup-$version.exe")
    $hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $installer.Name |
        Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding ascii
}
Write-Host "Candidate artifacts: $artifactRoot"
Write-Host 'Preview only: isolated network, soak, install/upgrade/uninstall acceptance tests remain external release gates.'

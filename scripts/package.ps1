param(
    [string]$DepsDir,
    [switch]$SkipTests,
    [switch]$SkipNative
)

# Builds a self-contained, ready-to-run release folder and zip under artifacts\.

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Get-RepoRoot
$dotnet = Get-DotNet
$deps = Get-DepsDir $DepsDir
$native = Join-Path $repo 'artifacts\native\bdv_vtk.dll'

if (-not $SkipNative) {
    if (-not (Test-Path (Join-Path $deps 'vtk-install\lib\cmake\vtk-9.7\vtk-config.cmake'))) {
        & (Join-Path $PSScriptRoot 'build-vtk.ps1') -DepsDir $deps
    }
    & (Join-Path $PSScriptRoot 'build-native.ps1') -DepsDir $deps
}
if (-not (Test-Path $native)) { throw "bdv_vtk.dll is missing. Run scripts\build-native.ps1." }

if (-not $SkipTests) {
    Invoke-Checked $dotnet @('test', (Join-Path $repo 'tests\BetterDicomViewer.Tests'), '-c', 'Release')
}

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$out = Join-Path $repo 'artifacts\publish\BetterDicomViewer'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Invoke-Checked $dotnet @(
    'publish', (Join-Path $repo 'src\BetterDicomViewer.App\BetterDicomViewer.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishReadyToRun=true', '-p:DebugType=none', '-o', $out)

if (-not (Test-Path (Join-Path $out 'bdv_vtk.dll'))) { Copy-Item $native $out }

$licenses = Join-Path $out 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $out
$vtkLicenses = Join-Path $deps 'vtk-install\share\licenses\VTK'
if (Test-Path $vtkLicenses) { Copy-Item -Recurse $vtkLicenses (Join-Path $licenses 'VTK') }
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
$codecLicense = Join-Path $nuget 'fo-dicom.codecs\5.16.7\LICENSE.txt'
if (Test-Path $codecLicense) { Copy-Item $codecLicense (Join-Path $licenses 'fo-dicom.Codecs-LICENSE.txt') }
$mvvm = Join-Path $nuget 'communitytoolkit.mvvm\8.4.2'
if (Test-Path (Join-Path $mvvm 'License.md')) { Copy-Item (Join-Path $mvvm 'License.md') (Join-Path $licenses 'CommunityToolkit-LICENSE.md') }
if (Test-Path (Join-Path $mvvm 'ThirdPartyNotices.txt')) { Copy-Item (Join-Path $mvvm 'ThirdPartyNotices.txt') (Join-Path $licenses 'CommunityToolkit-ThirdPartyNotices.txt') }
Set-Content -Encoding utf8 -Path (Join-Path $licenses 'fo-dicom-LICENSE.txt') -Value 'fo-dicom is licensed under the Microsoft Public License (MS-PL). See https://github.com/fo-dicom/fo-dicom/blob/development/License.txt'

$zip = Join-Path $repo "artifacts\BetterDicomViewer-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length / 1MB
Write-Host ("Package: {0} ({1:N0} MB). Run BetterDicomViewer.exe from the extracted folder." -f $zip, $size)

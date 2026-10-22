param(
    [string]$DepsDir,
    [switch]$Clean
)

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Get-RepoRoot
$deps = Get-DepsDir $DepsDir
$vtkDir = Join-Path $deps 'vtk-install\lib\cmake\vtk-9.7'
if (-not (Test-Path (Join-Path $vtkDir 'vtk-config.cmake'))) {
    throw "VTK was not found at $vtkDir. Run scripts\build-vtk.ps1 first."
}

$src = Join-Path $repo 'native\bdv_vtk'
$build = Join-Path $repo 'artifacts\native-build'
$out = Join-Path $repo 'artifacts\native'

if ($Clean -and (Test-Path $build)) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Force $out | Out-Null

Initialize-Msvc
Initialize-Ninja

Invoke-Checked cmake @('-S', $src, '-B', $build, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', "-DVTK_DIR=$vtkDir", "-DCMAKE_INSTALL_PREFIX=$out")
Invoke-Checked cmake @('--build', $build, '--config', 'Release')
Invoke-Checked cmake @('--install', $build, '--config', 'Release')

Write-Host "Native bridge: $(Join-Path $out 'bdv_vtk.dll')"

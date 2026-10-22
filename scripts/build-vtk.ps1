param(
    [string]$DepsDir,
    [string]$VtkTag = 'v9.7.1',
    [switch]$Clean
)

. (Join-Path $PSScriptRoot 'common.ps1')

$deps = Get-DepsDir $DepsDir
$src = Join-Path $deps 'vtk-src'
$build = Join-Path $deps 'vtk-build'
$install = Join-Path $deps 'vtk-install'

New-Item -ItemType Directory -Force $deps | Out-Null

if (-not (Test-Path (Join-Path $src 'CMakeLists.txt'))) {
    Invoke-Checked git @('clone', '--depth', '1', '--branch', $VtkTag, 'https://github.com/Kitware/VTK.git', $src)
}

if ($Clean -and (Test-Path $build)) { Remove-Item -Recurse -Force $build }

Initialize-Msvc
Initialize-Ninja

$modules = @(
    'CommonCore', 'CommonDataModel', 'CommonExecutionModel', 'CommonMath', 'CommonTransforms', 'CommonMisc',
    'ImagingCore', 'ImagingGeneral',
    'FiltersCore', 'FiltersGeneral', 'FiltersGeometry',
    'RenderingCore', 'RenderingOpenGL2', 'RenderingVolume', 'RenderingVolumeOpenGL2',
    'IOGeometry', 'IOImage'
)

$cmakeArgs = @(
    '-S', $src, '-B', $build, '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_INSTALL_PREFIX=$install",
    '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW',
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    '-DBUILD_SHARED_LIBS=OFF',
    '-DVTK_BUILD_TESTING=OFF',
    '-DVTK_BUILD_EXAMPLES=OFF',
    '-DVTK_BUILD_DOCUMENTATION=OFF',
    '-DVTK_ENABLE_REMOTE_MODULES=OFF',
    '-DVTK_ENABLE_WRAPPING=OFF',
    '-DVTK_WRAP_PYTHON=OFF',
    '-DVTK_WRAP_JAVA=OFF',
    '-DVTK_USE_CUDA=OFF',
    '-DVTK_USE_MPI=OFF',
    '-DVTK_SMP_IMPLEMENTATION_TYPE=STDThread',
    '-DVTK_GROUP_ENABLE_Imaging=DONT_WANT',
    '-DVTK_GROUP_ENABLE_MPI=NO',
    '-DVTK_GROUP_ENABLE_Qt=NO',
    '-DVTK_GROUP_ENABLE_Rendering=DONT_WANT',
    '-DVTK_GROUP_ENABLE_StandAlone=DONT_WANT',
    '-DVTK_GROUP_ENABLE_Views=DONT_WANT',
    '-DVTK_GROUP_ENABLE_Web=NO'
)
foreach ($m in $modules) { $cmakeArgs += "-DVTK_MODULE_ENABLE_VTK_$m=YES" }

Invoke-Checked cmake $cmakeArgs
Invoke-Checked cmake @('--build', $build, '--config', 'Release')
Invoke-Checked cmake @('--install', $build, '--config', 'Release')

Write-Host "VTK installed to $install"

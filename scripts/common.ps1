$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot

# Machine-specific tool locations (BDV_DOTNET, BDV_MSVC_SETUP, BDV_DEPS_DIR, BDV_NINJA_DIR) belong in this
# gitignored file rather than in public scripts.
$localEnv = Join-Path $PSScriptRoot 'local.env.ps1'
if (Test-Path $localEnv) { . $localEnv }

function Get-RepoRoot { $script:RepoRoot }

function Get-DepsDir {
    param([string]$Override)
    if ($Override) { return $Override }
    if ($env:BDV_DEPS_DIR) { return $env:BDV_DEPS_DIR }
    return (Join-Path $script:RepoRoot '.deps')
}

function Get-DotNet {
    if ($env:BDV_DOTNET -and (Test-Path $env:BDV_DOTNET)) { return $env:BDV_DOTNET }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match '^10\.') { return $c }
        }
    }
    throw 'A .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or set BDV_DOTNET to dotnet.exe.'
}

function Import-BatchEnvironment {
    param([string]$BatchFile, [string]$Arguments = '')
    $output = & cmd.exe /c "`"$BatchFile`" $Arguments >nul 2>&1 && set"
    foreach ($line in $output) {
        $idx = $line.IndexOf('=')
        if ($idx -gt 0) {
            [Environment]::SetEnvironmentVariable($line.Substring(0, $idx), $line.Substring($idx + 1), 'Process')
        }
    }
}

function Initialize-Msvc {
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) { return }

    if ($env:BDV_MSVC_SETUP -and (Test-Path $env:BDV_MSVC_SETUP)) {
        Import-BatchEnvironment $env:BDV_MSVC_SETUP
    }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $vsPath = $null
        if (Test-Path $vswhere) {
            $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        }
        if ($vsPath) {
            Import-BatchEnvironment (Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat') 'x64'
        }
    }

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        throw 'MSVC was not found. Install Visual Studio 2022 Build Tools with the "Desktop development with C++" workload, or set BDV_MSVC_SETUP to a vcvars-style batch file.'
    }
}

function Initialize-Ninja {
    if (Get-Command ninja.exe -ErrorAction SilentlyContinue) { return }
    foreach ($dir in @($env:BDV_NINJA_DIR, (Join-Path (Get-DepsDir) 'ninja')) | Where-Object { $_ }) {
        if (Test-Path (Join-Path $dir 'ninja.exe')) {
            $env:PATH = "$dir;$env:PATH"
            return
        }
    }
    throw 'Ninja was not found. Install it (for example "pip install ninja" or "winget install Ninja-build.Ninja") and make sure ninja.exe is on PATH.'
}

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    # Windows PowerShell 5.1 turns native stderr lines into terminating errors under 'Stop'.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $File @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$File failed with exit code $code" }
}

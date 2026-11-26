[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $CacheDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$deps = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'windows-preview.json') | ConvertFrom-Json
$vcpkg = Get-Content -Raw -LiteralPath (Join-Path $root 'vcpkg-configuration.json') | ConvertFrom-Json

function Initialize-Checkout([string] $Path, [string] $Repository, [string] $Revision) {
    if (-not (Test-Path -LiteralPath $Path)) {
        git init $Path
        git -C $Path config core.longpaths true
        git -C $Path remote add origin $Repository
        git -C $Path fetch --depth 1 origin $Revision
        git -C $Path switch --detach FETCH_HEAD
    }
    $current = git -C $Path rev-parse HEAD
    if ($current -ne $Revision) {
        throw "$Path is at $current. This build expects $Revision."
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -version '[18.0,19.0)' -property installationPath
if (-not $vs) { throw 'Visual Studio 2026 Build Tools are required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$toolset = (Get-Content -LiteralPath (Join-Path $root 'MSVC_VERSION')).Trim()
$compiler = Join-Path $vs "VC\Tools\MSVC\$toolset\bin\Hostx64\x64\cl.exe"
if (-not (Test-Path -LiteralPath $vcvars) -or -not (Test-Path -LiteralPath $compiler)) {
    throw "Install the x64 C++ Build Tools and pinned MSVC $toolset workload first."
}

$cache = [IO.Path]::GetFullPath($CacheDirectory, (Get-Location).Path)
$publish = [IO.Path]::GetFullPath($PublishDirectory, (Get-Location).Path)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if ($root -match '[ &|<>^%!"\r\n]' -or $publish -match '[&|<>^%!"\r\n]') {
    throw 'Use a short source path without spaces and a publish path without shell metacharacters.'
}

Initialize-Checkout (Join-Path $root 'external/orion-gtk') 'https://github.com/kagisearch/orion-linux.git' $deps.orionCoreRevision
Initialize-Checkout (Join-Path $root 'external/vcpkg') $vcpkg.'default-registry'.repository $vcpkg.'default-registry'.baseline

$vendor = Join-Path $root 'vendor'
$cef = Join-Path $vendor 'cef'
$versionFile = Join-Path $vendor 'CEF_VERSION'
$version = "official:$($deps.cefVersion)"
if (-not (Test-Path -LiteralPath $cef)) {
    $name = "cef_binary_$($deps.cefVersion)_windows64"
    $archive = Join-Path $cache "$name.tar.bz2"
    if (-not (Test-Path -LiteralPath $archive)) {
        $url = 'https://cef-builds.spotifycdn.com/' + [uri]::EscapeDataString("$name.tar.bz2")
        curl.exe --fail --location --retry 2 --silent --show-error --output $archive $url
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $deps.cefArchiveSha256) {
        throw "CEF archive checksum mismatch at $archive."
    }
    New-Item -ItemType Directory -Path $vendor -Force | Out-Null
    $staging = Join-Path $vendor ('cef-extract-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    tar.exe -xf $archive -C $staging
    $extracted = Join-Path $staging $name
    if (-not (Test-Path -LiteralPath (Join-Path $extracted 'Release/libcef.dll'))) {
        throw 'The CEF archive is missing its x64 runtime.'
    }
    $vendorPrefix = (Resolve-Path -LiteralPath $vendor).Path.TrimEnd('\') + '\'
    $unpacked = (Resolve-Path -LiteralPath $extracted).Path
    $sourceIsInVendor = $unpacked.StartsWith($vendorPrefix, [StringComparison]::OrdinalIgnoreCase)
    $targetIsInVendor = $cef.StartsWith($vendorPrefix, [StringComparison]::OrdinalIgnoreCase)
    if (-not ($sourceIsInVendor -and $targetIsInVendor)) {
        throw 'CEF extraction left the checkout vendor directory.'
    }
    Move-Item -LiteralPath $unpacked -Destination $cef
    $version | Set-Content -LiteralPath $versionFile
    $version | Set-Content -LiteralPath (Join-Path $cef 'INSTALLED_CEF_VERSION')
}
if ((Get-Content -LiteralPath $versionFile).Trim() -ne $version) {
    throw 'The checkout has a different CEF version. Use a fresh checkout for this release.'
}

$env:ORION_PREVIEW_VC_INIT = $vcvars
$env:ORION_PREVIEW_PUBLISH = $publish
$env:UV_CACHE_DIR = Join-Path $cache 'uv-cache'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $cache 'uv-python'
$env:UV_PYTHON_BIN_DIR = Join-Path $cache 'uv-bin'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& (Join-Path $PSScriptRoot 'build-windows-preview.cmd')

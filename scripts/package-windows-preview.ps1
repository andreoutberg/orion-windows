[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$pins = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'windows-preview.json') | ConvertFrom-Json
$revision = (git -C $root rev-parse HEAD).Trim()
$name = "Orion-SearchPlus-$($pins.version)-win-x64.zip"
$out = (New-Item -Path $OutputDirectory -ItemType Directory -Force).FullName
$zipPath = Join-Path $out $name
$checksumPath = Join-Path $out 'SHA256SUMS.txt'
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $checksumPath)) {
    throw 'Choose a new package output directory for this build.'
}

$required = @('Orion.exe', 'Orion.dll', 'Orion.pri', 'OrionBackend.dll', 'orion_net.dll', 'nethost.dll',
    'engines/blink/OrionEngineB.exe', 'engines/blink/OrionEngineB.pri', 'engines/blink/chrome_elf.dll',
    'engines/blink/OrionEngineB.dll', 'engines/blink/libcef.dll', 'engines/blink/resources.pak')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "Missing required publish file: $file"
    }
}

$brokerHash = (Get-FileHash (Join-Path $source 'engines/blink/OrionEngineB.exe')).Hash
if ($brokerHash -ne (Get-FileHash (Join-Path $root 'vendor/cef/Release/bootstrap.exe')).Hash) {
    throw 'The preview broker does not match the pinned CEF bootstrap.'
}
$priHash = (Get-FileHash (Join-Path $source 'Orion.pri')).Hash
if ($priHash -ne (Get-FileHash (Join-Path $source 'engines/blink/OrionEngineB.pri')).Hash) {
    throw 'The sandbox broker has a stale WinUI resource index.'
}

$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        if ($relative -like 'profile-data/*' -or $file.Extension -eq '.pdb') { continue }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative)
    }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
        (Join-Path $PSScriptRoot 'launch-windows-preview.cmd'), 'Open Orion Search+ Preview.cmd')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
        (Join-Path $root 'docs_ai/windows-preview-release.md'), 'PREVIEW.md')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
        (Join-Path $root 'docs_ai/searchplus-design.md'), 'searchplus-design.md')
    foreach ($notice in @(
        @{ Path = 'LICENSE'; Name = 'Orion.txt' },
        @{ Path = 'vendor/cef/LICENSE.txt'; Name = 'CEF.txt' },
        @{ Path = 'vendor/cef/CREDITS.html'; Name = 'Chromium-Credits.html' }
    )) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            (Join-Path $root $notice.Path), "licenses/$($notice.Name)")
    }
    $notices = Join-Path $root 'vcpkg_installed/x64-windows/share'
    foreach ($file in (Get-ChildItem -LiteralPath $notices -Filter copyright -File -Recurse)) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            $file.FullName, "licenses/vcpkg/$($file.Directory.Name).txt")
    }
    $info = $archive.CreateEntry('BUILDINFO.json')
    $writer = [IO.StreamWriter]::new($info.Open())
    try {
        $writer.Write((@{
            version = $pins.version
            sourceRevision = $revision
            sourceModified = [bool](git -C $root status --porcelain)
            architecture = 'win-x64'
            cefVersion = $pins.cefVersion
            orionCoreRevision = $pins.orionCoreRevision
        } | ConvertTo-Json))
    } finally { $writer.Dispose() }
} finally { $archive.Dispose() }

$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$sha256  $name" | Set-Content -LiteralPath $checksumPath -Encoding ascii
Write-Output $zipPath, $checksumPath

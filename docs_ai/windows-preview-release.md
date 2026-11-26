# Orion Search+ Windows preview

This x64 developer preview uses Orion's native WinUI shell and Chromium through
CEF. Search by Office Commun inspired the folding tab rail, quieter controls,
and quick tab search. The interface is implemented in Orion's own WinUI code.

The Chromium renderer sandbox is disabled in this build. Use the preview with
local test pages. The Windows build uses the public Chromium distribution while
Kagi's pinned WebKit source requires access to its build bucket.

## Try the portable package

Extract the ZIP to a writable folder and open **Open Orion Search+ Preview.cmd**.
The launcher puts its profile data next to the executable and opens vertical
tabs on first launch. Windows caption controls work normally.

Use **Ctrl+B** to fold or expand the tab rail, **Ctrl+K** to search open tabs by
title, site, or tab group, **Ctrl+L** to edit the address, and **Ctrl+T** for a
new tab. Use the arrow keys and Enter in tab search. Rail and focus animations
follow Windows' animation setting.

## Build and package

Use Windows x64 with Visual Studio 2026 Build Tools, the MSVC version in
`MSVC_VERSION`, the Windows SDK 26100, C++ CMake tools, .NET SDK 10 or 11, Git,
and PowerShell 7. Use a short checkout path without spaces. The build uses the
public source revisions and CEF SHA-256 recorded in `scripts/windows-preview.json`
and the vcpkg baseline in `vcpkg-configuration.json`.

```powershell
pwsh -NoProfile -File scripts/build-windows-preview.ps1 -PublishDirectory out/app -CacheDirectory out/cache
pwsh -NoProfile -File scripts/package-windows-preview.ps1 -PublishDirectory out/app -OutputDirectory out/package
```

The package includes the self-contained executable, a separate-profile launcher,
license notices and `BUILDINFO.json`. Verify the ZIP against `SHA256SUMS.txt`,
extract to a path containing spaces, launch the packaged preview, load a local
page, and exercise fold, search, selection and relaunch before publishing.

## Stage a draft prerelease

The **Windows preview package** workflow uploads the ZIP and `SHA256SUMS.txt`
as the `orion-searchplus-win-x64` artifact on relevant pull requests. After
review and merge to the fork's default branch, tag the reviewed commit with
`v` followed by the version in `scripts/windows-preview.json`. Run the workflow
against that tag with `draft_release` enabled.

```powershell
gh workflow run windows-preview.yml --ref v0.1.0-searchplus.1 -f draft_release=true
```

Check the draft's tag, attached checksum and ZIP's `BUILDINFO.json` against
the reviewed commit. Keep this build in prerelease status and include the
sandbox limitation in its release notes. Keep each preview in its own folder
so you can switch back by launching the previous extracted package.

[Search reference and MIT licence](https://github.com/driceroland/Search)

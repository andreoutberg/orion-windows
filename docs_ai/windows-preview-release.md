# Orion Search+ Windows preview

This x64 developer preview uses Orion's native WinUI shell and Chromium through
CEF. Search by Office Commun inspired the folding tab rail, quieter controls,
and quick tab search. The interface is implemented in Orion's own WinUI code.

The portable build uses CEF's Windows sandbox bootstrap. It loads the same
native executable for the browser process and its Chromium subprocesses. The
Windows build uses the public Chromium distribution while Kagi's pinned WebKit
source requires access to its build bucket.

## Try the portable package

Extract the ZIP to a writable folder and open **Open Orion Search+ Preview.cmd**.
The launcher puts its profile data next to the executable and opens vertical
tabs on first launch. Windows caption controls work normally.

Use **Ctrl+B** to fold or expand the tab rail, **Ctrl+K** to search workspaces
and open tabs, **Ctrl+L** to edit the address, and **Ctrl+T** for a new tab.
Use the arrow keys and Enter in search. Rail and focus animations follow
Windows' animation setting. See [Search+ design language](searchplus-design.md)
for the visual and interaction decisions.

### Workspaces and pins

Choose a saved tab group from the **Workspaces** switcher. Each window keeps
the tab you last visited in each workspace. From a tab's context menu, choose
**Pin to this workspace** for a named workspace, or **Pin across workspaces**
to show the pin throughout the profile. Existing pins keep their scope on
upgrade. Pins reopen their saved page. **Reset to saved page** and **Use current
page as saved page** let you manage that destination explicitly. Use **Pin
folder** on a workspace-only pin to create a folder or move the pin. Folders
keep their members, order, and expanded state. Unpacking a folder keeps its
pins. The folded rail opens folders from its folder button.

Workspaces share the profile's site sign-ins. Open a separate Orion profile
to use a separate persistent sign-in, or press **Ctrl+Shift+N** for a private
window. Private windows use a separate process and temporary browser root,
with application databases and CEF site data in memory. Native history,
password storage, favicons, and desktop notification registration are
disabled there. Files you explicitly download are kept.

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
Verify the Chromium renderer's restricted Windows token from the packaged
bootstrapped process with `scripts/test-renderer-sandbox.ps1 -BrowserProcessId
<broker-pid>`. Check that opening and closing a private window leaves
the existing profile untouched and that a new private session starts empty.

## Stage a draft prerelease

The **Windows preview package** workflow uploads the ZIP and `SHA256SUMS.txt`
as the `orion-searchplus-win-x64` artifact on relevant pull requests. After
review and merge to the fork's default branch, tag the reviewed commit with
`v` followed by the version in `scripts/windows-preview.json`. Run the workflow
against that tag with `draft_release` enabled.

```powershell
gh workflow run windows-preview.yml --ref v0.2.0-searchplus.1 -f draft_release=true
```

Check the draft's tag, attached checksum and ZIP's `BUILDINFO.json` against
the reviewed commit. Keep this build in prerelease status and record the
platform and browser-engine checks. Keep each preview in its own folder
so you can switch back by launching the previous extracted package.

[Search reference and MIT licence](https://github.com/driceroland/Search)

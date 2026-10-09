# Installer

Builds an .msi for File Command Center with [WiX Toolset](https://wixtoolset.org/) v5 (**not** v6/v7 – those require
accepting a paid "Open Source Maintenance Fee" EULA, which we have avoided).

## What it does

Installs to `Program Files\File Command Center\` (needs admin) and creates a Start menu shortcut. No desktop
shortcut, no file type registrations. `data.json` stays in `%LOCALAPPDATA%\FileCommandCenter\` and is not affected
by installing/uninstalling.

## Build

```bash
dotnet tool install --global wix --version 5.0.2
wix extension add WixToolset.UI.wixext/5.0.2 -g
```

Always build **from the publish output, with a real Windows path as the working directory** – WiX's binder uses the
Windows Installer database API directly, and it fails with "The Windows Installer service failed to start" (MSI 1631)
when run from a network/UNC path (e.g. `\\wsl.localhost\...`, which is where this folder is when seen from WSL). Copy
`Package.wxs` and `..\publish\` to a local `C:\` folder and build from there:

```powershell
dotnet publish ..\CommandCenter.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o C:\build\publish

Copy-Item Package.wxs C:\build\
cd C:\build
wix build Package.wxs -arch x64 -ext WixToolset.UI.wixext -d PublishDir=publish -out FileCommandCenter.msi
```

The `msiserver` service (Windows Installer) must be running – it is normally set to "Manual" and usually starts by
itself, but start it by hand (`Start-Service msiserver`) if the build fails with MSI 1631.

## New version / release

The app automatically checks **GitHub Releases** on `BahneGork/file-command-center` for a newer version (see
`../UpdateCheck.cs`) and lets the user install it straight from a bar in the app. That needs every release to have
an `.msi` attached as an asset – there is no CI here, so it is done by hand:

1. **`UpgradeCode` in `Package.wxs` (`dc319aa0-1725-4cc3-8c86-3dbd4fb3a308`) must never change** – it is what lets
   a newer version upgrade an older one instead of installing next to it.
2. Set the **same** new version number in two places: `<Version>` in `../CommandCenter.csproj` and `Version=` in `Package.wxs`.
3. Build the publish output and the MSI as described above.
4. Build the portable version separately, with `-p:Flavor=portable` added to the `dotnet publish` command and its own
   output folder (e.g. `-o C:\build\portable`), and zip that folder's contents directly as
   `FileCommandCenter-<version>-portable-win-x64.zip`. The portable exe must never go in the MSI, or the other way
   round – the mark in the exe decides whether the app updates itself with the MSI or the .zip.
5. Tag and release with both files attached (the tag must start with `v`, e.g. `v1.0.2` – that is what the app
   compares its own version against):
   ```
   gh release create v1.0.2 FileCommandCenter.msi FileCommandCenter-1.0.2-portable-win-x64.zip --title "v1.0.2" --notes "What changed"
   ```
6. Commit and push the source code (including the two version numbers) separately – the release is not tied to a
   particular commit in any way beyond the tag.

Only users already running a version that has the update check built in (this one or newer) will see the update
bar. A user on an older, manually copied build has to update by hand one last time.

## Not done yet

- **Signing.** The MSI is unsigned, so Windows SmartScreen/Defender will warn.
- No desktop shortcut or "Repair/Uninstall" shortcut in the Start menu beyond the standard Windows handling
  (Settings → Apps).

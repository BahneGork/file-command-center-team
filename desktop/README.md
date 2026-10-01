# File Command Center (desktop)

A Windows desktop version of the dashboard in `../index.html`. It replaces the original PowerShell/browser-based
version: there is **no local web server and no open network port**.

## What it is
- A .NET 8 WinForms window hosting the dashboard with Microsoft Edge WebView2.
- The page is `../index.html` (single source, copied to `web/index.html` at build time).
- The page talks to the app through WebView2 messages, not HTTP. Messages are only accepted from the app's own page.

## What it does on the PC
- Reads/writes its own data: `%LOCALAPPDATA%\FileCommandCenter\` (`data.json`, `backups\`, `WebView2\`).
- Tracks and opens any file type, not just Excel/CSV: the file picker, "Scan mapper…" and the folder browser all show every file type by default (with an opt-in filter to narrow to Excel/CSV).
- Opens files in the program Windows has associated with them (e.g. Excel), only for paths that are registered on the dashboard.
- Never opens: `.exe .bat .cmd .com .scr .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh .msi .msp .hta .reg .lnk .jar .dll .cpl .pif .url .appref-ms`.
- Reads file contents only for the preview pane, for the file the user has selected (read-only, registered paths only, max 50 MB). The pane renders spreadsheets/CSV as a table, images inline, PDFs with the native WebView2 PDF viewer, and common text/code files as text; other types show file info only.
- Lists folders / shows the Windows file and folder dialogs so the user can pick files.
- Brings the Excel or Explorer window to the front after opening (uses `user32.dll` window APIs).
- Only one instance can run per user (mutex).
- Checks GitHub Releases for a newer version (a background HTTPS request, native C# only — see "Network access"
  below) and, if you choose to update, downloads the installer and launches it, then closes itself. The portable
  build instead downloads the portable `.zip`, renames its own running `.exe` to `.old`, puts the new files in its
  folder, starts the new version and closes; the new version deletes the `.old` file. See `UpdateCheck.cs` and
  `installer/README.md`.

## What it does not do
- No listener on any port.
- Developer tools, autofill and password saving are disabled in the WebView.
- Links to `http(s)` sites open in the default browser; `ms-excel:ofe|u|http(s)://...` links open Excel.

## Network access
The page itself still cannot reach anything but its own files: requests to anything other than its own page are
blocked in the WebView (verified: fetches to the internet and to other localhost ports fail; navigation away is
cancelled). The **native app code**, outside the page, makes two kinds of outbound HTTPS requests, both only when
checking for or installing an update:
- `GET api.github.com/repos/.../releases/latest` (small JSON, no data about you or your files is sent).
- A download of the `.msi` asset (or, for the portable build, the portable `.zip`) from that release, if you click "Opdater nu".

Installing the downloaded update runs the standard Windows Installer, which asks for **admin rights (UAC)** — this
is the one case where the app needs elevation and writes outside your profile (to `Program Files`). Nothing else
the app does needs admin rights or leaves your profile.

## Requirements
- Windows 10/11 x64 with the Microsoft Edge **WebView2 Runtime** (included with Microsoft 365 apps and Windows 11).
  If it is missing the app shows a message and exits.
- Self-contained build: no .NET install needed. Framework-dependent build: needs .NET 8 Desktop Runtime.

## Build
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```
Distribute the contents of `publish/`: `FileCommandCenter.exe` **and the `web` folder next to it**.
Use `--self-contained false` for a ~1 MB exe that needs the .NET 8 Desktop Runtime.

The **portable** release asset is a second build of the same version with `-p:Flavor=portable` added (it marks
the `.exe` as portable, so it updates itself from the `.zip` instead of the `.msi`; the app also shows "Portable"
at the bottom of the sidebar). Publish it to its own folder, zip that folder's contents directly (no extra nesting
folder) as `FileCommandCenter-<version>-portable-win-x64.zip` and attach it to the GitHub release alongside the
`.msi` (`installer/README.md` covers building that). Never put the portable `.exe` in the `.msi` or the other way
round.

## Not done yet
- Code signing — the installer (`installer/`) is unsigned, so Windows SmartScreen/Defender will warn.

## Third-party components
- `Microsoft.Web.WebView2` (NuGet) — the only package.

# File Command Center

A small dashboard that gathers your files in one place (Excel, PDF, images, documents, folders, links – any type), whether they live locally, on a network drive or in OneDrive. The dashboard stores only the **path** to each file; files are never moved or copied.

A real Windows app (.NET 8 + WebView2), with no web server and no open port. See **[desktop/README.md](desktop/README.md)** for build instructions, what the app does on the PC, and requirements.

The app is in Danish and English: the language button at the bottom of the sidebar switches the whole interface and the manual. The default is Danish.

## Features

- File picker, "Scan folders…" and a built-in folder browser for adding files and folders.
- Hubs, tags, favourites, archive and file status (does the file exist or is it missing).
- Preview pane (as in File Explorer) with a spreadsheet/CSV table, images, PDFs and text/code files – the width can be dragged.
- Opens files in the program Windows has associated with them, and always blocks programs/scripts (`.exe`, `.ps1`, `.js` etc.).
- Deadlines, including recurring ones, with calendar export (.ics).
- Daily backups of your data, plus an extra backup whenever a save would remove files.
- The app checks for new versions itself (GitHub Releases) and can install an update with one click.

## Data

Your data (`data.json` + `backups/`) is stored locally in `%LOCALAPPDATA%\FileCommandCenter\` and is **not** part of this repo.

## Get the app

From [Releases](https://github.com/BahneGork/file-command-center/releases/latest) – two forms, same app:
- **Installer (.msi)** – installs to `Program Files`, needs admin, adds a Start menu shortcut and updates itself. Build instructions: **[desktop/installer/README.md](desktop/installer/README.md)**.
- **Portable (.zip)** – unzip and run `FileCommandCenter.exe` anywhere, no installation, no admin rights needed. The `web` folder must stay next to the .exe. From v1.0.7 it updates itself through "Opdater nu" / "Update now" (downloads the new .zip and swaps out its own files), as long as its folder is writable. A portable copy from before v1.0.7 has to be updated by hand one last time.

Both are self-contained builds (no .NET needs to be installed beforehand).

## Not done yet

- **Code signing.** The installer is unsigned, so Windows will warn (SmartScreen/Defender). See [desktop/README.md](desktop/README.md) and [desktop/installer/README.md](desktop/installer/README.md) for details.

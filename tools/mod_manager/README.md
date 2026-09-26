# Stardew Valley iOS Mod Manager (Vortex-Style Desktop Tool)

A dedicated desktop mod manager for Stardew Valley on iOS devices, inspired by Nexus Mods' Vortex.

## Features
- **Direct Apple USB AFC Connection**: Communicates directly with connected iPhones/iPads over USB using Apple's MobileDevice/AFC protocol (via `pymobiledevice3`). No jailbreak or third-party transfer software required.
- **1-Click Mod Installation**: Drag & drop or browse `.zip` archives or folders. Auto-detects `manifest.json`, sanitizes nesting, and pushes directly to `/Documents/Mods/`.
- **Instant Enable/Disable**: Toggle mods on/off in milliseconds without deleting files. Renames active folders to `.disabled_<mod>`, which SMAPI automatically ignores.
- **Content Patcher Detection**: Warns if Content Patcher is missing when content packs are installed.
- **Live SMAPI Logs**: Read `/Documents/ErrorLogs/SMAPI-latest.txt` directly from the phone with one click, copy to clipboard, or save to disk.
- **Save Backups**: One-click export of save games from your phone to `.zip` backups on your PC.
- **Local Folder Fallback**: Supports selecting a local directory if you mount your phone through iTunes / 3uTools / iMazing or an iCloud folder.

## How to Run
Double-click `Stardew Valley iOS Mod Manager.bat` on your Desktop, or run:
```bash
python tools/mod_manager/main.py
```

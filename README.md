# Stardew Valley iOS Port with SMAPI Modding Support

An open-source .NET 8 iOS host and runner for **Stardew Valley PC 1.6+**, featuring full **SMAPI modding support**, on-screen touch controls, native physical gamepad support, and automated **IPA packaging via GitHub Actions**.

---

## Features

- **PC 1.6 Parity**: Runs the true PC 1.6 release of Stardew Valley on iOS.
- **SMAPI Modding Support**: Enabled by .NET 8 iOS Mono Interpreter (`<UseInterpreter>true</UseInterpreter>`), allowing dynamic assembly loading and mod execution without requiring jailbreak.
- **iOS Files App Integration**: Game folders (`Mods`, `Saves`, `Content`, `ErrorLogs`) are exposed directly in the iOS **Files** app (`On My iPhone > Stardew Valley`). You can drop SMAPI mods or copy save files to and from your PC at any time.
- **Touch & Controller Controls**:
  - Transparent on-screen virtual analog stick and action buttons (Action/Talk, Tool, Inventory, Menu, Journal).
  - Tap-to-mouse emulation for dialogues, menus, and inventory management.
  - Native MFi, DualSense (PS5), DualShock 4, Xbox Wireless Controller, Backbone One, and Razer Kishi support.
  - One-tap toggle button to hide/show virtual controls.
- **ProMotion 120Hz Support**: Smooth high-refresh gameplay on supported iPhones and iPads.
- **Cloud CI/CD**: Clean GitHub Actions pipeline that builds and packages the `.ipa` using macOS runners.

---

## Quick Start Guide

### Step 1: Bundle Your Steam Game Files

Run the bundler script on your PC where Stardew Valley is installed:

#### Using PowerShell (Windows):
```powershell
.\tools\bundle_game.ps1 -CreateZip
```
*(Optionally include your installed mods with `-IncludeMods`)*

#### Using Python (Windows / macOS / Linux):
```bash
python tools/bundle_game.py --zip
```

This creates a `bundle_output.zip` containing:
- `Stardew Valley.dll` (v1.6+)
- `StardewModdingAPI.dll` & `smapi-internal/` (if SMAPI is installed)
- `Content/` (all textures, maps, sounds, data)
- Required game dependencies (`xTile`, `BmFont`, `Lidgren.Network`)

---

### Step 2: Build the IPA via GitHub Actions

1. Push this repository to your GitHub account (fork or private repo).
2. Go to the **Actions** tab in your repository.
3. Select **Build Stardew Valley iOS IPA**.
4. Click **Run workflow** (branch: `main`).
5. Once the build finishes (approx. 3–5 minutes), download `StardewValley-iOS.ipa` from the workflow summary artifacts.

---

### Step 3: Sideload onto Your iPhone / iPad

Install `StardewValley-iOS.ipa` using your preferred sideloading method:

| Method | iOS Version | JIT Support | Re-signing Needed? |
| :--- | :--- | :--- | :--- |
| **TrollStore** | iOS 14.0 – 17.0 | Permanent Native | No (Permanent) |
| **AltStore / SideStore** | All iOS versions | Supported (AltJIT / SideJIT) | Every 7 days |
| **Sideloadly** | All iOS versions | Supported | Every 7 days |
| **Feather / Scarlet** | All iOS versions | Supported | Depends on cert |
| **Apple Developer** | All iOS versions | Supported | 1 year |

---

### Step 4: Add Game Files & Mods via Files App

1. Launch **Stardew Valley** on your device once so it creates the default folders.
2. Open the native **Files** app on iOS.
3. Navigate to **On My iPhone (or iPad) > Stardew Valley**:
   ```text
   📁 Stardew Valley/
      📁 Content/       <-- Game assets
      📁 Mods/          <-- Put SMAPI mods here!
      📁 Saves/         <-- Your save games (synced with PC)
      📁 ErrorLogs/     <-- SMAPI logs
   ```
4. If you did not bundle the game assets inside the IPA, copy your `Content/` folder and game DLLs into this directory.
5. To install mods, simply download or AirDrop any standard PC SMAPI mod zip, extract it, and place the mod folder into `Stardew Valley/Mods/`.

---

## Controls & Key Mapping

| Virtual Button | Gamepad (Xbox / PS) | Action / PC Binding |
| :--- | :--- | :--- |
| **Virtual Stick** | Left Stick / D-Pad | Movement (WASD / Arrows) |
| **Button A** | A / Cross | Action / Talk / Left-Click |
| **Button X** | X / Square | Use Tool / Eat / Right-Click |
| **Button Y** | Y / Triangle | Inventory / Menu (E key) |
| **Button B** | B / Circle | Cancel / Escape |
| **Top-Right Button** | - | Hide / Show Virtual Overlay |
| **Screen Tap** | - | Mouse Click / Dialogue selection |

---

## Technical Architecture

- **Host App**: .NET 8 for iOS (`net8.0-ios`) utilizing Apple's modern toolchain.
- **Graphics Backend**: `MonoGame.Framework.iOS` utilizing Metal on iOS.
- **Execution Mode**: `<UseInterpreter>true</UseInterpreter>` enables Mono IL interpreter, bypassing Apple's W^X restrictions while running dynamic mod assemblies.
- **Stubs**: Safe no-op stubs for desktop-only libraries (`Steamworks.NET`, `GalaxyCSharp`) ensure PC assemblies launch cleanly without desktop background clients.

---

## License

This runner code is licensed under the MIT License. Stardew Valley is a trademark of ConcernedApe LLC. This project contains no proprietary game code or copyrighted assets.

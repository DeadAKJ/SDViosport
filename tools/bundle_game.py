#!/usr/bin/env python3
import os
import sys
import shutil
import argparse
import zipfile

DEFAULT_STEAM_PATH_WIN = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
DEFAULT_STEAM_PATH_MAC = os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS")
DEFAULT_STEAM_PATH_LINUX = os.path.expanduser("~/.local/share/Steam/steamapps/common/Stardew Valley")

def detect_steam_path():
    if os.path.exists(DEFAULT_STEAM_PATH_WIN):
        return DEFAULT_STEAM_PATH_WIN
    if os.path.exists(DEFAULT_STEAM_PATH_MAC):
        return DEFAULT_STEAM_PATH_MAC
    if os.path.exists(DEFAULT_STEAM_PATH_LINUX):
        return DEFAULT_STEAM_PATH_LINUX
    return None

def main():
    parser = argparse.ArgumentParser(description="Bundle Stardew Valley PC assets for iOS port")
    parser.add_argument("--game-path", "-g", default=None, help="Path to Stardew Valley game directory")
    parser.add_argument("--output", "-o", default="bundle_output", help="Output directory")
    parser.add_argument("--vanilla", action="store_true", help="Bundle pure vanilla game (no SMAPI or mods)")
    parser.add_argument("--include-mods", action="store_true", help="Include installed mods folder")
    parser.add_argument("--zip", action="store_true", help="Create a zip archive")
    args = parser.parse_args()

    game_path = args.game_path or detect_steam_path()
    if not game_path or not os.path.exists(game_path):
        print(f"Error: Game path not found: {game_path}. Use --game-path to specify.")
        sys.exit(1)

    print(f"[1/4] Found Stardew Valley at: {game_path}")
    sdv_dll = os.path.join(game_path, "Stardew Valley.dll")
    content_dir = os.path.join(game_path, "Content")

    if not os.path.exists(sdv_dll):
        print(f"Error: Stardew Valley.dll missing in {game_path}")
        sys.exit(1)
    if not os.path.exists(content_dir):
        print(f"Error: Content directory missing in {game_path}")
        sys.exit(1)

    has_smapi = os.path.exists(os.path.join(game_path, "StardewModdingAPI.dll")) and not args.vanilla
    print(f"  Mode: {'Pure Vanilla (No Mods)' if args.vanilla else 'SMAPI Modded'}")

    output_dir = args.output
    if os.path.exists(output_dir):
        shutil.rmtree(output_dir)
    os.makedirs(output_dir, exist_ok=True)

    print(f"[2/4] Copying assemblies and data...")
    dlls = [
        "Stardew Valley.dll",
        "StardewValley.GameData.dll",
        "xTile.dll",
        "BmFont.dll",
        "CPExtBmFont.dll",
        "Lidgren.Network.dll"
    ]
    if has_smapi:
        dlls.append("StardewModdingAPI.dll")

    for dll in dlls:
        src = os.path.join(game_path, dll)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(output_dir, dll))
            print(f"  Copied {dll}")

    if has_smapi:
        smapi_internal = os.path.join(game_path, "smapi-internal")
        if os.path.exists(smapi_internal):
            print("  Copying smapi-internal...")
            shutil.copytree(smapi_internal, os.path.join(output_dir, "smapi-internal"))
    else:
        with open(os.path.join(output_dir, "force_vanilla.txt"), "w") as f:
            f.write("Vanilla Mode Active")

    print("[3/4] Copying Content directory...")
    shutil.copytree(content_dir, os.path.join(output_dir, "Content"))

    if args.include_mods and not args.vanilla:
        mods_dir = os.path.join(game_path, "Mods")
        if os.path.exists(mods_dir):
            print("  Copying Mods directory...")
            shutil.copytree(mods_dir, os.path.join(output_dir, "Mods"))

    if args.zip:
        zip_path = f"{output_dir}.zip"
        print(f"[4/4] Creating zip archive: {zip_path}...")
        with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as z:
            for root, _, files in os.walk(output_dir):
                for f in files:
                    full = os.path.join(root, f)
                    rel = os.path.relpath(full, output_dir)
                    z.write(full, rel)
        print(f"Archive created: {zip_path}")

    print("Done! Game package ready for iOS deployment.")

if __name__ == "__main__":
    main()

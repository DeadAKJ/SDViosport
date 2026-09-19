import os
import sys
import zipfile
import shutil
import subprocess

def inject_game(ipa_path, game_files_dir, output_ipa_path):
    print(f"=== Injecting Game Files into IPA ===")
    print(f"Source IPA: {ipa_path}")
    print(f"Game Files: {game_files_dir}")
    print(f"Output IPA: {output_ipa_path}")

    if not os.path.exists(ipa_path):
        print(f"Error: IPA not found: {ipa_path}")
        return False
    if not os.path.exists(game_files_dir):
        print(f"Error: Game files dir not found: {game_files_dir}")
        return False

    # Ensure 0Harmony.dll is patched for iOS
    harmony_dll = os.path.join(game_files_dir, "smapi-internal", "0Harmony.dll")
    if os.path.exists(harmony_dll):
        patcher_csproj = os.path.join(os.path.dirname(__file__), "PatchHarmony", "PatchHarmony.csproj")
        if os.path.exists(patcher_csproj):
            print("Ensuring 0Harmony.dll is patched for iOS...")
            subprocess.run(["dotnet", "run", "--project", patcher_csproj, harmony_dll], check=False)

    temp_dir = os.path.join(os.path.dirname(output_ipa_path), "temp_inject")
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)
    os.makedirs(temp_dir, exist_ok=True)

    print("[1/3] Unpacking IPA...")
    with zipfile.ZipFile(ipa_path, 'r') as z:
        z.extractall(temp_dir)

    payload_dir = os.path.join(temp_dir, "Payload")
    app_dirs = [d for d in os.listdir(payload_dir) if d.endswith(".app")]
    if not app_dirs:
        print("Error: No .app folder in Payload!")
        return False

    app_dir = os.path.join(payload_dir, app_dirs[0])
    print(f"[2/3] Injecting game files into {app_dirs[0]}...")

    # Copy files from game_files_dir into app_dir
    file_count = 0
    for root, dirs, files in os.walk(game_files_dir):
        rel_root = os.path.relpath(root, game_files_dir)
        target_root = app_dir if rel_root == "." else os.path.join(app_dir, rel_root)
        os.makedirs(target_root, exist_ok=True)
        for f in files:
            src_f = os.path.join(root, f)
            dst_f = os.path.join(target_root, f)
            shutil.copy2(src_f, dst_f)
            file_count += 1
            if file_count % 500 == 0:
                print(f"  Injected {file_count} files...")

    print(f"  Total injected: {file_count} files.")

    # Ensure MonoGame.Framework.dll in app is patched for Audio
    app_mg_dll = os.path.join(app_dir, "MonoGame.Framework.dll")
    if os.path.exists(app_mg_dll):
        mg_patcher_csproj = os.path.join(os.path.dirname(__file__), "PatchMonoGame", "PatchMonoGame.csproj")
        if os.path.exists(mg_patcher_csproj):
            print("Ensuring MonoGame.Framework.dll is patched for Audio in .app...")
            subprocess.run(["dotnet", "run", "--project", mg_patcher_csproj, app_mg_dll], check=False)

    print(f"[3/3] Repackaging IPA to {output_ipa_path}...")
    if os.path.exists(output_ipa_path):
        os.remove(output_ipa_path)

    with zipfile.ZipFile(output_ipa_path, 'w', zipfile.ZIP_STORED) as z_out:
        for root, dirs, files in os.walk(temp_dir):
            for f in files:
                full_path = os.path.join(root, f)
                arcname = os.path.relpath(full_path, temp_dir)
                z_out.write(full_path, arcname)

    shutil.rmtree(temp_dir)
    size_mb = os.path.getsize(output_ipa_path) / (1024 * 1024)
    print(f"=== Success! Bundled IPA created: {output_ipa_path} ({size_mb:.2f} MB) ===")
    return True

def cleanup_previous_ipas(keep_dir, root_bundled):
    base_dir = r"C:\Users\User\Desktop\SDVport Version"
    print(f"Cleaning up previous IPAs outside {keep_dir}...")
    count = 0
    freed = 0
    for root, dirs, files in os.walk(base_dir):
        if os.path.abspath(root).startswith(os.path.abspath(keep_dir)):
            continue
        for f in files:
            if f.endswith(".ipa"):
                full = os.path.join(root, f)
                if os.path.abspath(full) == os.path.abspath(root_bundled):
                    continue
                try:
                    sz = os.path.getsize(full)
                    os.remove(full)
                    count += 1
                    freed += sz
                except Exception as ex:
                    print(f"  Could not remove {full}: {ex}")
    print(f"Purged {count} previous IPA(s), freed {freed / (1024*1024):.2f} MB.")

if __name__ == "__main__":
    if len(sys.argv) > 1:
        version_dir = sys.argv[1]
    else:
        version_dir = r"C:\Users\User\Desktop\SDVport Version\v1.0.23-fix-smapi-console-thread-and-logwriter"
    ipa = os.path.join(version_dir, "StardewValley-iOS.ipa")
    game = r"C:\Users\User\Desktop\SDVport Version\Game_Files"
    out_version = os.path.join(version_dir, "StardewValley-Bundled.ipa")
    out_root = r"C:\Users\User\Desktop\SDVport Version\StardewValley-Bundled.ipa"

    success = inject_game(ipa, game, out_version)
    if success:
        print(f"Copying to root: {out_root}...")
        shutil.copy2(out_version, out_root)
        cleanup_previous_ipas(version_dir, out_root)
        print("Done!")

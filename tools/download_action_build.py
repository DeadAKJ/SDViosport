import urllib.request
import json
import time
import zipfile
import io
import os
import sys
import subprocess
import shutil

# Load GitHub Token from environment or local gitignored token.txt
token = os.environ.get('GITHUB_TOKEN')
token_file = os.path.join(os.path.dirname(__file__), 'token.txt')
if not token and os.path.exists(token_file):
    with open(token_file, 'r', encoding='utf-8-sig') as f:
        token = f.read().strip().replace('\ufeff', '')

if not token:
    print("Warning: GITHUB_TOKEN not found in environment or tools/token.txt.")

repo = 'DeadAKJ/SDViosport'
version_name = 'v1.1.3-fix-furniture-and-camera'

changelog_content = """Version: v1.1.3-fix-furniture-and-camera
Date: 2026-09-24

Changes:
1. Fix Vertical Screen Shake / Camera Judder on Scrolling Maps:
   - Root Cause:
     * In `ExecuteDirectGameTick`, `ReviveGraphicsDeviceAndInstances(runner, plat, null, gameView)` and `AttachTouchOverlayToGameRunner()` were executing on every frame (60Hz).
     * `ReviveGraphicsDeviceAndInstances` continuously forced `PreferredBackBufferWidth = 1792` and `PreferredBackBufferHeight = 828` on `GraphicsDeviceManager`, synchronizing `Game1.defaultDeviceViewport` and `GraphicsDevice.Viewport` 60 times a second.
     * This 60Hz viewport resetting fought `Game1.UpdateViewPort()`'s camera lerp (`viewportPositionLerp`) whenever moving on maps longer than the screen height (Farm, Town, long interiors), creating severe vertical camera jitter.
   - Fix:
     * Guarded `ReviveGraphicsDeviceAndInstances` so it only executes on cold boot frame 0 or when `runner.GraphicsDevice == null || runner.GraphicsDevice.IsDisposed`.
     * Guarded `AttachTouchOverlayToGameRunner` with `_touchOverlayAttached` flag so it only runs once.

2. Fix Corrupted Placeable Furniture Sprites:
   - Root Cause:
     * Placeable furniture items (table, chair, rug, TV, fireplace) use `TileSheets/furniture.png` (512x1488, which is non-power-of-two).
     * In MonoGame's `GraphicsCapabilities.PlatformInitialize`, NPOT support was checked via OpenGL ES extension strings (`GL_OES_texture_npot`, `GL_ARB_texture_non_power_of_two`), which are not listed on iOS Metal-backed GLES 3.0 where NPOT is standard core.
     * Consequently, `SupportsNonPowerOfTwo` defaulted to `false`. MonoGame's `Texture2DReader.Read` clamped level output and OpenGL texture sampling modes failed for non-power-of-two furniture sheets.
     * Furthermore, 60Hz backbuffer resizing in `ReviveGraphicsDeviceAndInstances` triggered `GraphicsDevice.Resetting` and mid-frame texture recreation.
   - Fix:
     * Patched `GraphicsCapabilities.get_SupportsNonPowerOfTwo` in `MonoGame.Framework.dll` to always return `true`, and neutralized `set_SupportsNonPowerOfTwo`.
     * Stopped mid-frame graphics resets.

3. Fix Virtual Keyboard Double Text Input:
   - Root Cause:
     * When the user tapped a text input field, `SimulatedMousePosition` in `TouchVirtualPad` remained at the tapped coordinates.
     * After dismissing the keyboard, `TextBox.Update()` checked `if (boundingBox.Contains(mousePoint))` which evaluated to `true`, instantly re-selecting the textbox and reopening the keyboard dialog.
     * Multiple alert dismissals and return callbacks could also race.
   - Fix:
     * Added `ResetSimulatedMouse()` in `TouchVirtualPad` to move simulated mouse off-screen `(-1000, -1000)` upon text submit/cancel.
     * Cleared `Game1.keyboardDispatcher.Subscriber = null` and `subscriber.Selected = false`.
     * Added a 1.0-second debounce per subscriber to prevent re-opening loops.
"""




headers = {
    'Accept': 'application/vnd.github.v3+json',
    'User-Agent': 'Mozilla/5.0'
}
if token:
    headers['Authorization'] = f'Bearer {token}'

class NoAuthRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new_req = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new_req and 'Authorization' in new_req.headers:
            del new_req.headers['Authorization']
        return new_req

# Determine run_id: from command line arg or fetch latest run matching current git commit
run_id = sys.argv[1] if len(sys.argv) > 1 else None

if not run_id:
    try:
        git_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        current_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=git_dir).decode().strip()
        print(f"Targeting git commit SHA: {current_sha[:8]}")
        print(f"Waiting for GitHub Actions run matching commit {current_sha[:8]}...")
        for attempt in range(24): # wait up to 2 minutes for run to appear
            try:
                url = f'https://api.github.com/repos/{repo}/actions/runs?per_page=10'
                req = urllib.request.Request(url, headers=headers)
                with urllib.request.urlopen(req) as resp:
                    data = json.loads(resp.read().decode())
                    for r in data.get('workflow_runs', []):
                        if r.get('head_sha') == current_sha:
                            run_id = str(r['id'])
                            print(f"Detected matched run: {run_id} ({r.get('name', 'Workflow')})")
                            break
            except Exception as e:
                print(f"Error checking runs: {e}")
            if run_id:
                break
            time.sleep(5)
        if not run_id:
            print("Timed out waiting for GitHub Actions workflow to start.")
            sys.exit(1)
    except Exception as e:
        print(f"Error detecting git commit or runs: {e}")
        sys.exit(1)

if len(sys.argv) > 2:
    version_name = sys.argv[2]

print(f"Monitoring GitHub Actions Run {run_id} ({version_name})...")
start_time = time.time()

while True:
    try:
        url = f'https://api.github.com/repos/{repo}/actions/runs/{run_id}'
        req = urllib.request.Request(url, headers=headers)
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            status = data.get('status')
            conclusion = data.get('conclusion')
            elapsed = int(time.time() - start_time)
            print(f"[{elapsed}s] Status: {status}, Conclusion: {conclusion}")

            if status == 'completed':
                if conclusion != 'success':
                    print(f"Run ended with conclusion: {conclusion}")
                    sys.exit(1)
                break
    except Exception as e:
        print(f"Error checking run: {e}")
    time.sleep(30)

print("\nWorkflow completed successfully! Looking for IPA artifact...")
artifacts_url = f'https://api.github.com/repos/{repo}/actions/runs/{run_id}/artifacts'
req = urllib.request.Request(artifacts_url, headers=headers)
with urllib.request.urlopen(req) as resp:
    data = json.loads(resp.read().decode())

ipa_artifact = None
for a in data.get('artifacts', []):
    print(f"Found artifact: {a['name']} ({a['size_in_bytes']} bytes)")
    if 'ipa' in a['name'].lower() or 'ios' in a['name'].lower() or 'stardew' in a['name'].lower():
        ipa_artifact = a
        break

if not ipa_artifact and data.get('artifacts'):
    ipa_artifact = data['artifacts'][0]

if not ipa_artifact:
    print("No artifact found!")
    sys.exit(1)

download_url = ipa_artifact['archive_download_url']
print(f"Downloading artifact {ipa_artifact['name']} from {download_url}...")

opener = urllib.request.build_opener(NoAuthRedirectHandler)
req = urllib.request.Request(download_url, headers=headers)

target_dir = os.path.join(r"C:\Users\User\Desktop\SDVport Version\Post-Audio", version_name)
logs_dir = os.path.join(target_dir, "logs")
os.makedirs(logs_dir, exist_ok=True)

changelog_path = os.path.join(target_dir, "changelog.txt")
with open(changelog_path, "w", encoding="utf-8") as f:
    f.write(changelog_content)
print(f"Created changelog: {changelog_path}")
print(f"Created logs folder: {logs_dir}")

with opener.open(req) as resp:
    zip_bytes = resp.read()
    print(f"Downloaded {len(zip_bytes)} bytes.")
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as z:
        for filename in z.namelist():
            print(f"Extracting {filename} to {target_dir}...")
            z.extract(filename, target_dir)

# Copy standalone IPA to base directory root
base_dir = r"C:\Users\User\Desktop\SDVport Version"
root_ipa = os.path.join(base_dir, "StardewValley-iOS.ipa")
extracted_ipa = os.path.join(target_dir, "StardewValley-iOS.ipa")
if os.path.exists(extracted_ipa):
    try:
        shutil.copy2(extracted_ipa, root_ipa)
        print(f"Copied standalone IPA to: {root_ipa}")
    except Exception as e:
        print(f"Warning: could not copy to root: {e}")

# Purge previous version IPAs to conserve disk space
freed_bytes = 0
purged_count = 0
for r, d, f_list in os.walk(base_dir):
    if os.path.abspath(r).startswith(os.path.abspath(target_dir)):
        continue
    for f in f_list:
        if f.endswith(".ipa"):
            full_p = os.path.join(r, f)
            if os.path.abspath(full_p) == os.path.abspath(root_ipa):
                continue
            try:
                freed_bytes += os.path.getsize(full_p)
                os.remove(full_p)
                purged_count += 1
            except Exception as e:
                print(f"  Warning: could not delete {full_p}: {e}")

if purged_count > 0:
    print(f"Purged {purged_count} old version IPA(s), freed {freed_bytes / (1024*1024):.2f} MB.")

print("\n==========================================")
print(f"Build {version_name} successfully delivered to:")
print(f"  {target_dir}")
print(f"Files:")
for root, dirs, files in os.walk(target_dir):
    for file in files:
        rel = os.path.relpath(os.path.join(root, file), target_dir)
        size_mb = os.path.getsize(os.path.join(root, file)) / (1024 * 1024)
        print(f"  - {rel} ({size_mb:.2f} MB)")
print("==========================================")

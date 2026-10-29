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
version_name = 'v3.3.0-save-state-ui-scale-tool-swing-fix'

changelog_content = """Version: v3.3.0-save-state-ui-scale-tool-swing-fix
Date: 2026-10-29

Changes:
1. Midday Save State Manager (QuickSaveManager):
   - Added in-game midday save state saving and loading via overlay drawer buttons ('SAVE STATE' / 'LOAD STATE') and console commands ('quick_save', 'quick_load').
   - Cleanly serializes world state to midday save files and restores on demand.

2. Fix In-Game QuickLoad & Exit-to-Title Crashes:
   - Root Cause:
     * Calling helper.GameContent.InvalidateCache during title return or quick-load triggered SMAPI's CoreAssetPropagator on Data/AudioChanges, throwing MissingFieldException: CueDefinition.name in AudioCueModificationManager.ApplyAllCueModifications().
     * Simultaneously triggered mass table re-propagation across dozens of data assets, exhausting RAM (~19 MB headroom remaining) and triggering immediate iOS Jetsam SIGKILL OOM.
   - Fix:
     * Removed broad InvalidateCache calls during teardown.
     * Implemented IsPurgeableAsset and IsCoreDisposable to safely evict and dispose unneeded map/character textures from MonoGame ContentManager instances while protecting audio, UI, and title data.
     * QuickSaveManager performs safe world texture eviction, runs full GC flush, and triggers SaveGame.Load without crashing or re-entering audio propagator loops.

3. Fix Touch Cursor Desync with UI Scaling:
   - Root Cause:
     * TouchVirtualPad was applying uiScale division to touch coordinates, and Stardew Valley's getMousePosition(true) divided by uiScale again, resulting in a quadratic offset (touch / uiScale^2).
     * CurrentSimulatedMouseState was clamping coordinates to scaled UI viewport dimensions rather than raw screen bounds.
   - Fix:
     * SimulatedMousePosition is now maintained and clamped strictly in raw screen pixels (0.._rawScreenWidth, 0.._rawScreenHeight).
     * Game1's built-in single uiScale conversion accurately aligns cursor position directly under the finger.
     * OnGameSetMousePosition sets raw window coordinates directly without division.
     * Overlay feedback rendering in Draw scales coordinates appropriately for uiScreen.

4. Universal Tool & Melee Weapon Swinging via Button X:
   - Root Cause:
     * Game1.didPlayerJustLeftClick(true) inspects hardware input state directly, returning false on iOS touch controls without a physical mouse/keyboard/gamepad, blocking player.FireTool() and player.BeginUsingTool() on MeleeWeapon items.
     * MeleeWeapon.leftClick() explicitly ignores scythes (!isScythe()) and only triggers combo animations when already swinging.
     * Non-melee tools were omitted from post-input handling and targeted mouse coordinates (often hitting UI menus or distant tiles).
   - Fix:
     * Added (ButtonX ? Buttons.X : 0) to EffectiveGamePadState.
     * In TouchDelegatingModHooks, Button X directly calls player.Halt(); player.BeginUsingTool(); when starting a swing, and weapon.leftClick(player) with anotherClick = true when continuing a combo.
     * For non-melee tools (pickaxes, axes, hoes, watering cans, fishing rods), temporarily sets Game1.wasMouseVisibleThisFrame = false and calls Game1.pressUseToolButton(), ensuring tools target the tile directly in front of the farmer without cursor misdirection.
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

target_dir = os.path.join(r"C:\Users\User\Desktop\SDVport Version\Post-Audio", version_name)
logs_dir = os.path.join(target_dir, "logs")
os.makedirs(logs_dir, exist_ok=True)

changelog_path = os.path.join(target_dir, "changelog.txt")
with open(changelog_path, "w", encoding="utf-8") as f:
    f.write(changelog_content)
print(f"Created changelog: {changelog_path}")
print(f"Created logs folder: {logs_dir}")

artifact_id = str(ipa_artifact.get('id', ''))
candidate_urls = []
if token and 'archive_download_url' in ipa_artifact:
    candidate_urls.append((ipa_artifact['archive_download_url'], headers))
if artifact_id:
    candidate_urls.append((f"https://nightly.link/{repo}/actions/artifacts/{artifact_id}.zip", {'User-Agent': 'Mozilla/5.0'}))
candidate_urls.append((f"https://nightly.link/{repo}/workflows/build-ipa.yml/main/StardewValley-iOS.zip", {'User-Agent': 'Mozilla/5.0'}))

zip_bytes = None
for d_url, d_headers in candidate_urls:
    try:
        print(f"Attempting download from: {d_url}...")
        opener = urllib.request.build_opener(NoAuthRedirectHandler)
        req = urllib.request.Request(d_url, headers=d_headers)
        with opener.open(req, timeout=120) as resp:
            content = resp.read()
            if len(content) > 1000:
                zip_bytes = content
                print(f"Successfully downloaded {len(zip_bytes)} bytes.")
                break
    except Exception as e:
        print(f"Download attempt failed: {e}")

if not zip_bytes or len(zip_bytes) < 1000:
    print("Error: Could not download artifact from any candidate URL.")
    sys.exit(1)

with zipfile.ZipFile(io.BytesIO(zip_bytes)) as z:
    for filename in z.namelist():
        print(f"Extracting {filename} to {target_dir}...")
        z.extract(filename, target_dir)

# Verify extracted IPA integrity
extracted_ipa = os.path.join(target_dir, "StardewValley-iOS.ipa")
if os.path.exists(extracted_ipa):
    try:
        with zipfile.ZipFile(extracted_ipa, 'r') as ipa_zip:
            bad_file = ipa_zip.testzip()
            if bad_file:
                print(f"Warning: Corrupted file detected inside IPA: {bad_file}")
            else:
                payload_files = [n for n in ipa_zip.namelist() if n.startswith("Payload/")]
                print(f"IPA integrity verified: {len(payload_files)} files present in Payload bundle.")
    except Exception as e:
        print(f"Warning: IPA integrity verification exception: {e}")

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

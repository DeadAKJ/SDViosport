import urllib.request
import json
import time
import zipfile
import io
import os
import sys

# Load GitHub Token from environment or local gitignored token.txt
token = os.environ.get('GITHUB_TOKEN')
token_file = os.path.join(os.path.dirname(__file__), 'token.txt')
if not token and os.path.exists(token_file):
    with open(token_file, 'r', encoding='utf-8') as f:
        token = f.read().strip()

if not token:
    print("Warning: GITHUB_TOKEN not found in environment or tools/token.txt.")

repo = 'DeadAKJ/SDViosport'
version_name = 'v1.0.0-initial-smapi-ios-port'

changelog_content = """Version: v1.0.0-initial-smapi-ios-port
Date: 2026-09-12

Changes:
1. Native .NET 8 iOS Host & Mono Interpreter:
   - Configured with net8.0-ios and <UseInterpreter>true</UseInterpreter> to bypass Apple's W^X memory restrictions.
   - Allows loading and executing dynamic SMAPI assemblies and mod DLLs on iOS without jailbreak.

2. Comprehensive Engine Diagnostics & Logging:
   - Full stdout/stderr dual-output logging directly into Documents/StardewValley/ErrorLogs/engine-latest.log.
   - Log files are accessible directly on device via the iOS Files app (On My iPhone > Stardew Valley > ErrorLogs).
   - Real-time logging of assembly resolutions, lifecycle states, and fatal crash stack traces.

3. iOS Files App Storage Integration:
   - Enabled UIFileSharingEnabled and LSSupportsOpeningDocumentsInPlace.
   - User folders for Content, Mods, Saves, and ErrorLogs are fully exposed in the Files app.
   - Allows drag-and-drop SMAPI mod installation directly on iPhone and iPad.

4. Virtual Touchpad & Gamepad Engine:
   - On-screen virtual analog thumbstick for fluid player movement.
   - Action buttons: Action/Talk (A), Tool (X), Inventory/Menu (Y), Cancel/Back (B).
   - Tap-to-mouse emulation across screen for dialogs, shops, and inventory slots.
   - Auto-detection and passthrough for physical MFi, DualSense, Xbox, and Backbone controllers.
   - One-tap toggle button to hide/show virtual controls.

5. PC Stubs for Seamless Boot:
   - Safe desktop stubs for Steamworks.NET and GalaxyCSharp to ensure clean startup without desktop background processes.

6. Automated CI/CD:
   - GitHub Actions pipeline on macos-14 packaging StardewValley-iOS.ipa.
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

# Determine run_id: from command line arg or fetch latest run
run_id = sys.argv[1] if len(sys.argv) > 1 else None

if not run_id:
    print(f"Fetching latest GitHub Actions run for {repo}...")
    try:
        url = f'https://api.github.com/repos/{repo}/actions/runs?per_page=1'
        req = urllib.request.Request(url, headers=headers)
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            runs = data.get('workflow_runs', [])
            if runs:
                run_id = str(runs[0]['id'])
                print(f"Detected latest run: {run_id} ({runs[0].get('name', 'Workflow')})")
            else:
                print("No workflow runs found yet for this repository.")
                sys.exit(1)
    except Exception as e:
        print(f"Error fetching latest run: {e}")
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

target_dir = os.path.join(r"C:\Users\User\Desktop\SDVport Version", version_name)
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

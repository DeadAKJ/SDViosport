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
version_name = 'v1.0.48-fix-harmony-verification-exception'

changelog_content = """Version: v1.0.48-fix-harmony-verification-exception
Date: 2026-09-19

Changes:
1. Fix 0Harmony VerificationException / Black Screen:
   - Root Cause: In v1.0.47, RuntimeHarmonyPatcher emptied GetOrCreateSharedStateType instructions without clearing ExceptionHandlers. The leftover exception handler pointed to non-existent instruction offset 39, triggering System.Security.VerificationException: Invalid instruction target 39, which aborted SMAPI initialization before Stardew Valley launched.
   - Fix: RuntimeHarmonyPatcher now explicitly clears ExceptionHandlers and Variables when replacing GetOrCreateSharedStateType and neutralizing .cctor.
   - Cleaned any existing corrupted exception handlers in already-patched 0Harmony.dll instances.
2. Forced Sync of smapi-internal:
   - SyncBundledDirectory now forces overwriting of Documents/smapi-internal from app bundle so clean pre-patched assemblies always replace stale copies in app container.
3. VirtualKeyboardManager Cleanups:
   - Throttled reflection initialization and guarded dispatcher property getters to prevent TargetInvocationException warnings on uninitialized frames.
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

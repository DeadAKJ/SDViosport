"""
Backend module for Stardew Valley iOS Mod Manager.
Handles Apple USB AFC communication and mod/save/log operations.
"""

import os
import sys
import re
import json
import zipfile
import shutil
import tempfile
import asyncio
import subprocess
import urllib.request
from typing import Optional, Callable, Any
from dataclasses import dataclass, field

try:
    from pymobiledevice3.usbmux import list_devices
    from pymobiledevice3.lockdown import create_using_usbmux
    from pymobiledevice3.services.house_arrest import HouseArrestService
    from pymobiledevice3.services.installation_proxy import InstallationProxyService
    PYMD3_AVAILABLE = True
except ImportError:
    PYMD3_AVAILABLE = False


@dataclass
class ModInfo:
    folder_name: str
    name: str
    author: str = "Unknown"
    version: str = "1.0.0"
    description: str = ""
    unique_id: str = ""
    is_enabled: bool = True
    minimum_api_version: str = ""
    dependencies: list[str] = field(default_factory=list)
    content_pack_for: Optional[str] = None
    is_content_pack: bool = False
    is_code_mod: bool = False
    nexus_id: Optional[int] = None
    update_keys: list[str] = field(default_factory=list)


KNOWN_FRAMEWORK_NEXUS_IDS = {
    "pathoschild.contentpatcher": 1915,
    "platonymous.farmtypemanager": 3231,
    "esca.farmtypemanager": 3231,
    "spacechase0.spacecore": 1348,
    "spacechase0.jsonassets": 1720,
    "spacechase0.genericmodconfigmenu": 5098,
    "cherry.expandedpreconditionsutility": 6529,
    "flashshifter.stardewvalleyexpandedcp": 3753,
    "spacechase0.dynamicgameassets": 9350,
    "pathoschild.lookupanything": 541,
    "pathoschild.automate": 1063,
}


def clean_json(text: str) -> str:
    """Strip C-style comments (/* ... */ and // ...) and trailing commas from JSON for SMAPI manifest compatibility."""
    # 1. Strip block comments /* ... */
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.DOTALL)
    # 2. Strip line comments // ... but preserve http:// and https://
    lines = []
    for line in text.splitlines():
        clean_line = re.sub(r'(?<!http:)(?<!https:)//.*$', '', line)
        lines.append(clean_line)
    text = '\n'.join(lines)
    # 3. Strip trailing commas before } or ] (repeat for nested structures)
    for _ in range(3):
        text = re.sub(r',\s*([}\]])', r'\1', text)
    return text


def is_requirement_installed(req_mod_id: Optional[int], req_name: str, installed_mods: list[ModInfo]) -> bool:
    """Check if a required mod is satisfied by any of the installed mods."""
    if req_mod_id == 2400 or "smapi" in req_name.lower():
        return True

    # Normalize req name (strip notes/framework indicators and spaces/punctuation)
    clean_req = re.sub(r"\([^)]*\)", "", req_name).strip().lower().replace(" ", "").replace("_", "").replace("-", "")

    for m in installed_mods:
        if req_mod_id and m.nexus_id and m.nexus_id == req_mod_id:
            return True
        if m.unique_id and req_mod_id and m.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
            if KNOWN_FRAMEWORK_NEXUS_IDS[m.unique_id.lower()] == req_mod_id:
                return True
        m_uid_clean = m.unique_id.lower().replace(".", "").replace(" ", "").replace("_", "").replace("-", "")
        if clean_req and (clean_req == m_uid_clean or clean_req in m_uid_clean or m_uid_clean in clean_req):
            return True
        m_name_clean = m.name.lower().replace(" ", "").replace("_", "").replace("-", "")
        if clean_req and (clean_req == m_name_clean or clean_req in m_name_clean or m_name_clean in clean_req):
            return True

    return False


def parse_version_tuple(v: str) -> list:
    """Parse version string into comparable token list."""
    if not v:
        return []
    clean = re.sub(r'^[vV]', '', str(v).strip())
    tokens = re.findall(r'\d+|[a-zA-Z]+', clean)
    parsed = []
    for t in tokens:
        if t.isdigit():
            parsed.append(int(t))
        else:
            parsed.append(t.lower())
    return parsed


def is_version_newer(remote_v: str, local_v: str) -> bool:
    """Return True if remote_v is strictly newer than local_v."""
    if not remote_v or not local_v:
        return False
    r = parse_version_tuple(remote_v)
    l = parse_version_tuple(local_v)
    while len(r) > 0 and r[-1] == 0:
        r.pop()
    while len(l) > 0 and l[-1] == 0:
        l.pop()
    for ri, li in zip(r, l):
        if type(ri) is type(li):
            if ri > li:
                return True
            if ri < li:
                return False
        else:
            if isinstance(ri, int) and isinstance(li, str):
                return True
            elif isinstance(ri, str) and isinstance(li, int):
                return False
    return len(r) > len(l)


class IOSModBackend:
    """Manages iOS device connection and mod operations over AFC."""

    def __init__(self):
        self.lockdown = None
        self.house_arrest: Optional[HouseArrestService] = None
        self.device_info: dict = {}
        self.bundle_id: str = ""
        self.is_connected: bool = False
        self.local_mode_dir: Optional[str] = None  # If user selects a mounted PC folder
        self._save_locations: dict[str, str] = {}

    async def list_usb_devices(self) -> list[dict]:
        """List all connected iOS devices over USB."""
        if not PYMD3_AVAILABLE:
            return []
        try:
            devices = await list_devices()
            results = []
            for d in devices:
                results.append({
                    "devid": d.devid,
                    "serial": d.serial,
                    "connection_type": d.connection_type
                })
            return results
        except Exception as e:
            print(f"[Backend] Error listing devices: {e}")
            return []

    async def connect_usb(self, serial: Optional[str] = None) -> tuple[bool, str]:
        """Connect to an iOS device and vend Stardew Valley Documents."""
        if not PYMD3_AVAILABLE:
            return False, "pymobiledevice3 library not installed."

        try:
            self.lockdown = await create_using_usbmux(serial=serial)
            self.device_info = self.lockdown.short_info
            dev_name = self.device_info.get("DeviceName", "iOS Device")
            model = self.device_info.get("ProductType", "iPhone")
            os_ver = self.device_info.get("ProductVersion", "iOS")

            # 1. Discover installed Stardew Valley app bundle ID
            bundle_id = None
            try:
                async with InstallationProxyService(self.lockdown) as ip:
                    apps = await ip.get_apps()
                    for bid in apps.keys():
                        b_lower = bid.lower()
                        if "deadakj.sdvios" in b_lower or "stardew" in b_lower or "sdvios" in b_lower:
                            bundle_id = bid
                            break
            except Exception as e:
                print(f"[Backend] Warning during app discovery: {e}")

            if not bundle_id:
                # Default to known bundle ID
                bundle_id = "com.deadakj.sdvios"

            self.bundle_id = bundle_id
            print(f"[Backend] Connecting to container: {self.bundle_id}")

            # 2. Connect via HouseArrest (VendDocuments)
            self.house_arrest = await HouseArrestService.create(
                self.lockdown, self.bundle_id, documents_only=True
            )

            # Ensure /Documents/Mods exists
            try:
                if not await self.house_arrest.exists("/Documents/Mods"):
                    await self.house_arrest.makedirs("/Documents/Mods")
            except Exception:
                pass

            self.is_connected = True
            self.local_mode_dir = None
            return True, f"Connected to {dev_name} ({model}, iOS {os_ver})"

        except Exception as e:
            self.is_connected = False
            return False, f"Connection failed: {e}"

    def connect_local_folder(self, folder_path: str) -> tuple[bool, str]:
        """Fallback: connect to a local directory (e.g. if mounted by iTunes/3uTools/iMazing)."""
        if not os.path.isdir(folder_path):
            return False, f"Directory does not exist: {folder_path}"

        # If pointing to root of app documents, or directly to Mods folder:
        mods_path = folder_path if os.path.basename(folder_path).lower() == "mods" else os.path.join(folder_path, "Mods")
        os.makedirs(mods_path, exist_ok=True)
        self.local_mode_dir = folder_path
        self.is_connected = True
        return True, f"Connected to local folder: {folder_path}"

    async def disconnect(self):
        """Close AFC session."""
        if self.house_arrest:
            try:
                await self.house_arrest.close()
            except Exception:
                pass
            self.house_arrest = None
        self.is_connected = False

    async def list_mods(self) -> list[ModInfo]:
        """List all installed mods and parse their manifest.json."""
        if not self.is_connected:
            return []

        mods = []

        if self.local_mode_dir:
            # Local filesystem
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            if not os.path.isdir(mods_dir):
                return []
            for entry in os.listdir(mods_dir):
                full_p = os.path.join(mods_dir, entry)
                if not os.path.isdir(full_p):
                    continue
                manifest_p = os.path.join(full_p, "manifest.json")
                mod = self._parse_manifest_local(entry, manifest_p)
                mods.append(mod)
            return sorted(mods, key=lambda m: m.name.lower())

        # AFC Remote
        if not self.house_arrest:
            return []

        try:
            entries = await self.house_arrest.listdir("/Documents/Mods")
            for entry in entries:
                if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                    continue

                mod_path = f"/Documents/Mods/{entry}"
                try:
                    if not await self.house_arrest.isdir(mod_path):
                        continue
                except Exception:
                    continue

                manifest_path = f"{mod_path}/manifest.json"
                mod = await self._parse_manifest_afc(entry, manifest_path)
                mods.append(mod)

            return sorted(mods, key=lambda m: m.name.lower())
        except Exception as e:
            print(f"[Backend] Error listing mods over AFC: {e}")
            return []

    async def _parse_manifest_afc(self, folder_name: str, manifest_path: str) -> ModInfo:
        """Parse manifest.json over AFC."""
        is_enabled = not folder_name.startswith(".disabled_") and not folder_name.startswith(".disabled-") and not folder_name.startswith(".")
        clean_name = folder_name.replace(".disabled_", "").replace(".disabled-", "")

        mod = ModInfo(
            folder_name=folder_name,
            name=clean_name,
            is_enabled=is_enabled
        )

        try:
            if await self.house_arrest.exists(manifest_path):
                raw = await self.house_arrest.get_file_contents(manifest_path)
                raw_str = clean_json(raw.decode("utf-8-sig", errors="ignore"))
                data = json.loads(raw_str)
                mod.name = data.get("Name", clean_name)
                mod.author = data.get("Author", "Unknown")
                mod.version = str(data.get("Version", "1.0.0"))
                mod.description = data.get("Description", "")
                mod.unique_id = data.get("UniqueID", "")
                mod.minimum_api_version = str(data.get("MinimumApiVersion", ""))

                if "ContentPackFor" in data and data["ContentPackFor"]:
                    mod.is_content_pack = True
                    mod.content_pack_for = data["ContentPackFor"].get("UniqueID", "Content Patcher")

                if "EntryDll" in data and data["EntryDll"]:
                    mod.is_code_mod = True

                deps = data.get("Dependencies", [])
                if isinstance(deps, list):
                    mod.dependencies = [
                        d.get("UniqueID", "") for d in deps 
                        if isinstance(d, dict) and "UniqueID" in d and d.get("IsRequired", True)
                    ]

                update_keys = data.get("UpdateKeys", [])
                if isinstance(update_keys, list):
                    mod.update_keys = [str(k) for k in update_keys if isinstance(k, str)]
                    for k in mod.update_keys:
                        m_match = re.search(r"nexus:(\d+)", k, re.IGNORECASE)
                        if m_match:
                            mod.nexus_id = int(m_match.group(1))
                            break

                if not mod.nexus_id and mod.unique_id and mod.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
                    mod.nexus_id = KNOWN_FRAMEWORK_NEXUS_IDS[mod.unique_id.lower()]
        except Exception as e:
            print(f"[Backend] Manifest parse error for {folder_name}: {e}")

        return mod

    def _parse_manifest_local(self, folder_name: str, manifest_path: str) -> ModInfo:
        """Parse manifest.json on local filesystem."""
        is_enabled = not folder_name.startswith(".disabled_") and not folder_name.startswith(".disabled-") and not folder_name.startswith(".")
        clean_name = folder_name.replace(".disabled_", "").replace(".disabled-", "")

        mod = ModInfo(
            folder_name=folder_name,
            name=clean_name,
            is_enabled=is_enabled
        )

        if os.path.isfile(manifest_path):
            try:
                with open(manifest_path, "r", encoding="utf-8-sig", errors="ignore") as f:
                    raw_str = clean_json(f.read())
                    data = json.loads(raw_str)
                    mod.name = data.get("Name", clean_name)
                    mod.author = data.get("Author", "Unknown")
                    mod.version = str(data.get("Version", "1.0.0"))
                    mod.description = data.get("Description", "")
                    mod.unique_id = data.get("UniqueID", "")
                    mod.minimum_api_version = str(data.get("MinimumApiVersion", ""))

                    if "ContentPackFor" in data and data["ContentPackFor"]:
                        mod.is_content_pack = True
                        mod.content_pack_for = data["ContentPackFor"].get("UniqueID", "Content Patcher")

                    if "EntryDll" in data and data["EntryDll"]:
                        mod.is_code_mod = True

                    deps = data.get("Dependencies", [])
                    if isinstance(deps, list):
                        mod.dependencies = [
                            d.get("UniqueID", "") for d in deps 
                            if isinstance(d, dict) and "UniqueID" in d and d.get("IsRequired", True)
                        ]

                    update_keys = data.get("UpdateKeys", [])
                    if isinstance(update_keys, list):
                        mod.update_keys = [str(k) for k in update_keys if isinstance(k, str)]
                        for k in mod.update_keys:
                            m_match = re.search(r"nexus:(\d+)", k, re.IGNORECASE)
                            if m_match:
                                mod.nexus_id = int(m_match.group(1))
                                break

                    if not mod.nexus_id and mod.unique_id and mod.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
                        mod.nexus_id = KNOWN_FRAMEWORK_NEXUS_IDS[mod.unique_id.lower()]
            except Exception as e:
                print(f"[Backend] Local manifest parse error: {e}")

        return mod

    async def toggle_mod(self, mod: ModInfo) -> tuple[bool, str]:
        """Toggle a mod between Enabled and Disabled."""
        if not self.is_connected:
            return False, "Not connected"

        target_enabled = not mod.is_enabled
        clean_name = mod.folder_name.replace(".disabled_", "").replace(".disabled-", "")
        new_folder = clean_name if target_enabled else f".disabled_{clean_name}"

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            src = os.path.join(mods_dir, mod.folder_name)
            dst = os.path.join(mods_dir, new_folder)
            try:
                os.rename(src, dst)
                mod.folder_name = new_folder
                mod.is_enabled = target_enabled
                return True, f"Mod {'enabled' if target_enabled else 'disabled'}"
            except Exception as e:
                return False, f"Rename failed: {e}"

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            src = f"/Documents/Mods/{mod.folder_name}"
            dst = f"/Documents/Mods/{new_folder}"
            await self.house_arrest.rename(src, dst)
            mod.folder_name = new_folder
            mod.is_enabled = target_enabled
            return True, f"Mod {'enabled' if target_enabled else 'disabled'}"
        except Exception as e:
            return False, f"AFC rename failed: {e}"

    async def delete_mod(self, mod: ModInfo) -> tuple[bool, str]:
        """Permanently delete a mod."""
        if not self.is_connected:
            return False, "Not connected"

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            target = os.path.join(mods_dir, mod.folder_name)
            try:
                shutil.rmtree(target)
                return True, f"Deleted {mod.name}"
            except Exception as e:
                return False, f"Delete failed: {e}"

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            target = f"/Documents/Mods/{mod.folder_name}"
            await self.house_arrest.rm(target)
            return True, f"Deleted {mod.name}"
        except Exception as e:
            return False, f"AFC delete failed: {e}"

    async def delete_all_mods(self) -> tuple[bool, str]:
        """Permanently delete all mods while keeping SMAPI config intact."""
        if not self.is_connected:
            return False, "Not connected"

        deleted_count = 0
        errors = []

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            if os.path.isdir(mods_dir):
                for entry in os.listdir(mods_dir):
                    if entry.lower() in ["smapi-config.json", ".ds_store", "desktop.ini"]:
                        continue
                    p = os.path.join(mods_dir, entry)
                    try:
                        if os.path.isdir(p):
                            shutil.rmtree(p)
                        else:
                            os.remove(p)
                        deleted_count += 1
                    except Exception as e:
                        errors.append(f"{entry}: {e}")
            if errors:
                return False, f"Deleted {deleted_count} mod(s), but {len(errors)} error(s) occurred."
            return True, f"Successfully deleted all {deleted_count} mod(s)."

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            entries = await self.house_arrest.listdir("/Documents/Mods")
            for entry in entries:
                if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                    continue
                p = f"/Documents/Mods/{entry}"
                try:
                    await self.house_arrest.rm(p)
                    deleted_count += 1
                except Exception as e:
                    errors.append(f"{entry}: {e}")

            if errors:
                return False, f"Deleted {deleted_count} mod(s), but {len(errors)} error(s) occurred."
            return True, f"Successfully deleted all {deleted_count} mod(s)."
        except Exception as e:
            return False, f"AFC delete all failed: {e}"


    async def install_mod_archive(self, archive_path: str, progress_callback: Optional[Callable[[str], None]] = None) -> tuple[bool, str]:
        """Extract a .zip or folder and push valid mod folders to the device."""
        if not self.is_connected:
            return False, "Not connected"

        if progress_callback:
            progress_callback("Extracting archive...")

        temp_dir = tempfile.mkdtemp(prefix="sdv_mod_")
        try:
            # 1. Extract archive
            if zipfile.is_zipfile(archive_path):
                with zipfile.ZipFile(archive_path, "r") as z:
                    z.extractall(temp_dir)
            elif os.path.isdir(archive_path):
                shutil.copytree(archive_path, os.path.join(temp_dir, os.path.basename(archive_path)))
            else:
                # Attempt extraction via system bsdtar (built-in Windows tar.exe handles .7z, .rar, .tar, .zip, etc.)
                extracted = False
                try:
                    subprocess.check_call(["tar", "-xf", archive_path, "-C", temp_dir], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    extracted = True
                except Exception:
                    pass

                # If tar failed or not available, check 7z / WinRAR
                if not extracted:
                    for archiver in ["7z", "7za", "winrar"]:
                        archiver_path = shutil.which(archiver)
                        if archiver_path:
                            try:
                                subprocess.check_call([archiver_path, "x", "-y", f"-o{temp_dir}", archive_path], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                                extracted = True
                                break
                            except Exception:
                                pass

                if not extracted:
                    return False, f"Unsupported file format ({os.path.splitext(archive_path)[1]}). Please choose a .zip, .7z, .rar, or folder."

            # 2. Discover all mod roots (directories containing manifest.json)
            mod_dirs = []
            for root, dirs, files in os.walk(temp_dir):
                if "manifest.json" in files:
                    mod_dirs.append(root)

            if not mod_dirs:
                # 2a. Check if this is a config pack (archive containing config.json files for existing mods)
                config_files = []
                for root, dirs, files in os.walk(temp_dir):
                    if "config.json" in files:
                        config_files.append((root, os.path.join(root, "config.json")))

                if config_files:
                    if progress_callback:
                        progress_callback(f"Deploying {len(config_files)} mod configuration(s)...")

                    applied_count = 0
                    existing_mods = set()
                    if self.local_mode_dir:
                        mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
                        if os.path.exists(mods_dir):
                            existing_mods = set(os.listdir(mods_dir))
                    elif self.house_arrest:
                        try:
                            existing_mods = set(await self.house_arrest.listdir("/Documents/Mods"))
                        except Exception:
                            pass

                    for cfg_dir, cfg_path in config_files:
                        cfg_folder = os.path.basename(cfg_dir)
                        # Match folder name against installed mod folders
                        matched = next((m for m in existing_mods if m.lower() == cfg_folder.lower()), None)
                        if not matched:
                            parent_folder = os.path.basename(os.path.dirname(cfg_dir))
                            matched = next((m for m in existing_mods if m.lower() == parent_folder.lower()), None)

                        if matched:
                            if self.local_mode_dir:
                                dest_cfg = os.path.join(mods_dir, matched, "config.json")
                                shutil.copy2(cfg_path, dest_cfg)
                            elif self.house_arrest:
                                dest_cfg = f"/Documents/Mods/{matched}/config.json"
                                with open(cfg_path, "rb") as f_cfg:
                                    await self.house_arrest.set_file_contents(dest_cfg, f_cfg.read())
                            applied_count += 1

                    if applied_count > 0:
                        return True, f"Config pack applied to {applied_count} mod(s)."

                # 2b. Check if this is the SMAPI installer
                for root, dirs, files in os.walk(temp_dir):
                    if any("StardewModdingAPI" in f for f in files) and any("install" in f.lower() for f in files):
                        return True, "SMAPI installer detected (SMAPI is already built-in on SDViOS)."

                return False, "No valid Stardew Valley mod (manifest.json) or mod configurations found in archive."

            installed_names = []

            for mod_dir in mod_dirs:
                # Read name from manifest
                m_path = os.path.join(mod_dir, "manifest.json")
                mod_name = os.path.basename(mod_dir)
                try:
                    with open(m_path, "r", encoding="utf-8-sig", errors="ignore") as f:
                        m_data = json.load(f)
                        mod_name = m_data.get("Name", mod_name)
                except Exception:
                    pass

                # Sanitize folder name
                folder_name = os.path.basename(mod_dir)
                if folder_name.startswith("sdv_mod_") or folder_name == os.path.basename(temp_dir):
                    folder_name = "".join(c for c in mod_name if c.isalnum() or c in (" ", "_", "-", "[", "]")).strip()
                    if not folder_name:
                        folder_name = "InstalledMod"

                if progress_callback:
                    progress_callback(f"Installing {mod_name}...")

                if self.local_mode_dir:
                    mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
                    dst = os.path.join(mods_dir, folder_name)
                    if os.path.exists(dst):
                        shutil.rmtree(dst)
                    shutil.copytree(mod_dir, dst)
                else:
                    remote_target = f"/Documents/Mods/{folder_name}"
                    try:
                        if await self.house_arrest.exists(remote_target):
                            await self.house_arrest.rm(remote_target)
                    except Exception:
                        pass
                    # Push directory
                    await self.house_arrest.push(mod_dir, "/Documents/Mods", progress_bar=False)

                installed_names.append(mod_name)

            return True, f"Successfully installed: {', '.join(installed_names)}"

        except Exception as e:
            return False, f"Installation failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def get_smapi_log(self) -> str:
        """Fetch latest SMAPI log."""
        if not self.is_connected:
            return "Not connected."

        if self.local_mode_dir:
            base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
            for cand in [
                os.path.join(base, ".config", "StardewValley", "ErrorLogs", "SMAPI-latest.txt"),
                os.path.join(base, "ErrorLogs", "SMAPI-latest.txt"),
                os.path.join(base, "ErrorLogs", "engine-latest.log"),
            ]:
                if os.path.isfile(cand):
                    with open(cand, "r", encoding="utf-8", errors="ignore") as f:
                        return f.read()
            return f"No log found on device."

        if not self.house_arrest:
            return "AFC not available."

        try:
            for candidate in [
                "/Documents/.config/StardewValley/ErrorLogs/SMAPI-latest.txt",
                "/Documents/ErrorLogs/SMAPI-latest.txt",
                "/Documents/ErrorLogs/engine-latest.log",
            ]:
                if await self.house_arrest.exists(candidate):
                    raw = await self.house_arrest.get_file_contents(candidate)
                    return raw.decode("utf-8", errors="ignore")
            return "No SMAPI log found on device in /Documents/ErrorLogs/ or /Documents/.config/StardewValley/ErrorLogs/."
        except Exception as e:
            return f"Error retrieving SMAPI log: {e}"

    async def list_saves(self) -> list[str]:
        """List all save game folder names across all known iOS Stardew locations."""
        if not self.is_connected:
            return []

        self._save_locations = {}

        if self.local_mode_dir:
            base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
            search_paths = [
                os.path.join(base, ".config", "StardewValley", "Saves"),
                os.path.join(base, "Saves"),
                os.path.join(base, "StardewValley", "Saves"),
                os.path.join(base, "StardewValley"),
            ]
            for folder in search_paths:
                if os.path.isdir(folder):
                    try:
                        for entry in os.listdir(folder):
                            p = os.path.join(folder, entry)
                            if os.path.isdir(p) and entry not in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", ".smapi", ".Trash"]:
                                if os.path.exists(os.path.join(p, "SaveGameInfo")) or os.path.exists(os.path.join(p, entry)) or "_" in entry:
                                    if entry not in self._save_locations:
                                        self._save_locations[entry] = p
                    except Exception:
                        pass

            try:
                for entry in os.listdir(base):
                    p = os.path.join(base, entry)
                    if os.path.isdir(p) and entry not in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", "Saves", "StardewValley", ".config", ".Trash"]:
                        if os.path.exists(os.path.join(p, "SaveGameInfo")):
                            if entry not in self._save_locations:
                                self._save_locations[entry] = p
            except Exception:
                pass

            return sorted(list(self._save_locations.keys()))

        if not self.house_arrest:
            return []

        try:
            search_paths = [
                "/Documents/.config/StardewValley/Saves",
                "/Documents/Saves",
                "/Documents/StardewValley/Saves",
                "/Documents/StardewValley",
            ]
            for folder in search_paths:
                try:
                    if await self.house_arrest.exists(folder):
                        entries = await self.house_arrest.listdir(folder)
                        for entry in entries:
                            if entry in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", ".smapi", ".Trash"]:
                                continue
                            full_p = f"{folder}/{entry}"
                            try:
                                if await self.house_arrest.isdir(full_p):
                                    sub_entries = await self.house_arrest.listdir(full_p)
                                    if "SaveGameInfo" in sub_entries or entry in sub_entries or "_" in entry:
                                        if entry not in self._save_locations:
                                            self._save_locations[entry] = full_p
                            except Exception:
                                pass
                except Exception:
                    pass

            # Also check top-level /Documents for any folder containing SaveGameInfo
            try:
                doc_entries = await self.house_arrest.listdir("/Documents")
                for entry in doc_entries:
                    if entry in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", "Saves", "StardewValley", ".config", ".Trash", "smapi-internal"]:
                        continue
                    full_p = f"/Documents/{entry}"
                    try:
                        if await self.house_arrest.isdir(full_p):
                            sub_entries = await self.house_arrest.listdir(full_p)
                            if "SaveGameInfo" in sub_entries:
                                if entry not in self._save_locations:
                                    self._save_locations[entry] = full_p
                    except Exception:
                        pass
            except Exception:
                pass

            return sorted(list(self._save_locations.keys()))
        except Exception as e:
            print(f"[Backend] Error listing saves: {e}")
            return []

    async def backup_save(self, save_name: str, local_dest_dir: str) -> tuple[bool, str]:
        """Download a save folder and zip it."""
        if not self.is_connected:
            return False, "Not connected"

        out_zip = os.path.join(local_dest_dir, f"{save_name}_backup.zip")
        temp_dir = tempfile.mkdtemp(prefix="sdv_save_")

        try:
            if self.local_mode_dir:
                src = self._save_locations.get(save_name)
                if not src or not os.path.exists(src):
                    base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
                    for cand in [
                        os.path.join(base, ".config", "StardewValley", "Saves", save_name),
                        os.path.join(base, "Saves", save_name),
                        os.path.join(base, "StardewValley", "Saves", save_name),
                        os.path.join(base, save_name),
                    ]:
                        if os.path.exists(cand):
                            src = cand
                            break
                if not src or not os.path.exists(src):
                    return False, f"Save directory '{save_name}' not found."

                shutil.copytree(src, os.path.join(temp_dir, save_name))
            else:
                remote_src = self._save_locations.get(save_name)
                if not remote_src:
                    for cand in [
                        f"/Documents/.config/StardewValley/Saves/{save_name}",
                        f"/Documents/Saves/{save_name}",
                        f"/Documents/StardewValley/Saves/{save_name}",
                        f"/Documents/{save_name}",
                    ]:
                        if await self.house_arrest.exists(cand):
                            remote_src = cand
                            break
                if not remote_src:
                    return False, f"Save directory '{save_name}' not found on device."

                await self.house_arrest.pull(remote_src, temp_dir, progress_bar=False)

            # Zip contents
            with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as z:
                for root, dirs, files in os.walk(temp_dir):
                    for file in files:
                        p = os.path.join(root, file)
                        arc = os.path.relpath(p, temp_dir)
                        z.write(p, arc)

            return True, f"Saved backup to: {out_zip}"
        except Exception as e:
            return False, f"Backup failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def import_save(self, source_path: str) -> tuple[bool, str]:
        """Import a save folder or .zip file into the game saves directory."""
        if not self.is_connected:
            return False, "Not connected"

        temp_dir = tempfile.mkdtemp(prefix="sdv_import_save_")
        try:
            extracted_folder = None
            if os.path.isfile(source_path) and (source_path.lower().endswith(".zip") or zipfile.is_zipfile(source_path)):
                with zipfile.ZipFile(source_path, "r") as z:
                    z.extractall(temp_dir)
                for root, dirs, files in os.walk(temp_dir):
                    if "SaveGameInfo" in files:
                        extracted_folder = root
                        break
            elif os.path.isdir(source_path):
                if os.path.exists(os.path.join(source_path, "SaveGameInfo")):
                    extracted_folder = source_path
                else:
                    for entry in os.listdir(source_path):
                        sub = os.path.join(source_path, entry)
                        if os.path.isdir(sub) and os.path.exists(os.path.join(sub, "SaveGameInfo")):
                            extracted_folder = sub
                            break

            if not extracted_folder or not os.path.exists(extracted_folder):
                return False, "Could not find a valid Stardew Valley save (must contain 'SaveGameInfo')."

            save_name = os.path.basename(extracted_folder)

            if self.local_mode_dir:
                base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
                dest_dir = os.path.join(base, ".config", "StardewValley", "Saves", save_name)
                os.makedirs(os.path.dirname(dest_dir), exist_ok=True)
                if os.path.exists(dest_dir):
                    shutil.rmtree(dest_dir)
                shutil.copytree(extracted_folder, dest_dir)

                # Also sync to /Saves/ if it exists
                alt_dir = os.path.join(base, "Saves", save_name)
                if os.path.exists(os.path.dirname(alt_dir)):
                    if os.path.exists(alt_dir):
                        shutil.rmtree(alt_dir)
                    shutil.copytree(extracted_folder, alt_dir)
            else:
                # iOS device over AFC
                dest_config = f"/Documents/.config/StardewValley/Saves"
                if not await self.house_arrest.exists(dest_config):
                    await self.house_arrest.makedirs(dest_config)
                remote_save_cfg = f"{dest_config}/{save_name}"
                if await self.house_arrest.exists(remote_save_cfg):
                    await self.house_arrest.rm(remote_save_cfg)
                await self.house_arrest.push(extracted_folder, dest_config, progress_bar=False)

                # Also sync to /Documents/Saves
                try:
                    if await self.house_arrest.exists("/Documents/Saves"):
                        remote_alt = f"/Documents/Saves/{save_name}"
                        if await self.house_arrest.exists(remote_alt):
                            await self.house_arrest.rm(remote_alt)
                        await self.house_arrest.push(extracted_folder, "/Documents/Saves", progress_bar=False)
                except Exception:
                    pass

            return True, f"Successfully imported save '{save_name}'."
        except Exception as e:
            return False, f"Import failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def get_system_update_status(self) -> dict:
        """Check Stardew Valley game version, SDViOS port version, and SMAPI version against latest upstream releases."""
        local_game_version = "1.6.15"
        local_port_version = "v3.0.0"
        local_smapi_version = "4.5.2"

        if self.is_connected and not self.local_mode_dir and self.lockdown:
            try:
                async with InstallationProxyService(self.lockdown) as ip:
                    apps = await ip.get_apps()
                    for bid, meta in apps.items():
                        if "sdvios" in bid.lower() or "stardew" in bid.lower():
                            local_game_version = meta.get("CFBundleShortVersionString", local_game_version)
                            break
            except Exception:
                pass

        # 1. Check latest SDViOS GitHub release tag
        latest_port_version = "v3.0.0"
        port_release_url = "https://github.com/DeadAKJ/SDViosport/releases"
        try:
            req = urllib.request.Request("https://api.github.com/repos/DeadAKJ/SDViosport/tags", headers={"User-Agent": "SDViOS-ModManager"})
            with urllib.request.urlopen(req, timeout=8) as resp:
                tags = json.loads(resp.read().decode())
                if tags and isinstance(tags, list):
                    latest_port_version = tags[0].get("name", latest_port_version)
        except Exception as e:
            print(f"[Backend] Port update check: {e}")

        # 2. Check latest SMAPI release on GitHub
        latest_smapi_version = "4.5.2"
        smapi_release_url = "https://github.com/Pathoschild/SMAPI/releases"
        try:
            req = urllib.request.Request("https://api.github.com/repos/Pathoschild/SMAPI/releases/latest", headers={"User-Agent": "SDViOS-ModManager"})
            with urllib.request.urlopen(req, timeout=8) as resp:
                det = json.loads(resp.read().decode())
                latest_smapi_version = det.get("tag_name", latest_smapi_version).lstrip("v")
        except Exception as e:
            print(f"[Backend] SMAPI update check: {e}")

        # 3. Check official Stardew Valley version (1.6.15 is latest official 1.6 branch)
        latest_game_version = "1.6.15"
        game_wiki_url = "https://stardewvalleywiki.com/Version_History"

        return {
            "game": {
                "name": "Stardew Valley (iOS)",
                "installed_version": local_game_version,
                "latest_version": latest_game_version,
                "has_update": is_version_newer(latest_game_version, local_game_version),
                "url": game_wiki_url,
            },
            "port": {
                "name": "SDViOS Port Engine",
                "installed_version": local_port_version,
                "latest_version": latest_port_version,
                "has_update": is_version_newer(latest_port_version, local_port_version),
                "url": port_release_url,
            },
            "smapi": {
                "name": "SMAPI for iOS",
                "installed_version": local_smapi_version,
                "latest_version": latest_smapi_version,
                "has_update": is_version_newer(latest_smapi_version, local_smapi_version),
                "url": smapi_release_url,
            }
        }

    async def check_all_mod_updates(self, mods: list[ModInfo], nexus_api: Optional[Any] = None) -> list[dict]:
        """Check online repositories (Nexus Mods, GitHub) for updates to installed mods."""
        results = []

        for m in mods:
            mod_id = m.nexus_id
            github_repo = None

            for uk in m.update_keys:
                uk_lower = uk.lower().strip()
                if uk_lower.startswith("nexus:"):
                    part = uk[6:].split("@")[0].strip()
                    if part.isdigit():
                        mod_id = int(part)
                elif uk_lower.startswith("github:"):
                    github_repo = uk[7:].strip()

            if not mod_id and m.unique_id:
                uid_lower = m.unique_id.lower()
                if uid_lower in KNOWN_FRAMEWORK_NEXUS_IDS:
                    mod_id = KNOWN_FRAMEWORK_NEXUS_IDS[uid_lower]
                elif uid_lower.startswith("flashshifter.stardewvalleyexpanded"):
                    mod_id = 3753

            latest_v = None
            url = ""
            source = "Unknown"

            if mod_id and nexus_api and getattr(nexus_api, "api_key", None):
                try:
                    det = nexus_api.get_mod_details("stardewvalley", mod_id)
                    latest_v = det.get("version")
                    url = f"https://www.nexusmods.com/stardewvalley/mods/{mod_id}"
                    source = "Nexus Mods"
                except Exception:
                    pass

            if not latest_v and github_repo:
                try:
                    req = urllib.request.Request(
                        f"https://api.github.com/repos/{github_repo}/releases/latest",
                        headers={"User-Agent": "SDViOS-ModManager"}
                    )
                    with urllib.request.urlopen(req, timeout=6) as resp:
                        det = json.loads(resp.read().decode())
                        latest_v = det.get("tag_name", "").lstrip("v")
                        url = f"https://github.com/{github_repo}/releases"
                        source = "GitHub"
                except Exception:
                    pass

            has_update = is_version_newer(latest_v, m.version) if latest_v else False

            results.append({
                "mod": m,
                "name": m.name,
                "unique_id": m.unique_id,
                "folder_name": m.folder_name,
                "installed_version": m.version,
                "latest_version": latest_v or m.version,
                "has_update": has_update,
                "url": url,
                "nexus_id": mod_id,
                "github_repo": github_repo,
                "source": source
            })

        return results

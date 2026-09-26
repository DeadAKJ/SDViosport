"""
Backend module for Stardew Valley iOS Mod Manager.
Handles Apple USB AFC communication and mod/save/log operations.
"""

import os
import sys
import json
import zipfile
import shutil
import tempfile
import asyncio
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


class IOSModBackend:
    """Manages iOS device connection and mod operations over AFC."""

    def __init__(self):
        self.lockdown = None
        self.house_arrest: Optional[HouseArrestService] = None
        self.device_info: dict = {}
        self.bundle_id: str = ""
        self.is_connected: bool = False
        self.local_mode_dir: Optional[str] = None  # If user selects a mounted PC folder

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
                data = json.loads(raw.decode("utf-8-sig", errors="ignore"))
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
                    mod.dependencies = [d.get("UniqueID", "") for d in deps if isinstance(d, dict) and "UniqueID" in d]
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
                    data = json.load(f)
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
                        mod.dependencies = [d.get("UniqueID", "") for d in deps if isinstance(d, dict) and "UniqueID" in d]
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
                return False, "Unsupported file format. Please choose a .zip or folder."

            # 2. Discover all mod roots (directories containing manifest.json)
            mod_dirs = []
            for root, dirs, files in os.walk(temp_dir):
                if "manifest.json" in files:
                    mod_dirs.append(root)

            if not mod_dirs:
                return False, "No valid Stardew Valley mod (manifest.json) found in archive."

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
            log_p = os.path.join(base, "ErrorLogs", "SMAPI-latest.txt")
            if os.path.isfile(log_p):
                with open(log_p, "r", encoding="utf-8", errors="ignore") as f:
                    return f.read()
            return f"No log found at {log_p}."

        if not self.house_arrest:
            return "AFC not available."

        try:
            for candidate in ["/Documents/ErrorLogs/SMAPI-latest.txt", "/Documents/ErrorLogs/engine-latest.log"]:
                if await self.house_arrest.exists(candidate):
                    raw = await self.house_arrest.get_file_contents(candidate)
                    return raw.decode("utf-8", errors="ignore")
            return "No SMAPI log found on device in /Documents/ErrorLogs/."
        except Exception as e:
            return f"Error retrieving SMAPI log: {e}"

    async def list_saves(self) -> list[str]:
        """List all save game folder names."""
        if not self.is_connected:
            return []

        if self.local_mode_dir:
            base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
            saves_p = os.path.join(base, "Saves")
            if os.path.isdir(saves_p):
                return [d for d in os.listdir(saves_p) if os.path.isdir(os.path.join(saves_p, d))]
            return []

        if not self.house_arrest:
            return []

        try:
            if not await self.house_arrest.exists("/Documents/Saves"):
                return []
            entries = await self.house_arrest.listdir("/Documents/Saves")
            saves = []
            for e in entries:
                if e in [".", "..", ".DS_Store"]:
                    continue
                if await self.house_arrest.isdir(f"/Documents/Saves/{e}"):
                    saves.append(e)
            return sorted(saves)
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
                base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
                src = os.path.join(base, "Saves", save_name)
                shutil.copytree(src, os.path.join(temp_dir, save_name))
            else:
                remote_src = f"/Documents/Saves/{save_name}"
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

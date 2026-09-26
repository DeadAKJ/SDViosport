"""
Nexus Mods API integration and NXM protocol handling for Stardew Valley iOS Mod Manager.
"""

import os
import sys
import re
import json
import urllib.parse
import requests
import winreg
from typing import Optional, Callable



CONFIG_FILE = os.path.join(os.path.dirname(__file__), "config.json")
DEFAULT_DOWNLOAD_DIR = os.path.join(os.path.dirname(__file__), "downloads")


def load_config() -> dict:
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {}


def save_config(cfg: dict):
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
    except Exception as e:
        print(f"[Config] Error saving config: {e}")


class NexusAPI:
    BASE_URL = "https://api.nexusmods.com/v1"
    USER_AGENT = "StardewValley-iOS-ModManager/1.0"

    def __init__(self, api_key: str = ""):
        self.api_key = api_key

    def set_api_key(self, api_key: str):
        self.api_key = api_key.strip()

    def validate_api_key(self) -> tuple[bool, dict]:
        """Validate API key with Nexus Mods API."""
        if not self.api_key:
            return False, {"message": "API key cannot be empty."}

        url = f"{self.BASE_URL}/users/validate.json"
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "accept": "application/json"
        }
        try:
            r = requests.get(url, headers=headers, timeout=10)
            if r.status_code == 200:
                data = r.json()
                return True, data
            else:
                err_msg = r.json().get("message", f"HTTP {r.status_code}")
                return False, {"message": err_msg}
        except Exception as e:
            return False, {"message": str(e)}

    def parse_nxm_url(self, nxm_url: str) -> Optional[dict]:
        """
        Parse nxm://stardewvalley/mods/{mod_id}/files/{file_id}?key={key}&expires={expires}
        """
        if not nxm_url.startswith("nxm://"):
            return None

        try:
            # Replace nxm:// with http:// for standard urlparse
            dummy_url = "http://" + nxm_url[6:]
            parsed = urllib.parse.urlparse(dummy_url)
            game_name = parsed.netloc

            parts = [p for p in parsed.path.split("/") if p]
            # Expecting: ['mods', '{mod_id}', 'files', '{file_id}']
            if len(parts) >= 4 and parts[0] == "mods" and parts[2] == "files":
                mod_id = int(parts[1])
                file_id = int(parts[3])
                params = urllib.parse.parse_qs(parsed.query)
                key = params.get("key", [""])[0]
                expires = params.get("expires", [""])[0]
                user_id = params.get("user_id", [""])[0]

                return {
                    "game": game_name,
                    "mod_id": mod_id,
                    "file_id": file_id,
                    "key": key,
                    "expires": expires,
                    "user_id": user_id
                }
        except Exception as e:
            print(f"[Nexus] Error parsing nxm URL: {e}")

        return None

    def get_mod_details(self, game_name: str, mod_id: int) -> dict:
        """Fetch mod details from Nexus Mods API."""
        if not self.api_key:
            raise ValueError("Nexus API key not configured.")
        url = f"{self.BASE_URL}/games/{game_name}/mods/{mod_id}.json"
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "accept": "application/json"
        }
        r = requests.get(url, headers=headers, timeout=15)
        if r.status_code != 200:
            raise Exception(f"Nexus API error ({r.status_code}): {r.text}")
        return r.json()

    def get_mod_files(self, game_name: str, mod_id: int) -> list[dict]:
        """Fetch list of mod downloadable files from Nexus Mods API."""
        if not self.api_key:
            raise ValueError("Nexus API key not configured.")
        url = f"{self.BASE_URL}/games/{game_name}/mods/{mod_id}/files.json"
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "accept": "application/json"
        }
        r = requests.get(url, headers=headers, timeout=15)
        if r.status_code != 200:
            raise Exception(f"Nexus API error ({r.status_code}): {r.text}")
        data = r.json()
        return data.get("files", [])

    def get_download_links(self, game_name: str, mod_id: int, file_id: int, key: str = "", expires: str = "") -> list[str]:
        """Request direct CDN download links for an NXM link or mod file."""
        if not self.api_key:
            raise ValueError("Nexus API key not configured.")

        url = f"{self.BASE_URL}/games/{game_name}/mods/{mod_id}/files/{file_id}/download_link.json"
        params = {}
        if key:
            params["key"] = key
        if expires:
            params["expires"] = expires

        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "accept": "application/json"
        }

        r = requests.get(url, headers=headers, params=params, timeout=15)
        if r.status_code != 200:
            raise Exception(f"Nexus API error ({r.status_code}): {r.text}")

        data = r.json()
        links = []
        if isinstance(data, list):
            for item in data:
                if "URI" in item:
                    links.append(item["URI"])
        return links

    def download_file(self, uri: str, dest_dir: str, progress_callback: Optional[Callable[[int, int], None]] = None) -> str:
        """Download file from CDN or direct URL to local dest_dir with progress."""
        os.makedirs(dest_dir, exist_ok=True)

        headers = {"User-Agent": self.USER_AGENT}
        with requests.get(uri, headers=headers, stream=True, timeout=30) as r:
            r.raise_for_status()

            # Determine filename
            cd = r.headers.get("content-disposition", "")
            filename = ""
            if "filename=" in cd:
                filename = cd.split("filename=")[-1].strip('";\' ')
            if not filename:
                filename = os.path.basename(urllib.parse.urlparse(uri).path)
            if not filename or "." not in filename:
                filename = "nexus_mod_download.zip"

            total_size = int(r.headers.get("content-length", 0))
            out_path = os.path.join(dest_dir, filename)

            downloaded = 0
            with open(out_path, "wb") as f:
                for chunk in r.iter_content(chunk_size=65536):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if progress_callback:
                            progress_callback(downloaded, total_size)

        return out_path


def parse_any_url(raw: str) -> dict:
    """
    Parse any mod input string:
    - NXM protocol: nxm://stardewvalley/mods/{mod_id}/files/{file_id}?...
    - Nexus web page: https://www.nexusmods.com/stardewvalley/mods/{mod_id}
    - Numeric Mod ID: 1915 or #1915 or mod:1915
    - Direct archive URL: https://.../something.zip
    """
    s = raw.strip()
    if not s:
        return {"type": "empty"}

    # 1. NXM protocol
    if s.startswith("nxm://"):
        api = NexusAPI()
        parsed = api.parse_nxm_url(s)
        if parsed:
            return {"type": "nxm", **parsed}
        return {"type": "invalid", "error": "Invalid nxm:// link format"}

    # 2. Nexus Web URL
    nexus_match = re.search(r"nexusmods\.com/([^/]+)/mods/(\d+)", s, re.IGNORECASE)
    if nexus_match:
        game = nexus_match.group(1).lower()
        mod_id = int(nexus_match.group(2))
        file_id = None
        if "?" in s:
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(s).query)
            if "file_id" in qs:
                try:
                    file_id = int(qs["file_id"][0])
                except ValueError:
                    pass
        return {
            "type": "nexus_web",
            "game": game,
            "mod_id": mod_id,
            "file_id": file_id,
            "url": f"https://www.nexusmods.com/{game}/mods/{mod_id}"
        }

    # 3. Numeric Mod ID
    cleaned = re.sub(r"^(?:mod[:\s#]*|#)", "", s, flags=re.IGNORECASE).strip()
    if cleaned.isdigit():
        mod_id = int(cleaned)
        return {
            "type": "nexus_web",
            "game": "stardewvalley",
            "mod_id": mod_id,
            "file_id": None,
            "url": f"https://www.nexusmods.com/stardewvalley/mods/{mod_id}"
        }

    # 4. Direct HTTP/HTTPS Archive or Web link
    if s.startswith("http://") or s.startswith("https://"):
        return {
            "type": "direct_url",
            "url": s
        }

    return {"type": "invalid", "error": "Unrecognized link format"}



def register_nxm_protocol() -> bool:
    """Register current application as the Windows nxm:// protocol handler."""
    try:
        python_exe = sys.executable
        script_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "main.py"))

        # Command string: python.exe "path\to\main.py" "%1"
        command_str = f'"{python_exe}" "{script_path}" "%1"'

        key_path = r"Software\Classes\nxm"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, key_path) as key:
            winreg.SetValueEx(key, "", 0, winreg.REG_SZ, "URL:Nexus Mod Manager Protocol")
            winreg.SetValueEx(key, "URL Protocol", 0, winreg.REG_SZ, "")

        cmd_path = rf"{key_path}\shell\open\command"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, cmd_path) as cmd_key:
            winreg.SetValueEx(cmd_key, "", 0, winreg.REG_SZ, command_str)

        return True
    except Exception as e:
        print(f"[Nexus] Failed to register nxm protocol: {e}")
        return False


def is_nxm_registered_to_us() -> bool:
    """Check if nxm protocol currently points to our Mod Manager."""
    try:
        key_path = r"Software\Classes\nxm\shell\open\command"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as k:
            val, _ = winreg.QueryValueEx(k, "")
            script_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "main.py")).lower()
            return script_path in val.lower()
    except Exception:
        return False


def get_vortex_stardew_dirs() -> tuple[Optional[str], Optional[str]]:
    """Return (staged_mods_dir, downloads_dir) for Vortex Stardew Valley if available."""
    appdata = os.environ.get("APPDATA", "")
    if not appdata:
        return None, None

    vortex_mods = os.path.join(appdata, "Vortex", "stardewvalley", "mods")
    vortex_downloads = os.path.join(appdata, "Vortex", "downloads", "stardewvalley")

    mods_dir = vortex_mods if os.path.isdir(vortex_mods) else None
    dl_dir = vortex_downloads if os.path.isdir(vortex_downloads) else None
    return mods_dir, dl_dir

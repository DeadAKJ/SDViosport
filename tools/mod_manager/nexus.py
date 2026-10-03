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
    GRAPHQL_URL = "https://api.nexusmods.com/v2/graphql"
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

    def get_collection(self, slug: str, domain_name: str = "stardewvalley") -> dict:
        """Fetch complete collection info, revision, and mod list from Nexus GraphQL."""
        if not self.api_key:
            raise ValueError("Nexus API key not configured.")

        query = """
        query GetCollection($slug: String!, $domainName: String!) {
          collection(slug: $slug, domainName: $domainName) {
            id
            slug
            name
            summary
            user {
              name
            }
            overallRating
            tileImage {
              url
            }
            latestPublishedRevision {
              revisionNumber
              modCount
              totalSize
              modFiles {
                fileId
                optional
                version
                file {
                  fileId
                  modId
                  name
                  sizeInBytes
                  version
                  uri
                  mod {
                    name
                    author
                    summary
                  }
                }
              }
            }
          }
        }
        """
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "Content-Type": "application/json",
            "accept": "application/json"
        }
        variables = {"slug": slug, "domainName": domain_name}
        r = requests.post(self.GRAPHQL_URL, headers=headers, json={"query": query, "variables": variables}, timeout=25)
        if r.status_code != 200:
            raise Exception(f"GraphQL request error ({r.status_code}): {r.text}")

        res = r.json()
        if "errors" in res:
            err_msg = "; ".join(e.get("message", "Error") for e in res["errors"])
            raise Exception(f"Nexus GraphQL error: {err_msg}")

        coll = res.get("data", {}).get("collection")
        if not coll:
            raise Exception(f"Collection '{slug}' not found on Nexus Mods.")

        rev = coll.get("latestPublishedRevision") or {}
        raw_mods = rev.get("modFiles") or []

        mods = []
        for m in raw_mods:
            f = m.get("file") or {}
            mod_data = f.get("mod") or {}
            name = mod_data.get("name") or f.get("name") or f"Mod #{f.get('modId', m.get('fileId'))}"
            author = mod_data.get("author") or "Unknown"
            size_b = f.get("sizeInBytes")
            size_bytes = int(size_b) if size_b is not None else 0

            mods.append({
                "mod_id": f.get("modId") or 0,
                "file_id": m.get("fileId") or f.get("fileId") or 0,
                "name": name,
                "file_name": f.get("name") or name,
                "author": author,
                "version": m.get("version") or f.get("version") or "1.0.0",
                "optional": bool(m.get("optional", False)),
                "size_bytes": size_bytes,
                "summary": mod_data.get("summary") or ""
            })

        total_size_b = rev.get("totalSize")
        total_size = int(total_size_b) if total_size_b is not None else sum(m["size_bytes"] for m in mods)

        return {
            "id": coll.get("id"),
            "slug": coll.get("slug", slug),
            "name": coll.get("name", slug),
            "summary": coll.get("summary", ""),
            "author": coll.get("user", {}).get("name", "Unknown"),
            "rating": coll.get("overallRating", ""),
            "revision": rev.get("revisionNumber", 1),
            "mod_count": len(mods),
            "total_size": total_size,
            "tile_image": coll.get("tileImage", {}).get("url", "") if coll.get("tileImage") else "",
            "mods": mods
        }

    def search_collections(self, count: int = 15, domain_name: str = "stardewvalley") -> list[dict]:
        """Fetch popular collections for Stardew Valley from Nexus GraphQL."""
        if not self.api_key:
            return []

        query = """
        query SearchCollections($count: Int!) {
          collectionsV2(
            filter: {
              gameId: { value: "1303", op: EQUALS }
            }
            count: $count
          ) {
            nodes {
              slug
              name
              summary
              overallRating
              user {
                name
              }
              latestPublishedRevision {
                revisionNumber
                modCount
                totalSize
              }
            }
          }
        }
        """
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "Content-Type": "application/json",
            "accept": "application/json"
        }
        try:
            r = requests.post(self.GRAPHQL_URL, headers=headers, json={"query": query, "variables": {"count": count}}, timeout=15)
            if r.status_code == 200:
                nodes = r.json().get("data", {}).get("collectionsV2", {}).get("nodes", [])
                out = []
                for n in nodes:
                    rev = n.get("latestPublishedRevision") or {}
                    total_sz = int(rev.get("totalSize") or 0)
                    out.append({
                        "slug": n.get("slug"),
                        "name": n.get("name"),
                        "summary": n.get("summary", ""),
                        "author": n.get("user", {}).get("name", "Unknown"),
                        "rating": n.get("overallRating", ""),
                        "revision": rev.get("revisionNumber", 1),
                        "mod_count": rev.get("modCount", 0),
                        "total_size": total_sz
                    })
        except Exception as e:
            print(f"[Nexus] Error searching collections: {e}")
        return []

    def get_mod_requirements(self, mod_id: int) -> list[dict]:
        """Fetch prerequisites/requirements for a mod using GraphQL."""
        if not self.api_key:
            return []

        query = """
        query GetModReqs($modId: ID!) {
          mod(modId: $modId, gameId: 1303) {
            name
            modRequirements {
              nexusRequirements {
                nodes {
                  modId
                  modName
                  notes
                }
              }
            }
          }
        }
        """
        headers = {
            "apikey": self.api_key,
            "User-Agent": self.USER_AGENT,
            "Content-Type": "application/json",
            "accept": "application/json"
        }
        try:
            r = requests.post(self.GRAPHQL_URL, headers=headers, json={"query": query, "variables": {"modId": str(mod_id)}}, timeout=15)
            if r.status_code == 200:
                data = r.json().get("data", {}).get("mod", {}) or {}
                req_nodes = data.get("modRequirements", {}).get("nexusRequirements", {}).get("nodes", []) or []
                out = []
                for n in req_nodes:
                    m_id_str = str(n.get("modId", "")).strip()
                    if not m_id_str.isdigit():
                        continue
                    m_id = int(m_id_str)
                    if m_id == 2400:  # Skip SMAPI (built-in on SDViOS)
                        continue
                    out.append({
                        "mod_id": m_id,
                        "name": n.get("modName", f"Mod #{m_id}"),
                        "notes": n.get("notes", "")
                    })
                return out
        except Exception as e:
            print(f"[Nexus] Error fetching requirements for mod {mod_id}: {e}")
        return []

    def get_primary_mod_file(self, game_name: str, mod_id: int) -> Optional[dict]:
        """Fetch the primary/latest downloadable file for a mod."""
        try:
            files = self.get_mod_files(game_name, mod_id)
            if not files:
                return None
            # Filter MAIN files first
            main_files = [f for f in files if f.get("category_name") == "MAIN"]
            target_list = main_files if main_files else files
            # Sort newest first
            target_list.sort(key=lambda x: x.get("uploaded_timestamp", 0), reverse=True)
            return target_list[0]
        except Exception as e:
            print(f"[Nexus] Error fetching primary file for mod {mod_id}: {e}")
            return None


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
    Parse any mod or collection input string:
    - NXM protocol: nxm://stardewvalley/mods/{mod_id}/files/{file_id}?...
    - Nexus collection URL: https://next.nexusmods.com/stardewvalley/collections/{slug}
    - Nexus web page: https://www.nexusmods.com/stardewvalley/mods/{mod_id}
    - Numeric Mod ID: 1915 or #1915 or mod:1915
    - Collection slug: col:htknoa or collection:htknoa
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

    # 2. Nexus Collection URL or Prefix
    coll_match = re.search(r"nexusmods\.com/(?:next/)?(?:stardewvalley/)?collections/([a-zA-Z0-9_-]+)", s, re.IGNORECASE)
    if not coll_match and (s.lower().startswith("col:") or s.lower().startswith("collection:")):
        slug = re.sub(r"^(?:col:|collection:)", "", s, flags=re.IGNORECASE).strip()
        if slug:
            return {
                "type": "nexus_collection",
                "game": "stardewvalley",
                "slug": slug,
                "url": f"https://next.nexusmods.com/stardewvalley/collections/{slug}"
            }

    if coll_match:
        slug = coll_match.group(1)
        return {
            "type": "nexus_collection",
            "game": "stardewvalley",
            "slug": slug,
            "url": f"https://next.nexusmods.com/stardewvalley/collections/{slug}"
        }

    # 3. Nexus Web URL
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

    # 4. Numeric Mod ID
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

    # 5. Direct HTTP/HTTPS Archive or Web link
    if s.startswith("http://") or s.startswith("https://"):
        return {
            "type": "direct_url",
            "url": s
        }

    return {"type": "invalid", "error": "Unrecognized link format"}




def register_nxm_protocol() -> bool:
    """Register current application as the Windows nxm:// protocol handler."""
    try:
        # Prefer pythonw.exe so no console / powershell window appears
        python_exe = sys.executable
        pythonw_candidate = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
        if os.path.isfile(pythonw_candidate):
            python_exe = pythonw_candidate

        script_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "main.py"))

        # Command string: pythonw.exe "path\to\main.py" "%1"
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


def find_local_vortex_mod(mod_id: int, file_id: Optional[int] = None, mod_name: Optional[str] = None) -> tuple[Optional[str], bool]:
    """
    Check if a mod (by mod_id, file_id, or mod_name) is already in local Vortex downloads or staged mods.
    Returns (path, is_dir) or (None, False).
    """
    mods_dir, dl_dir = get_vortex_stardew_dirs()

    # Normalize mod_name keywords if provided
    name_tokens = []
    if mod_name:
        clean = "".join(c if c.isalnum() or c in (" ", "_", "-") else " " for c in mod_name).lower()
        name_tokens = [t for t in clean.split() if len(t) >= 4 and t not in ("mod", "stardew", "valley", "custom", "continued", "redux", "revisited")]

    # 1. Check downloaded archives (.zip, .rar, .7z)
    if dl_dir and os.path.isdir(dl_dir):
        try:
            for f in os.listdir(dl_dir):
                if not f.endswith((".zip", ".rar", ".7z")) or f.startswith("__vortex"):
                    continue
                if mod_id and (f"-{mod_id}-" in f or f.endswith(f"-{mod_id}.zip")):
                    return os.path.join(dl_dir, f), False
                if file_id and str(file_id) in f:
                    return os.path.join(dl_dir, f), False
                # Fallback: check matching tokens in filename
                if name_tokens and len(name_tokens) >= 2:
                    f_lower = f.lower()
                    if all(t in f_lower for t in name_tokens[:3]):
                        return os.path.join(dl_dir, f), False
        except Exception:
            pass

    # 2. Check staged mods folder
    if mods_dir and os.path.isdir(mods_dir):
        try:
            for d in os.listdir(mods_dir):
                full_p = os.path.join(mods_dir, d)
                if not os.path.isdir(full_p):
                    continue
                if mod_id and f"-{mod_id}-" in d:
                    return full_p, True
                if name_tokens and len(name_tokens) >= 2:
                    d_lower = d.lower()
                    if all(t in d_lower for t in name_tokens[:3]):
                        return full_p, True
        except Exception:
            pass

    return None, False


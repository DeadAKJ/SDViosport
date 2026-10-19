import argparse
import asyncio
import os
import urllib.parse
import urllib.request

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
MOD_BIN_DIR = os.path.join(REPO_ROOT, 'src', 'Mods', 'SDViOSTouchControls', 'bin', 'Release', 'net6.0')
MOD_SRC_DIR = os.path.join(REPO_ROOT, 'src', 'Mods', 'SDViOSTouchControls')

TOUCH_DLL = os.path.join(MOD_BIN_DIR, 'SDViOSTouchControls.dll')
TOUCH_PDB = os.path.join(MOD_BIN_DIR, 'SDViOSTouchControls.pdb')
MANIFEST_JSON = os.path.join(MOD_SRC_DIR, 'manifest.json')


def deploy_via_wifi(ip='192.168.1.123', port=8080):
    base_url = f'http://{ip}:{port}'
    files_to_push = [
        (TOUCH_DLL, 'Mods/SDViOSTouchControls/SDViOSTouchControls.dll'),
        (TOUCH_DLL, 'Mods/SDViOSTouchControls/SDViOSTouchControlsMod.dll'),
        (MANIFEST_JSON, 'Mods/SDViOSTouchControls/manifest.json'),
    ]
    if os.path.exists(TOUCH_PDB):
        files_to_push.append((TOUCH_PDB, 'Mods/SDViOSTouchControls/SDViOSTouchControls.pdb'))

    for local_path, remote_path in files_to_push:
        if not os.path.exists(local_path):
            print(f'Missing build output: {local_path}')
            return False
        with open(local_path, 'rb') as f:
            data = f.read()
        url = f"{base_url}/upload?path={urllib.parse.quote(remote_path)}"
        req = urllib.request.Request(url, data=data, method='POST')
        req.add_header('Content-Type', 'application/octet-stream')
        with urllib.request.urlopen(req, timeout=15) as resp:
            print(f'[Wi-Fi] Uploaded {remote_path} ({len(data):,} bytes) -> {resp.read().decode("utf-8")}')
    return True


async def deploy_via_usb():
    from pymobiledevice3.usbmux import list_devices
    from pymobiledevice3.lockdown import create_using_usbmux
    from pymobiledevice3.services.house_arrest import HouseArrestService
    from pymobiledevice3.services.installation_proxy import InstallationProxyService

    devices = await list_devices()
    if not devices:
        print('No USB iOS devices found!')
        return False

    print(f'Connecting to USB device: {devices[0].serial}...')
    lockdown = await create_using_usbmux(serial=devices[0].serial)

    bundle_id = 'com.deadakj.sdvios.KRS7366K7J'
    try:
        inst = InstallationProxyService(lockdown=lockdown)
        apps = await inst.get_apps(application_type='User')
        for bid in apps:
            if bid.startswith('com.deadakj.sdvios'):
                bundle_id = bid
                break
    except Exception:
        pass

    ha = await HouseArrestService.create(lockdown, bundle_id, documents_only=True)
    remote_dir = '/Documents/Mods/SDViOSTouchControls'
    try:
        await ha.makedirs(remote_dir)
    except Exception:
        pass

    with open(TOUCH_DLL, 'rb') as f:
        dll_data = f.read()

    for dll_name in ('SDViOSTouchControls.dll', 'SDViOSTouchControlsMod.dll'):
        remote_file = f'{remote_dir}/{dll_name}'
        await ha.set_file_contents(remote_file, dll_data)
        print(f'[USB] Uploaded {dll_name} ({len(dll_data):,} bytes) to {remote_file}')

    if os.path.exists(MANIFEST_JSON):
        with open(MANIFEST_JSON, 'rb') as f:
            manifest_data = f.read()
        await ha.set_file_contents(f'{remote_dir}/manifest.json', manifest_data)
        print(f'[USB] Uploaded manifest.json ({len(manifest_data):,} bytes)')

    if os.path.exists(TOUCH_PDB):
        with open(TOUCH_PDB, 'rb') as f:
            pdb_data = f.read()
        await ha.set_file_contents(f'{remote_dir}/SDViOSTouchControls.pdb', pdb_data)
        print(f'[USB] Uploaded SDViOSTouchControls.pdb ({len(pdb_data):,} bytes)')

    return True


def main():
    parser = argparse.ArgumentParser(description='Deploy SDViOSTouchControls over Wi-Fi or USB')
    parser.add_argument('--ip', default='192.168.1.123', help='iPhone Wi-Fi IP address')
    parser.add_argument('--port', type=int, default=8080, help='RemoteDebugServer port')
    parser.add_argument('--usb', action='store_true', help='Force USB deployment via pymobiledevice3')
    args = parser.parse_args()

    if not args.usb:
        try:
            if deploy_via_wifi(args.ip, args.port):
                return
        except Exception as e:
            print(f'Wi-Fi deployment unavailable ({e}), trying USB...')

    asyncio.run(deploy_via_usb())


if __name__ == '__main__':
    main()

import asyncio
import os
from pymobiledevice3.usbmux import list_devices
from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.services.house_arrest import HouseArrestService
from pymobiledevice3.services.installation_proxy import InstallationProxyService

DEFAULT_BUNDLE_ID = 'com.deadakj.sdvios.KRS7366K7J'

LOG_TARGETS = [
    ('SMAPI-latest.txt', ['/Documents/ErrorLogs/SMAPI-latest.txt', '/Documents/SMAPI-latest.txt']),
    ('SMAPI-previous.txt', ['/Documents/ErrorLogs/SMAPI-previous.txt', '/Documents/SMAPI-previous.txt']),
    ('engine-latest.log', ['/Documents/ErrorLogs/engine-latest.log', '/Documents/engine-latest.log']),
    ('engine-previous.log', ['/Documents/ErrorLogs/engine-previous.log', '/Documents/engine-previous.log']),
]


async def main():
    devices = await list_devices()
    if not devices:
        print('No iOS device found on usbmux!')
        return
    print(f'Found device: {devices[0].serial}')
    ld = await create_using_usbmux(serial=devices[0].serial)

    bundle_id = DEFAULT_BUNDLE_ID
    try:
        inst = InstallationProxyService(lockdown=ld)
        apps = await inst.get_apps(application_type='User')
        for bid in apps:
            if bid.startswith('com.deadakj.sdvios'):
                bundle_id = bid
                break
    except Exception:
        pass

    ha = await HouseArrestService.create(ld, bundle_id, documents_only=True)

    pulled_any = False
    for local_name, remote_candidates in LOG_TARGETS:
        for cand in remote_candidates:
            try:
                content = await ha.get_file_contents(cand)
                out_file = os.path.join(os.getcwd(), local_name)
                with open(out_file, 'wb') as fp:
                    fp.write(content)
                print(f'Successfully pulled {cand} ({len(content):,} bytes) -> saved to {out_file}')
                pulled_any = True
                break
            except Exception:
                continue

    if not pulled_any:
        print('Could not locate any SMAPI or Engine logs on device.')


if __name__ == '__main__':
    asyncio.run(main())

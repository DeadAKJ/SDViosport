import asyncio
import os
from pymobiledevice3.usbmux import list_devices
from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.services.house_arrest import HouseArrestService

bundle_id = 'com.deadakj.sdvios.KRS7366K7J'

async def main():
    devices = await list_devices()
    if not devices:
        print('No iOS device found on usbmux!')
        return
    print(f'Found device: {devices[0].serial}')
    ld = await create_using_usbmux(serial=devices[0].serial)
    ha = await HouseArrestService.create(ld, bundle_id, documents_only=True)

    log_candidates = [
        '/Documents/ErrorLogs/SMAPI-latest.txt',
        '/Documents/SMAPI-latest.txt',
        '/Documents/StardewValley/ErrorLogs/SMAPI-latest.txt'
    ]

    for cand in log_candidates:
        try:
            content = await ha.get_file_contents(cand)
            out_file = os.path.join(os.getcwd(), 'SMAPI-latest.txt')
            with open(out_file, 'wb') as fp:
                fp.write(content)
            print(f'Successfully pulled log from {cand} ({len(content)} bytes) -> saved to {out_file}')
            return
        except Exception:
            continue

    print('Could not locate SMAPI-latest.txt in standard paths on device.')

if __name__ == '__main__':
    asyncio.run(main())

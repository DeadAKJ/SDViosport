import asyncio
import os
from pymobiledevice3.usbmux import list_devices
from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.services.house_arrest import HouseArrestService

touch_dll = r'C:\Users\User\Documents\GitHub\SDVios\src\Mods\SDViOSTouchControls\bin\Release\net6.0\SDViOSTouchControls.dll'

async def main():
    devices = await list_devices()
    if not devices:
        print("No iOS devices found!")
        return
    print(f"Connecting to device: {devices[0].serial}...")
    lockdown = await create_using_usbmux(serial=devices[0].serial)
    ha = await HouseArrestService.create(lockdown, 'com.deadakj.sdvios.KRS7366K7J', documents_only=True)
    
    # Check directory
    remote_dir = '/Documents/Mods/SDViOSNativeHotfix'
    try:
        await ha.makedirs(remote_dir)
    except Exception:
        pass

    remote_file = f'{remote_dir}/SDViOSTouchControls.dll'
    with open(touch_dll, 'rb') as f:
        data = f.read()
    
    await ha.set_file_contents(remote_file, data)
    print(f"Uploaded SDViOSTouchControls.dll ({len(data)} bytes) to {remote_file}")
    
    touch_pdb = touch_dll.replace('.dll', '.pdb')
    if os.path.exists(touch_pdb):
        remote_pdb = f'{remote_dir}/SDViOSTouchControls.pdb'
        with open(touch_pdb, 'rb') as f:
            pdb_data = f.read()
        await ha.set_file_contents(remote_pdb, pdb_data)
        print(f"Uploaded SDViOSTouchControls.pdb ({len(pdb_data)} bytes) to {remote_pdb}")

    stat = await ha.stat(remote_file)
    print(f"Verified on iPhone: {remote_file}: {stat['st_size']} bytes")

if __name__ == '__main__':
    asyncio.run(main())

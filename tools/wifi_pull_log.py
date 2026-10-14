import argparse
import os
import sys
import urllib.request
import urllib.parse
import json

DEFAULT_IP = '192.168.1.123'
DEFAULT_PORT = 8080

def get_base_url(ip, port):
    return f'http://{ip}:{port}'

def fetch_log(base_url, output_path='SMAPI-latest.txt'):
    url = f'{base_url}/log'
    print(f'Fetching current SMAPI log from {url}...')
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = resp.read()
            with open(output_path, 'wb') as f:
                f.write(data)
            print(f'Successfully pulled current SMAPI log ({len(data):,} bytes) -> saved to {output_path}')
            return True
    except Exception as e:
        print(f'Failed to fetch current SMAPI log: {e}')
        return False

def fetch_previous_log(base_url, output_path='SMAPI-previous.txt'):
    url = f'{base_url}/log-previous'
    print(f'Fetching previous/crash SMAPI log from {url}...')
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = resp.read()
            with open(output_path, 'wb') as f:
                f.write(data)
            print(f'Successfully pulled previous/crash SMAPI log ({len(data):,} bytes) -> saved to {output_path}')
            return True
    except Exception as e:
        print(f'Failed to fetch previous SMAPI log: {e}')
        return False

def fetch_engine_log(base_url, output_path='engine-latest.log'):
    url = f'{base_url}/engine-log'
    print(f'Fetching Engine log from {url}...')
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = resp.read()
            with open(output_path, 'wb') as f:
                f.write(data)
            print(f'Successfully pulled Engine log ({len(data):,} bytes) -> saved to {output_path}')
            return True
    except Exception as e:
        print(f'Failed to fetch Engine log: {e}')
        return False

def tail_log(base_url, lines=100, previous=False):
    endpoint = 'tail-previous' if previous else 'tail'
    url = f'{base_url}/{endpoint}?lines={lines}'
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            text = resp.read().decode('utf-8', errors='replace')
            label = "Previous/Crash" if previous else "Current"
            print(f'--- Last {lines} lines of {label} SMAPI log ---')
            print(text)
            return True
    except Exception as e:
        print(f'Failed to tail log: {e}')
        return False

def trigger_gc(base_url):
    url = f'{base_url}/gc'
    print(f'Triggering memory compaction & GC at {url}...')
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            data = json.loads(resp.read().decode('utf-8'))
            print('GC Result:', json.dumps(data, indent=2))
            return True
    except Exception as e:
        print(f'Failed to trigger GC: {e}')
        return False

def upload_file(base_url, local_path, remote_path):
    if not os.path.exists(local_path):
        print(f'Error: Local file not found: {local_path}')
        return False

    with open(local_path, 'rb') as f:
        data = f.read()

    url = f"{base_url}/upload?path={urllib.parse.quote(remote_path)}"
    print(f'Uploading {local_path} ({len(data):,} bytes) to {remote_path} via {url}...')
    try:
        req = urllib.request.Request(url, data=data, method='POST')
        req.add_header('Content-Type', 'application/octet-stream')
        with urllib.request.urlopen(req, timeout=30) as resp:
            result = json.loads(resp.read().decode('utf-8'))
            print('Upload Result:', json.dumps(result, indent=2))
            return True
    except Exception as e:
        print(f'Failed to upload file: {e}')
        return False

def main():
    parser = argparse.ArgumentParser(description='SDViOS Wi-Fi Debug Client')
    parser.add_argument('--ip', default=DEFAULT_IP, help=f'iPhone Wi-Fi IP (default: {DEFAULT_IP})')
    parser.add_argument('--port', type=int, default=DEFAULT_PORT, help=f'Server port (default: {DEFAULT_PORT})')
    parser.add_argument('--tail', type=int, nargs='?', const=100, help='Tail last N lines of log')
    parser.add_argument('--previous', action='store_true', help='Download previous/crash log')
    parser.add_argument('--engine', action='store_true', help='Download engine-latest.log')
    parser.add_argument('--gc', action='store_true', help='Force GC & print memory stats')
    parser.add_argument('--upload', nargs=2, metavar=('LOCAL', 'REMOTE'), help='Upload local file to remote Documents path')

    args = parser.parse_args()
    base_url = get_base_url(args.ip, args.port)

    if args.gc:
        trigger_gc(base_url)
    elif args.upload:
        upload_file(base_url, args.upload[0], args.upload[1])
    elif args.tail:
        tail_log(base_url, args.tail, previous=args.previous)
    elif args.previous:
        fetch_previous_log(base_url)
    elif args.engine:
        fetch_engine_log(base_url)
    else:
        # Default action: pull current log, previous log, and engine log
        fetch_log(base_url)
        fetch_previous_log(base_url)
        fetch_engine_log(base_url)

if __name__ == '__main__':
    main()

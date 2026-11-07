"""Balanced, same-binary diagnostic comparisons. Never uses production phone storage."""
import argparse
import hashlib
import json
import pathlib
import statistics
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--platform', choices=['windows', 'android'], required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--database', type=pathlib.Path)
parser.add_argument('--serial')
parser.add_argument('--apk', type=pathlib.Path)
parser.add_argument('--iterations', type=int, default=8)
args = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parents[1]
args.output.mkdir(parents=True, exist_ok=True)
if any(args.output.iterdir()):
    raise RuntimeError('Use a new output directory; refusing to overwrite a comparison')
order = ['optimized', 'full', 'uncached', 'uncached', 'full', 'optimized',
         'full', 'uncached', 'optimized', 'optimized', 'uncached', 'full']
package = 'com.grimlist.perftest'
folder = '/sdcard/Android/data/' + package + '/files/'
adb_path = pathlib.Path('C:/Users/es0p0/AppData/Local/Android/Sdk/platform-tools/adb.exe')

def adb(*command):
    return subprocess.check_output([str(adb_path), '-s', args.serial, *command], text=True, timeout=180).strip()

def device_state():
    return dict(battery=adb('shell', 'dumpsys', 'battery'), thermal=adb('shell', 'dumpsys', 'thermalservice'))

identity = dict(platform=args.platform, order=order, iterations=args.iterations, warmups=3)
if args.platform == 'android':
    if not args.serial or not args.apk:
        parser.error('Android requires --serial and --apk')
    aapt = adb_path.parent.parent/'build-tools/36.0.0/aapt.exe'
    badging = subprocess.check_output([str(aapt), 'dump', 'badging', str(args.apk)], text=True)
    if "package: name='com.grimlist.perftest'" not in badging:
        raise RuntimeError('Refusing a non-diagnostic APK')
    identity['apkSha256'] = hashlib.file_digest(args.apk.open('rb'), 'sha256').hexdigest()
    identity['serial'] = args.serial
    print(adb('install', '--no-streaming', '-r', str(args.apk.resolve())), flush=True)
    installed = adb('shell', 'pm', 'path', package)
    if not installed.startswith('package:/data/app/') or '\n' in installed:
        raise RuntimeError('Unexpected diagnostic APK path')
    installed_hash = adb('shell', 'sha256sum', installed.removeprefix('package:')).split()[0]
    if installed_hash != identity['apkSha256']:
        raise RuntimeError('Installed APK hash mismatch')
    identity['installedPackage'] = adb('shell', 'dumpsys', 'package', package)
    activity = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).splitlines()[-1]
    if not activity.startswith(package+'/'):
        raise RuntimeError('Unexpected diagnostic activity')
else:
    if not args.database:
        parser.error('Windows requires --database')
    binary = repo/'tools/GrimList.Performance/bin/Controlled/GrimList.Performance.dll'
    identity['binarySha256'] = hashlib.file_digest(binary.open('rb'), 'sha256').hexdigest()

(args.output/'identity.json').write_text(json.dumps(identity, indent=2))

def run(label, mode, iterations):
    output = args.output/(label+'.json')
    if args.platform == 'windows':
        with (args.output/(label+'.log')).open('w') as log:
            subprocess.run(['dotnet', str(binary), '--controlled', str(args.database.resolve()), mode,
                            str(output.resolve()), str(iterations)], stdout=log, stderr=subprocess.STDOUT,
                           timeout=1200, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
        result = json.loads(output.read_text(encoding='utf-8-sig'))
    else:
        before = device_state()
        adb('shell', 'am', 'force-stop', package)
        adb('shell', 'rm', '-f', folder+'controlled.json', folder+'performance-error.txt')
        adb('shell', 'am', 'start', '-W', '-n', activity, '--es', 'controlledMode', mode,
            '--ei', 'iterations', str(iterations))
        deadline = time.monotonic()+1200
        while time.monotonic() < deadline:
            error = adb('shell', 'sh', '-c', f"'test ! -f {folder}performance-error.txt || cat {folder}performance-error.txt'")
            if error:
                raise RuntimeError(error)
            value = adb('shell', 'sh', '-c', f"'test ! -f {folder}controlled.json || cat {folder}controlled.json'")
            if value:
                try:
                    result = json.loads(value)
                    break
                except json.JSONDecodeError:
                    pass
            time.sleep(3)
        else:
            raise TimeoutError(label)
        result['DeviceBefore'] = before
        result['DeviceAfter'] = device_state()
        output.write_text(json.dumps(result, indent=2))
        adb('shell', 'am', 'force-stop', package)
    if result['Mode'] != mode or len(result['Samples']) != iterations*6 or len(result['Verification']) != iterations*6:
        raise RuntimeError('Unexpected comparison report')
    print(label, mode, 'equipment median', round(statistics.median(s['UsableMs'] for s in result['Samples'] if s['Operation']=='equipment'), 2), 'ms', flush=True)
    return result

try:
    run('warmup', 'optimized', 1)  # Prepare projections/storage before the measured order.
    reports = []
    for index, mode in enumerate(order):
        time.sleep(2)
        reports.append(run(f'block-{index:02}', mode, args.iterations))
    if len({r['InputHash'] for r in reports}) != 1 or len({r['CoreModule'] for r in reports}) != 1:
        raise RuntimeError('Input or binary changed across modes')
    for operation in {s['Operation'] for r in reports for s in r['Verification']}:
        hashes = {s['StateHash'] for r in reports for s in r['Verification'] if s['Operation']==operation}
        if len(hashes) != 1:
            raise RuntimeError('Mode/sample result mismatch: '+operation)
    (args.output/'complete.json').write_text(json.dumps(dict(blocks=len(reports), samples=sum(len(r['Samples']) for r in reports), exactParity=True)))
    print('All blocks complete; identical inputs and effective/result hashes.', flush=True)
finally:
    if args.platform == 'android':
        adb('shell', 'am', 'force-stop', package)
        main = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', 'com.grimlist.mobile').splitlines()[-1]
        if main.startswith('com.grimlist.mobile/'):
            adb('shell', 'am', 'start', '-n', main)

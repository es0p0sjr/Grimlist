"""Measure only the isolated com.grimlist.perftest package on an authorized device."""
import argparse
import json
import pathlib
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--apk', type=pathlib.Path, required=True)
parser.add_argument('--serial', required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--iterations', type=int, default=20)
parser.add_argument('--starts', type=int, default=5)
parser.add_argument('--skip-install', action='store_true', help='Measure the already installed isolated package')
parser.add_argument('--startup-only', action='store_true', help='Leave the saved fixture context unchanged')
args = parser.parse_args()
sdk = pathlib.Path('C:/Users/es0p0/AppData/Local/Android/Sdk')
package = 'com.grimlist.perftest'
folder = '/sdcard/Android/data/' + package + '/files/'
args.output.mkdir(parents=True, exist_ok=True)

def adb(*command):
    result = subprocess.run([str(sdk / 'platform-tools/adb.exe'), '-s', args.serial, *command],
                            capture_output=True, text=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout

badging = subprocess.check_output([str(sdk / 'build-tools/36.0.0/aapt.exe'), 'dump', 'badging', str(args.apk)], text=True)
assert "package: name='" + package + "'" in badging, 'Refusing to replace production app'
if not args.skip_install:
    print(adb('install', '--no-streaming', '-r', str(args.apk.resolve())), flush=True)
(args.output / 'installed-package.txt').write_text(adb('shell', 'dumpsys', 'package', package))
(args.output / 'battery-before.txt').write_text(adb('shell', 'dumpsys', 'battery'))
activity = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).strip().splitlines()[-1]
starts = []

def wait_file(name, timeout=1200):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        error = adb('shell', 'sh', '-c', f"'test ! -f {folder}performance-error.txt || cat {folder}performance-error.txt'")
        if error.strip():
            raise RuntimeError(error)
        value = adb('shell', 'sh', '-c', f"'test ! -f {folder}{name} || cat {folder}{name}'")
        if value.strip():
            try:
                return json.loads(value)
            except json.JSONDecodeError:
                pass
        time.sleep(0.5)
    raise TimeoutError(name)

for index in range(args.starts):
    adb('shell', 'am', 'force-stop', package)
    adb('shell', 'rm', '-f', folder + 'startup.json', folder + 'performance-error.txt')
    launch = time.monotonic()
    result = adb('shell', 'am', 'start', '-W', '-n', activity, '--ez', 'startupOnly', 'true')
    startup = wait_file('startup.json')
    startup['ExternalReadyMs'] = (time.monotonic() - launch) * 1000
    startup['AmStart'] = result
    starts.append(startup)
    (args.output / f'startup-{index}-memory.txt').write_text(adb('shell', 'dumpsys', 'meminfo', package))
    (args.output / 'startup.json').write_text(json.dumps(starts, indent=2))
    print('Startup', index, startup, flush=True)
if args.startup_only:
    adb('shell', 'am', 'force-stop', package)
    raise SystemExit(0)
adb('shell', 'am', 'force-stop', package)
adb('shell', 'rm', '-f', folder + 'performance.json', folder + 'performance-error.txt')
adb('shell', 'am', 'start', '-W', '-n', activity, '--ei', 'iterations', str(args.iterations))
samples = wait_file('performance.json', timeout=3600)
(args.output / 'samples.json').write_text(json.dumps(samples, indent=2))
(args.output / 'presentation.json').write_text(json.dumps(wait_file('presentation.json'), indent=2))
(args.output / 'after-memory.txt').write_text(adb('shell', 'dumpsys', 'meminfo', package))
(args.output / 'frames.txt').write_text(adb('shell', 'dumpsys', 'gfxinfo', package))
(args.output / 'battery-after.txt').write_text(adb('shell', 'dumpsys', 'battery'))
print('Completed', len(samples), 'operation samples', flush=True)

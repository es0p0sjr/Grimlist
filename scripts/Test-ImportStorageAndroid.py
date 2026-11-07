"""Exercise full-library import/apply/restart only in com.grimlist.perftest.

Build with IncludePerformanceChecks=true and ImportStorageFixtureDirectory set
to a directory containing a pinned repository.zip, its source.json, and mfm.json.
The fixtures are embedded only in that test APK; production storage is untouched.
"""
import argparse
import json
import pathlib
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--apk', type=pathlib.Path, required=True)
parser.add_argument('--serial', required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--sdk', type=pathlib.Path, default=pathlib.Path('C:/Users/es0p0/AppData/Local/Android/Sdk'))
args = parser.parse_args()
package = 'com.grimlist.perftest'
folder = '/sdcard/Android/data/' + package + '/files/'
args.output.mkdir(parents=True, exist_ok=True)


def adb(*command, optional=False):
    result = subprocess.run([str(args.sdk / 'platform-tools/adb.exe'), '-s', args.serial, *command],
                            capture_output=True, timeout=120)
    if result.returncode and not optional:
        raise RuntimeError(result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
    return result.stdout.decode('utf-8', errors='replace')


badging = subprocess.check_output([str(args.sdk / 'build-tools/36.0.0/aapt.exe'), 'dump', 'badging', str(args.apk)], text=True)
if "package: name='" + package + "'" not in badging:
    raise RuntimeError('Refusing to replace the production app')
print(adb('install', '--no-streaming', '-r', str(args.apk.resolve())), flush=True)
activity = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).strip().splitlines()[-1]

for reopen in [False, True]:
    name = 'import-storage-reopen.json' if reopen else 'import-storage.json'
    adb('shell', 'am', 'force-stop', package)
    # Only this check's disposable reports in its isolated app directory.
    adb('shell', 'rm', '-f', folder + name, folder + 'performance-error.txt', folder + 'import-storage-progress.json')
    command = ['shell', 'am', 'start', '-W', '-n', activity, '--ez', 'importStorageChecks', 'true']
    if reopen:
        command += ['--ez', 'importStorageReopen', 'true']
    print(adb(*command), flush=True)
    start = time.monotonic()
    sample = 0
    last_progress = ''
    while time.monotonic() - start < 1200:
        progress = adb('shell', 'cat', folder + 'import-storage-progress.json', optional=True)
        if progress.strip() and progress != last_progress:
            last_progress = progress
            print(progress.strip(), flush=True)
        error = adb('shell', 'cat', folder + 'performance-error.txt', optional=True)
        if error.strip():
            (args.output / 'native-error.txt').write_text(error, encoding='utf-8')
            raise RuntimeError(error)
        raw = adb('shell', 'cat', folder + name, optional=True)
        if raw.strip():
            try:
                report = json.loads(raw)
            except json.JSONDecodeError:
                time.sleep(1)
                continue
            (args.output / name).write_text(json.dumps(report, indent=2), encoding='utf-8')
            print(json.dumps(report), flush=True)
            break
        if not adb('shell', 'pidof', package, optional=True).strip():
            raise RuntimeError('Android check process exited')
        if int(time.monotonic() - start) // 30 > sample:
            sample = int(time.monotonic() - start) // 30
            print(name, 'running for', sample * 30, 'seconds', flush=True)
            (args.output / ('memory-' + str(reopen) + '.txt')).write_text(adb('shell', 'dumpsys', 'meminfo', package), encoding='utf-8')
        time.sleep(2)
    else:
        raise TimeoutError(name)
    (args.output / ('completed-memory-' + str(reopen) + '.txt')).write_text(adb('shell', 'dumpsys', 'meminfo', package), encoding='utf-8')
print('Native import, repeated apply, and cold restart verified.', flush=True)

"""Run catalogue checks twice across cold starts in the isolated Android test app."""
import argparse
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--apk', type=Path, required=True)
parser.add_argument('--serial', required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--sdk', type=Path, default=Path('C:/Users/es0p0/AppData/Local/Android/Sdk'))
args = parser.parse_args()
package = 'com.grimlist.perftest'
folder = '/sdcard/Android/data/' + package + '/files/'
args.output.mkdir(parents=True, exist_ok=True)


def adb(*command, optional=False):
    result = subprocess.run([str(args.sdk / 'platform-tools/adb.exe'), '-s', args.serial, *command], capture_output=True, timeout=120)
    if result.returncode and not optional:
        raise RuntimeError(result.stderr.decode(errors='replace'))
    return result.stdout.decode('utf-8', errors='replace')


badging = subprocess.check_output([str(args.sdk / 'build-tools/36.0.0/aapt.exe'), 'dump', 'badging', str(args.apk)], text=True)
if "package: name='" + package + "'" not in badging:
    raise RuntimeError('Refusing to replace production storage')
print(adb('install', '--no-streaming', '-r', str(args.apk.resolve())), flush=True)
activity = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).strip().splitlines()[-1]
reports = []
for run in range(2):
    adb('shell', 'am', 'force-stop', package)
    adb('shell', 'rm', '-f', folder + 'detachment-catalogue.json', folder + 'performance-error.txt')
    print(adb('shell', 'am', 'start', '-W', '-n', activity, '--ez', 'detachmentCatalogueChecks', 'true'), flush=True)
    start = time.monotonic()
    while time.monotonic() - start < 300:
        error = adb('shell', 'cat', folder + 'performance-error.txt', optional=True)
        if error.strip():
            raise RuntimeError(error)
        raw = adb('shell', 'cat', folder + 'detachment-catalogue.json', optional=True)
        if raw.strip():
            report = json.loads(raw)
            reports.append(report)
            (args.output / f'run-{run}.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
            print(json.dumps({k: v for k, v in report.items() if k != 'Armies'}), flush=True)
            break
        if not adb('shell', 'pidof', package, optional=True).strip():
            raise RuntimeError('Native process exited')
        time.sleep(2)
    else:
        raise TimeoutError('Native catalogue check did not finish')
if reports[0] != reports[1]:
    raise RuntimeError('Catalogue or saved armies changed between cold starts')
print('PASS: Native catalogue checks, restart and saved-army stability.', flush=True)

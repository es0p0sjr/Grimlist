"""Run explicitly selected native checks in the isolated performance app."""
import argparse
import json
import pathlib
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--serial', required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--checks', nargs='+', choices=['weaponMetadataChecks', 'attachmentChecks', 'enhancementChoiceChecks', 'catalogueStorageChecks', 'incrementalEvaluationChecks'],
                    default=['weaponMetadataChecks', 'attachmentChecks'])
args = parser.parse_args()
adb_path = pathlib.Path('C:/Users/es0p0/AppData/Local/Android/Sdk/platform-tools/adb.exe')
package = 'com.grimlist.perftest'
folder = '/sdcard/Android/data/' + package + '/files/'
reports = {'weaponMetadataChecks': 'weapon-metadata-checks.json', 'attachmentChecks': 'attachment-checks.json',
           'enhancementChoiceChecks': 'enhancement-choice-checks.json', 'catalogueStorageChecks': 'catalogue-storage.json',
           'incrementalEvaluationChecks': 'incremental-evaluation.json'}
args.output.mkdir(parents=True, exist_ok=True)


def adb(*command):
    result = subprocess.run([str(adb_path), '-s', args.serial, *command], capture_output=True,
                            text=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout


activity = adb('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).strip().splitlines()[-1]
if not activity.startswith(package + '/'):
    raise RuntimeError('Isolated performance app is not installed')
for check in args.checks:
    name = reports[check]
    adb('shell', 'am', 'force-stop', package)
    adb('shell', 'rm', '-f', folder + name, folder + 'performance-error.txt')
    print('Running', check, flush=True)
    adb('shell', 'am', 'start', '-W', '-n', activity, '--ez', check, 'true')
    deadline = time.monotonic() + 1200
    while time.monotonic() < deadline:
        error = adb('shell', 'sh', '-c', f"'test ! -f {folder}performance-error.txt || cat {folder}performance-error.txt'")
        if error.strip():
            raise RuntimeError(error)
        report = adb('shell', 'sh', '-c', f"'test ! -f {folder}{name} || cat {folder}{name}'")
        if report.strip():
            try:
                parsed = json.loads(report)
            except json.JSONDecodeError:
                time.sleep(0.5)
                continue
            (args.output / name).write_text(json.dumps(parsed, indent=2))
            passed = parsed.get('Passed', parsed.get('Checks'))
            print('PASS', check, len(passed) if isinstance(passed, list) else passed, flush=True)
            break
        time.sleep(0.5)
    else:
        raise TimeoutError(check)
adb('shell', 'am', 'force-stop', package)

"""Native offline acceptance on an explicitly selected disposable emulator.

Uses only the published APK and Android UI; never seeds data from the repository.
Requires --clear-data to acknowledge deleting this test installation's storage.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import time
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--adb', required=True)
parser.add_argument('--serial', required=True)
parser.add_argument('--apk', type=pathlib.Path, required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--clear-data', action='store_true')
args = parser.parse_args()
if not args.clear_data or not args.serial.startswith('emulator-'):
    parser.error('Select a disposable emulator and explicitly pass --clear-data.')
args.output.mkdir(parents=True, exist_ok=True)
package = 'com.grimlist.mobile'
results = {'apk_sha256': hashlib.sha256(args.apk.read_bytes()).hexdigest(), 'serial': args.serial, 'checks': []}

def adb(*arguments):
    result = subprocess.run([args.adb, '-s', args.serial, *arguments], capture_output=True,
                            text=True, encoding='utf-8', errors='replace', timeout=55)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout

def check(name, condition=True):
    if not condition:
        raise AssertionError(name)
    results['checks'].append(name)
    print('PASS ' + name, flush=True)
    (args.output / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')

def dump(name='current'):
    for attempt in range(5):
        output = adb('shell', 'uiautomator', 'dump', '/sdcard/grimlist-acceptance.xml')
        if 'dumped' in output:
            xml = adb('shell', 'cat', '/sdcard/grimlist-acceptance.xml')
            (args.output / (name + '.xml')).write_text(xml, encoding='utf-8')
            return ET.fromstring(xml)
        time.sleep(1)
    raise RuntimeError('No accessible app UI')

def find(ui, value):
    for node in ui.iter('node'):
        if node.get('text') == value or node.get('resource-id') == package + ':id/' + value:
            x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds', '')))
            if x2 > x1 and y2 > y1:
                return node
    return None

def wait(value, name, seconds=300):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            ui = dump(name)
        except RuntimeError as error:
            # During a cold launch Android can expose no idle accessibility root
            # yet. Respect the outer launch deadline rather than failing early.
            if str(error) != 'No accessible app UI':
                raise
            time.sleep(3)
            continue
        if find(ui, value) is not None:
            return ui
        time.sleep(3)
    raise TimeoutError('App did not show ' + value)

def tap(value, scroll=False):
    for attempt in range(12 if scroll else 1):
        node = find(dump(), value)
        if node is not None:
            x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds')))
            adb('shell', 'input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
            time.sleep(1)
            return
        adb('shell', 'input', 'swipe', '530', '2030', '530', '520', '400')
    raise AssertionError('UI control missing: ' + value)

def screenshot(name):
    adb('shell', 'screencap', '-p', '/sdcard/grimlist-acceptance.png')
    adb('pull', '/sdcard/grimlist-acceptance.png', str(args.output / (name + '.png')))

check('APK installs', 'Success' in adb('install', '--no-streaming', '-r', str(args.apk.resolve())))
check('Test app storage cleared', 'Success' in adb('shell', 'pm', 'clear', package))
adb('shell', 'svc', 'wifi', 'disable')
adb('shell', 'svc', 'data', 'disable')
adb('shell', 'cmd', 'connectivity', 'airplane-mode', 'enable')
check('Airplane mode enabled', adb('shell', 'settings', 'get', 'global', 'airplane_mode_on').strip() == '1')
check('Wi-Fi disabled', adb('shell', 'settings', 'get', 'global', 'wifi_on').strip() == '0')
check('Mobile data disabled', adb('shell', 'settings', 'get', 'global', 'mobile_data').strip() == '0')
started = time.monotonic()
adb('shell', 'monkey', '-p', package, '-c', 'android.intent.category.LAUNCHER', '1')
wait('+  New army', 'first-launch')
results['first_launch_seconds'] = round(time.monotonic() - started, 1)
check('First launch initializes the bundled library offline')
screenshot('first-launch')
tap('+  New army')
ui = wait('Death Guard', 'factions')
check('Factions available', find(ui, 'Chaos Daemons') is not None)
tap('Death Guard')
wait('army-name', 'setup')
tap('army-name')
adb('shell', 'input', 'text', 'OfflineAcceptance')
adb('shell', 'input', 'keyevent', '4')
tap('detachment-Flyblown Host')
check('Detachment selected', find(dump('detachment'), '1 / 3 DP selected') is not None)
tap('create-army', scroll=True)
wait('category-Epic Heroes', 'empty-roster')
tap('category-Epic Heroes')
ui = dump('units')
check('Units and points available', find(ui, 'Mortarion') is not None and find(ui, '375 pts') is not None)
tap('add-unit-b691a3aa8e673511d0e0fd6e')
ui = wait('375/2000 points\n1625 remaining', 'saved-roster')
check('Army created and saved with points')
check('Normal army validation succeeds', find(ui, 'validation').get('text') == '\u2713')
screenshot('saved-roster')
tap('menu')
tap('Army rules')
wait("Nurgle's Gift (Aura)", 'references')
tap("Nurgle's Gift (Aura)")
ui = dump('expanded-reference')
check('Reference card expands offline', any(len(n.get('text', '')) > 150 for n in ui.iter('node')))
tap("Nurgle's Gift (Aura)")
# Scroll through the reference view to confirm stratagem cards are also present.
found = False
for attempt in range(10):
    ui = dump('stratagems')
    if any('Command Re-roll' in n.get('text', '') for n in ui.iter('node')):
        found = True
        break
    adb('shell', 'input', 'swipe', '530', '2030', '530', '720', '400')
check('Stratagem cards available offline', found)
screenshot('stratagems')
tap('back')
wait('army-points', 'before-restart')
adb('shell', 'am', 'force-stop', package)
adb('shell', 'monkey', '-p', package, '-c', 'android.intent.category.LAUNCHER', '1')
ui = wait('375/2000 points\n1625 remaining', 'reopened')
check('Army survives process death and reopen', find(ui, 'OfflineAcceptance') is not None)
check('Reopened army still validates', find(ui, 'validation').get('text') == '\u2713')
screenshot('reopened')
(args.output / 'logcat.txt').write_text(adb('logcat', '-d', '-t', '1500'), encoding='utf-8')
print('Android standalone acceptance complete.', flush=True)

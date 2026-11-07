"""Native detachment/report checks after Test-EffectiveDatasheetsAndroid on the same APK.

Uses the same emulator/APK/output arguments. No messages are sent to a recipient.
"""
import json
import pathlib
import re
import sys
import time
import xml.etree.ElementTree as ET
import zipfile

report_only = '--report-only' in sys.argv
if report_only:
    sys.argv.remove('--report-only')

baseline = pathlib.Path(__file__).with_name('Test-StandaloneAndroid.py')
context = {'__name__': '__playtester_checks__'}
exec(compile(baseline.read_text(encoding='utf-8').split("check('APK installs'")[0], str(baseline), 'exec'), context)
previous = json.loads((context['args'].output / 'results.json').read_text(encoding='utf-8'))
if previous['apk_sha256'] != context['results']['apk_sha256'] or 'Reopened army still validates' not in previous['checks']:
    raise RuntimeError('Run baseline acceptance for this APK first.')
context['results'] = previous
adb, check, dump, find, wait, tap, screenshot = (context[n] for n in ('adb', 'check', 'dump', 'find', 'wait', 'tap', 'screenshot'))
native_tap = tap

def tap(value, scroll=False):
    for _ in range(15 if scroll else 1):
        if find(dump(), value) is not None:
            return native_tap(value)
        # Use the page gutter so a read-only JSON editor cannot consume scrolling.
        if scroll:
            adb('shell', 'input', 'swipe', '20', '2030', '20', '600', '350')
    raise AssertionError('Control not visible: ' + value)

def top():
    for _ in range(8):
        adb('shell', 'input', 'swipe', '20', '650', '20', '2100', '100')
    time.sleep(.5)

def text(ui):
    return '\n'.join(n.get('text', '') for n in ui.iter('node'))

def dismiss_keyboard():
    # A returning chooser can leave the field focused with no keyboard. Sending
    # Back then would close the modal report instead of dismissing the keyboard.
    if 'mInputShown=true' in adb('shell', 'dumpsys', 'input_method'):
        adb('shell', 'input', 'keyevent', '4')

def detachments(faction):
    tap('menu'); tap('menu-new'); tap(faction, scroll=True)
    wait('army-name', 'playtester-' + faction)
    found = {}
    for _ in range(12):
        ui = dump()
        for n in ui.iter('node'):
            if ':id/detachment-' in n.get('resource-id', ''):
                found[n.get('resource-id').split(':id/detachment-', 1)[1]] = n.get('text', '')
        if find(ui, 'create-army') is not None:
            break
        adb('shell', 'input', 'swipe', '530', '2030', '530', '600', '350')
    check(faction + ': generic detachment visible', 'Gladius Task Force' in found)
    check(faction + ': Armoured Speartip displays 3 DP', '3 DP' in found.get('Armoured Speartip', ''))
    check(faction + ': chapter exclusivity', ('Angelic Inheritors' in found) == (faction == 'Blood Angels'))
    screenshot('playtester-' + faction.replace(' ', '-'))

if not report_only:
    for faction in ('Imperial Fists', 'Blood Angels', 'Black Templars', 'Dark Angels', 'Space Wolves'):
        detachments(faction)

tap('menu'); tap('menu-home'); tap('menu'); tap('menu-bug-report')
wait('bug-title', 'bug-form')
tap('bug-title'); adb('shell', 'input', 'text', 'Detachment%scost%sreport'); dismiss_keyboard()
tap('bug-description'); adb('shell', 'input', 'text', 'Open%sthe%sdetachment%spicker'); dismiss_keyboard()
tap('bug-review', scroll=True)
ui = dump('bug-preview')
preview = next(n.get('text', '') for n in ui.iter('node') if ':id/bug-preview' in n.get('resource-id', ''))
report = json.loads(preview)
check('Android report preview contains platform and data version', report['Diagnostics']['Platform'] == 'Android' and bool(report['Diagnostics']['DataVersion']))
check('Home report excludes unrelated armies and images', report['Diagnostics']['Units'] == [] and report['Images'] == [])
tap('bug-share', scroll=True)
ui = dump('bug-share-sheet')
check('Android native share sheet opens for ZIP', any(n.get('package') in ('android', 'com.android.intentresolver') for n in ui.iter('node')) and 'bug-report' in text(ui).lower())
screenshot('bug-share-sheet')
adb('shell', 'input', 'keyevent', '4')
top()
tap('bug-title'); adb('shell', 'input', 'text', '%sedited'); dismiss_keyboard()
for _ in range(6):
    ui = dump('bug-invalidated')
    share = find(ui, 'bug-share')
    if share is not None:
        break
    adb('shell', 'input', 'swipe', '20', '2030', '20', '700', '350')
check('Editing Android report requires another review', share is not None and share.get('enabled') == 'false')

# Add an explicit synthetic screenshot through the native picker, then verify the
# actual Android document-save output against the immutable on-screen preview.
top()
adb('shell', 'screencap', '-p', '/sdcard/Download/GrimList-report-test.png')
adb('shell', 'am', 'broadcast', '-a', 'android.intent.action.MEDIA_SCANNER_SCAN_FILE', '-d', 'file:///sdcard/Download/GrimList-report-test.png')
tap('bug-images', scroll=True)
ui = dump('bug-image-picker')
node = next(n for n in ui.iter('node') if n.get('content-desc', '').startswith('GrimList-report-test.png,'))
x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds')))
adb('shell', 'input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
time.sleep(2)
tap('bug-review', scroll=True)
tap('bug-preview', scroll=True)
ui = dump('bug-image-preview')
reviewed = next(n.get('text', '') for n in ui.iter('node') if ':id/bug-preview' in n.get('resource-id', ''))
report = json.loads(reviewed)
check('Android explicitly selected image appears in reviewed inventory', len(report['Images']) == 1 and report['Images'][0]['File'] == 'images/image-1.png')
screenshot('bug-image-preview')
tap('bug-save', scroll=True)
ui = dump('bug-save-picker')
filename = next(n.get('text') for n in ui.iter('node') if n.get('class') == 'android.widget.EditText' and n.get('text', '').endswith('.zip'))
tap('SAVE')
time.sleep(2)
output = context['args'].output / filename
adb('pull', '/sdcard/Download/' + filename, str(output))
with zipfile.ZipFile(output) as archive:
    check('Android document-save ZIP exactly matches reviewed JSON and image inventory',
          archive.read('bug-report.json').decode('utf-8') == reviewed and archive.namelist() == ['bug-report.json', 'images/image-1.png'])
check('Reporting and selection checks stayed offline', adb('shell', 'settings', 'get', 'global', 'airplane_mode_on').strip() == '1')
print('Android playtester checks complete.', flush=True)

"""Exercise conditional rules through the native UI on a disposable emulator.

Run Test-StandaloneAndroid.py for this APK first. This script retains its data and
creates a separate test army; it does not push a library/database to the device.
"""
import pathlib
import time
import sys
import re

resume = '--resume-existing' in sys.argv
if resume:
    sys.argv.remove('--resume-existing')

helper = pathlib.Path(__file__).with_name('Test-StandaloneAndroid.py')
source = helper.read_text(encoding='utf-8').split("check('APK installs'")[0]
source = source.replace('if not args.clear_data or not args.serial.startswith', 'if not args.serial.startswith')
context = {'__name__': '__conditional_ui__'}
exec(compile(source, str(helper), 'exec'), context)
adb, check, dump, find, wait, tap, screenshot = (context[n] for n in
    ('adb', 'check', 'dump', 'find', 'wait', 'tap', 'screenshot'))
package = context['package']


def tap(value, scroll=False):
    # Short steps avoid jumping an attachment button past the fixed header.
    for _ in range(30 if scroll else 10):
        node = find(dump(), value)
        if node is not None:
            if node.get('enabled') == 'false':
                time.sleep(1)
                continue
            x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds')))
            adb('shell', 'input', 'tap', str((x1 + x2) // 2), str((y1 + y2) // 2))
            time.sleep(1)
            return
        if scroll:
            adb('shell', 'input', 'swipe', '20', '1600', '20', '1100', '250')
        else:
            time.sleep(1)
    raise AssertionError('UI control missing: ' + value)


def top():
    for _ in range(4):
        adb('shell', 'input', 'swipe', '20', '650', '20', '2100', '100')
    time.sleep(1)


def texts(ui):
    return '\n'.join(n.get('text', '') for n in ui.iter('node'))


def hide_keyboard():
    if 'mInputShown=true' in adb('shell', 'dumpsys', 'input_method'):
        adb('shell', 'input', 'keyevent', '4')


def entry_ids(ui):
    return [n.get('resource-id').split(':id/roster-entry-', 1)[1] for n in ui.iter('node')
            if ':id/roster-entry-' in n.get('resource-id', '')]


def add(name):
    tap('unit-search')
    adb('shell', 'input', 'keyevent', '123')
    adb('shell', 'input', 'keyevent', *(['67'] * 40))
    adb('shell', 'input', 'text', name.replace(' ', '%s'))
    hide_keyboard()
    time.sleep(2)
    ui = dump()
    button = next(n.get('resource-id').split(':id/', 1)[1] for n in ui.iter('node')
                  if ':id/add-unit-' in n.get('resource-id', ''))
    tap(button)
    ui = dump()
    if any(n.get('text', '').startswith('Add ' + name) for n in ui.iter('node')):
        size = next(n.get('text') for n in ui.iter('node')
                    if 'model' in n.get('text', '') and 'pts' in n.get('text', ''))
        tap(size)


def fnp(expected, name):
    top()
    for _ in range(8):
        ui = dump(name)
        node = find(ui, 'feel-no-pain')
        actual = node is not None and '5+++' in node.get('text', '')
        if find(ui, 'LOKHUST DESTROYERS') is not None and actual == expected:
            check(name)
            return
        time.sleep(1)
    check(name, False)


if not resume:
    wait('menu', 'ready')
    tap('menu')
    tap('menu-new')
    tap('Necrons', scroll=True)
    wait('army-name', 'setup')
    tap('army-name')
    adb('shell', 'input', 'text', 'ConditionalNative')
    hide_keyboard()
    tap('detachment-Cursed Legion', scroll=True)
    tap('create-army', scroll=True)
    wait('unit-search', 'empty')
    add('Technomancer')
    tech = entry_ids(dump())[0]
    add('Lokhust Destroyers')
    destroyers = next(i for i in entry_ids(dump()) if i != tech)
else:
    tech, destroyers = entry_ids(dump())
tap('roster-entry-' + destroyers)
wait('unit-tab-datasheet', 'destroyers')
fnp(False, 'Unattached Destroyers have no granted FNP')
all_text = ''
for _ in range(8):
    all_text += texts(dump()) + '\n'
    adb('shell', 'input', 'swipe', '20', '2030', '20', '520', '200')
check('Only the selected detachment rule is visible', 'Cold Fervour' in all_text and
      not any(name in all_text for name in ['Command Protocols', 'Hyperphasing', 'Power Matrix', 'Cosmic Distortion']))
tap('back')
tap('roster-entry-' + tech)
wait('unit-tab-datasheet', 'technomancer')
tap('enhancement-Murdermind', scroll=True)
check('Murdermind is selectable')
top()
tap('attach-' + destroyers, scroll=True)
check('Enhancement unlocks attachment in the native picker')
top()
tap('view-attached-' + destroyers, scroll=True)
fnp(True, 'Legal attachment grants FNP on the native datasheet')
screenshot('attached-fnp')
tap('detach-' + tech, scroll=True)
fnp(False, 'Detaching removes FNP immediately')
tap('undo')
fnp(True, 'Undo restores the legal attachment and FNP')
tap('redo')
fnp(False, 'Redo removes the attachment and FNP')
tap('undo')
fnp(True, 'Attachment restored before restart')
adb('shell', 'am', 'force-stop', package)
adb('shell', 'monkey', '-p', package, '-c', 'android.intent.category.LAUNCHER', '1')
wait('menu', 'reopened')
ui = dump()
if find(ui, 'unit-tab-datasheet') is None:
    tap('roster-entry-' + destroyers)
fnp(True, 'Saved attachment and conditional FNP survive process restart')
tap('view-attached-' + tech, scroll=True)
top()
tap('remove-enhancement', scroll=True)
top()
tap('view-attached-' + destroyers, scroll=True)
fnp(False, 'Removing the prerequisite enhancement removes dependent FNP')
tap('back')
tap('validation')
ui = dump('invalid-attachment')
check('Invalid resulting attachment is explained', 'attachment' in texts(ui).casefold()
      or 'bodyguard is not permitted' in texts(ui).casefold())
print('Conditional Android acceptance complete.', flush=True)

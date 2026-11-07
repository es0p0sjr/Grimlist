"""Run offline standalone acceptance, then exercise effective datasheets through Android UI.

Takes the same explicit disposable-emulator arguments as Test-StandaloneAndroid.py.
The APK supplies all data. No repository library or database is pushed to Android.
"""
import pathlib
import runpy
import time
import sys
import json

standalone = pathlib.Path(__file__).with_name('Test-StandaloneAndroid.py')
if '--features-only' in sys.argv:
    # Retry native feature automation after a completed baseline for this exact APK.
    sys.argv.remove('--features-only')
    context = {'__name__': '__effective_retry__'}
    exec(compile(standalone.read_text(encoding='utf-8').split("check('APK installs'")[0], str(standalone), 'exec'), context)
    prior = json.loads((context['args'].output / 'results.json').read_text(encoding='utf-8'))
    if prior['apk_sha256'] != context['results']['apk_sha256'] or 'Reopened army still validates' not in prior['checks']:
        raise RuntimeError('Run complete standalone acceptance for this APK before retrying features.')
    prior['checks'] = prior['checks'][:prior['checks'].index('Reopened army still validates') + 1]
    context['results'] = prior
else:
    context = runpy.run_path(str(standalone))
adb, check, dump, find, wait, tap, screenshot = (context[n] for n in
    ('adb', 'check', 'dump', 'find', 'wait', 'tap', 'screenshot'))
package = context['package']


def top():
    for _ in range(9):
        adb('shell', 'input', 'swipe', '530', '650', '530', '2100', '120')
    time.sleep(1)


def texts(ui):
    return '\n'.join(n.get('text', '') for n in ui.iter('node'))


def choose(name):
    top()
    ui = dump()
    selector = next(n.get('resource-id').split(':id/', 1)[1] for n in ui.iter('node')
                    if ':id/datasheet-choice-' in n.get('resource-id', ''))
    tap(selector)
    tap(name)


def expect_stat(profile, field, value, name):
    for _ in range(14):
        ui = dump(name)
        parents = {child: parent for parent in ui.iter() for child in parent}
        for title in ui.iter('node'):
            if not title.get('text', '').casefold().endswith(profile.casefold()):
                continue
            container = parents.get(title)
            while container is not None:
                fields = [n for n in container.iter('node') if n.get('text') == field]
                if fields:
                    if any(value in [n.get('text') for n in parents[label].iter('node')] for label in fields):
                        return ui
                    break
                container = parents.get(container)
        adb('shell', 'input', 'swipe', '530', '2030', '530', '1250', '600')
    raise AssertionError(f'Missing current stat: {profile} {field}={value}')


def dialog_button(label):
    # Native Android alert buttons can expose their capitalised display text.
    ui = dump()
    value = next(n.get('text') for n in ui.iter('node') if n.get('text', '').casefold() == label.casefold())
    tap(value)


tap('menu')
tap('menu-new')
tap('Chaos Daemons')
wait('army-name', 'effective-setup')
tap('army-name')
adb('shell', 'input', 'text', 'EffectiveAcceptance')
adb('shell', 'input', 'keyevent', '4')
tap('detachment-Daemonic Incursion', scroll=True)
tap('create-army', scroll=True)
wait('unit-search', 'effective-empty')
tap('unit-search')
adb('shell', 'input', 'text', 'Daemon%sPrince')
adb('shell', 'input', 'keyevent', '4')
time.sleep(2)
ui = dump('effective-library')
# Pick the first matching source row (the unwinged prince), without embedding source IDs.
add = next(n.get('resource-id').split(':id/', 1)[1] for n in ui.iter('node')
           if ':id/add-unit-' in n.get('resource-id', ''))
tap(add)
ui = dump('effective-roster')
entry = next(n.get('resource-id').split(':id/', 1)[1] for n in ui.iter('node')
             if ':id/roster-entry-' in n.get('resource-id', ''))
tap(entry)
wait('unit-tab-datasheet', 'effective-base')
check('Prince exposes source-driven allegiance selector', 'Allegiance' in texts(dump()).title())
choose('Khorne')
expect_stat('strike', 'S', '10', 'effective-khorne')
check('Khorne changes effective weapon Strength')
top()
tap("enhancement-A'rgath, the King of Blades", scroll=True)
top()
ui = expect_stat('strike', 'S', '11', 'effective-argath')
expect_stat('strike', 'A', '7', 'effective-argath-attacks')
check("A'rgath changes effective melee Strength and Attacks")
check('Datasheet shows current values without change history', '\u2192' not in texts(ui) and 'EFFECTIVE DATASHEET' not in texts(ui))
screenshot('effective-argath')
expect_stat('strike', 'S', '11', 'effective-argath-weapon')
check('Weapon table shows the combined effective Strength')
choose('Nurgle')
ui = dump('effective-confirmation')
check('Allegiance change names invalid enhancement before applying', "A'rgath, the King of Blades" in texts(ui)
      and 'remove and change' in texts(ui).casefold())
dialog_button('Cancel')
check('Cancelling preserves Khorne', 'Khorne' in texts(dump('effective-cancelled')))
choose('Nurgle')
dialog_button('Remove and change')
top()
ui = expect_stat('DAEMON PRINCE OF CHAOS', 'T', '11', 'effective-nurgle')
check('Confirmed change replaces keyword/stat and removes enhancement', "A'rgath" not in texts(ui) and 'Khorne' not in texts(ui))
tap('undo')
top()
expect_stat('strike', 'S', '11', 'effective-undone')
check('Undo restores allegiance and enhancement together')
tap('redo')
top()
expect_stat('DAEMON PRINCE OF CHAOS', 'T', '11', 'effective-redone')
check('Redo restores Nurgle')
choose('Tzeentch')
expect_stat('Infernal cannon', 'A', '6', 'effective-tzeentch')
check('Tzeentch changes infernal cannon Attacks')
choose('Slaanesh')
top()
expect_stat('DAEMON PRINCE OF CHAOS', 'M', '10"', 'effective-slaanesh')
check('Slaanesh changes Move')
choose('Nurgle')
top()
screenshot('effective-nurgle')
adb('shell', 'am', 'force-stop', package)
adb('shell', 'monkey', '-p', package, '-c', 'android.intent.category.LAUNCHER', '1')
wait('army-points', 'effective-reopened')
ui = dump()
if find(ui, 'unit-tab-datasheet') is None:
    tap(entry)
top()
expect_stat('DAEMON PRINCE OF CHAOS', 'T', '11', 'effective-reopened-nurgle')
check('Allegiance and effective stats survive Android process death')
check('Feature workflow stayed offline', adb('shell', 'settings', 'get', 'global', 'airplane_mode_on').strip() == '1')
screenshot('effective-reopened')
print('Android effective-datasheet acceptance complete.', flush=True)

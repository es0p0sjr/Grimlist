"""Native recipient regression checks on the disposable acceptance emulator.

Run Test-StandaloneAndroid.py first, then pass its APK, serial and output arguments.
Retains the imported library and creates separate test armies through the UI.
"""
import pathlib

helper = pathlib.Path(__file__).with_name('Test-ConditionalRulesAndroid.py')
source = helper.read_text(encoding='utf-8').split('if not resume:')[0]
context = {'__name__': '__recipient_ui__', '__file__': str(helper)}
exec(compile(source, str(helper), 'exec'), context)
globals().update({name: context[name] for name in
    ('tap', 'top', 'dump', 'find', 'wait', 'check', 'add', 'entry_ids', 'screenshot')})


def badge(name, roll):
    top()
    ui = dump(name)
    node = find(ui, 'feel-no-pain')
    check(name, node is None if roll is None else
          node is not None and roll in node.get('text', ''))


for faction, leader_name, guard_name, roll in [
        ('Death Guard', 'Lord of Contagion', 'Deathshroud Terminators', '4+++'),
        ('Tyranids', 'Hive Tyrant', 'Tyrant Guard', '5+++')]:
    wait('menu', 'ready')
    tap('menu')
    tap('menu-new')
    tap(faction, scroll=True)
    wait('army-name', 'setup')
    tap('create-army', scroll=True)
    wait('unit-search', 'empty')
    add(leader_name)
    leader = entry_ids(dump())[0]
    add(guard_name)
    guard = next(i for i in entry_ids(dump()) if i != leader)
    tap('roster-entry-' + guard)
    badge(guard_name + ' unattached', None)
    tap('back')
    tap('roster-entry-' + leader)
    badge(leader_name + ' unattached', None)
    tap('attach-' + guard, scroll=True)
    badge(leader_name + ' receives protection', roll)
    screenshot(faction + '-recipient')
    tap('view-attached-' + guard, scroll=True)
    badge(guard_name + ' does not receive its own grant', None)
    tap('menu')
    tap('menu-mode')
    badge(guard_name + ' play mode', None)
    tap('view-attached-' + leader, scroll=True)
    badge(leader_name + ' play mode', roll)
    tap('menu')
    tap('menu-mode')
    tap('view-attached-' + guard, scroll=True)
    tap('detach-' + leader, scroll=True)
    badge(guard_name + ' detached', None)
    tap('undo')
    tap('view-attached-' + leader, scroll=True)
    badge(leader_name + ' undo restores protection', roll)
    tap('redo')
    badge(leader_name + ' redo removes protection', None)

print('Attachment recipient Android acceptance complete.', flush=True)

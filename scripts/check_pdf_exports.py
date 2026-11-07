"""Verify PDFs produced by the native export smoke test using the real PDF text extractor."""
import os
import shutil
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
tool = os.environ.get('GRIMLIST_PDFTOTEXT') or shutil.which('pdftotext') or r'C:\Program Files\dnGrep\Plugins\PdfSearch\pdftotext.exe'


def extract(name):
    output = root / (name + '-extracted.txt')
    subprocess.run([tool, '-enc', 'UTF-8', '-layout', str(root / (name + '.pdf')), str(output)], check=True)
    return output.read_text(encoding='utf-8-sig')


selected = extract('roster-selected')
assert '\u0394\u03bf\u03ba\u03b9\u03bc\u03ae & <Army>' in selected, 'Unicode title was lost'
assert selected.count('\f') > 5, 'Long rule did not paginate'
assert 'END OF LONG RULE' in selected, 'Long ability was cut off'
assert 'Attached Leader: Commander' in selected and 'Attached Leader: Medic' in selected
assert 'Attached to: Guardians' in selected
assert 'Plasma gun' in selected and 'supercharge' in selected, 'Equipped alternate profiles lost'
assert 'Unequipped flamer' not in selected, 'Unselected weapon leaked into equipped-only PDF'
assert 'IMPORTED ARMY RULE' in selected and 'SELECTED DETACHMENT RULE' in selected
assert 'UNSELECTED DETACHMENT RULE' not in selected
all_weapons = extract('roster-all-weapons')
assert 'Unequipped flamer' in all_weapons, 'All-weapons option ignored'
assert 'IMPORTED ARMY RULE' not in all_weapons, 'Rules toggle ignored'
missing = extract('roster-missing')
assert missing.count('Datasheet unavailable') == 3, 'Missing unit datasheets were silently omitted'
if (root / 'death-guard.pdf').exists():
    death_guard = extract('death-guard')
    assert '10 models' in death_guard and 'Attached Leader: Biologus Putrifier' in death_guard
    assert 'Attached Leader: Malignant Plaguecaster' in death_guard and 'Foul Infusion' in death_guard
print('PASS: real PDF extraction; Unicode, pagination, both Leaders, weapons, rule options and missing datasheets.')

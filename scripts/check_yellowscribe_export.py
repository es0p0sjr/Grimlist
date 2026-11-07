"""Offline compatibility check with the unmodified upstream Yellowscribe parser.

Usage: python scripts/check_yellowscribe_export.py roster.ros upstream-bin-directory
The directory must contain roszParser.js and 10e11eDataModel.js from
https://github.com/ThePants999/Yellowscribe (CC0-1.0).
Only ZIP extraction and xml2js are adapted: ElementTree reads our uncompressed XML
and supplies xml2js's documented attribute/child-array shape. The actual edition,
unit, model, equipment and ability parsing runs through upstream code in Node.
No roster is uploaded and no third-party package installation is needed.
"""
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

roster, upstream = map(lambda s: Path(s).resolve(), sys.argv[1:3])


def convert(element):
    value = {"$": dict(element.attrib)} if element.attrib else {}
    if element.text and element.text.strip():
        if not element.attrib and not len(element):
            return element.text.strip()
        value["_"] = element.text.strip()
    for child in element:
        value.setdefault(child.tag.split("}")[-1], []).append(convert(child))
    return value or None


xml = ET.parse(roster).getroot()
converted = roster.with_suffix(".xml2js.json")
converted.write_text(json.dumps({"roster": convert(xml)}, ensure_ascii=False), encoding="utf-8")
script = r"""
const fs = require('fs'), vm = require('vm'), path = require('path');
const parsed = JSON.parse(fs.readFileSync(process.argv[1], 'utf8'));
const source = process.argv[2];
const context = { module: {exports: {}}, require: name => {
  if (name === 'adm-zip') return class { constructor() { throw new Error('Uncompressed ROS input'); } };
  if (name === './xml') return {parseXML: () => parsed};
  if (name === './9eRoster') return {parse: () => { throw new Error('Unexpected legacy edition'); }};
  if (name === './10e11eDataModel') return require(path.join(source, '10e11eDataModel.js'));
  throw new Error('Unexpected parser dependency: ' + name);
}};
vm.runInNewContext(fs.readFileSync(path.join(source, 'roszParser.js'), 'utf8'), context);
const result = context.module.exports.roszParse(Buffer.from(''), 'allModels');
process.stdout.write(JSON.stringify(result, (_, value) => value instanceof Map ? Object.fromEntries(value) : value instanceof Set ? [...value] : value, 2));
"""
result = subprocess.run(["node", "-e", script, str(converted), str(upstream)], check=True, capture_output=True, text=True, encoding="utf-8")
parsed = json.loads(result.stdout)
assert not parsed.get("errors"), parsed["errors"]
assert parsed["units"], "Upstream parser returned no units"
ns = {'r': xml.tag.split('}')[0][1:]}
source_units = xml.findall('r:forces/r:force/r:selections/r:selection', ns)
assert len(source_units) == len(parsed['units']), 'A unit was dropped or duplicated'
for original, unit in zip(source_units, parsed['units'].values()):
    models = original.findall('r:selections/r:selection', ns)
    assert unit['models']['totalNumberOfModels'] == sum(int(m.attrib['number']) for m in models)
    for expected, model in zip(models, unit['models']['models'].values()):
        expected_weapons = expected.findall('r:selections/r:selection', ns)
        assert sum(float(w.attrib['number']) for w in expected_weapons) == sum(w['number'] for w in model['weapons']), 'Weapon count/ownership changed'
    for ability in original.findall('r:profiles/r:profile', ns):
        if ability.attrib['name'] == 'GrimList attachments':
            expected = ability.find('r:characteristics/r:characteristic', ns).text
            assert unit['abilities']['GrimList attachments']['desc'] == expected, 'Attachment information was overwritten'
    saves = [c.text for c in original.findall('.//r:characteristic', ns) if c.attrib['name'] == 'INSV']
    assert all(save in [p['insv'] for p in unit['modelProfiles'].values()] for save in saves), 'Invulnerable save lost'
output = roster.with_suffix(".yellowscribe-parsed.json")
output.write_text(result.stdout, encoding="utf-8")
print(f"PASS: upstream {parsed['edition']} parser; {len(parsed['units'])} units; "
      f"{sum(u['models']['totalNumberOfModels'] for u in parsed['units'].values())} models. {output}")

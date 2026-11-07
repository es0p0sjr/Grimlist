"""Check a production APK; optionally verify an in-place physical-phone update.

Does not uninstall, clear storage, seed rosters or submit any data.
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--apk', type=pathlib.Path, required=True)
parser.add_argument('--version', required=True)
parser.add_argument('--code', type=int, required=True)
parser.add_argument('--certificate', required=True)
parser.add_argument('--evidence', type=pathlib.Path, required=True)
parser.add_argument('--serial')
args = parser.parse_args()
sdk = pathlib.Path(os.environ.get('ANDROID_HOME', 'C:/Users/es0p0/AppData/Local/Android/Sdk'))
environment = os.environ.copy()
environment['JAVA_HOME'] = 'C:/Program Files/Microsoft/jdk-21.0.12.101-hotspot'
args.evidence.mkdir(parents=True, exist_ok=True)
package = 'com.grimlist.mobile'
report = dict(apk=str(args.apk.resolve()), version=args.version, code=args.code,
              sha256=hashlib.file_digest(args.apk.open('rb'), 'sha256').hexdigest(), checks=[])

def run(command, timeout=180):
    result = subprocess.run([str(x) for x in command], capture_output=True, text=True,
                            encoding='utf-8', errors='replace', env=environment, timeout=timeout)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout + result.stderr

def check(name, condition=True):
    if not condition:
        raise AssertionError(name)
    report['checks'].append(name)
    (args.evidence/'results.json').write_text(json.dumps(report, indent=2)+'\n')
    print('PASS '+name, flush=True)

badging = run([sdk/'build-tools/36.0.0/aapt.exe', 'dump', 'badging', args.apk])
(args.evidence/'identity.txt').write_text(badging)
check('Production package and release version/code',
      f"package: name='{package}' versionCode='{args.code}' versionName='{args.version}'" in badging)
check('ARM64 and x64 support', "'arm64-v8a'" in badging and "'x86_64'" in badging)
signature = run([sdk/'build-tools/36.0.0/apksigner.bat', 'verify', '--verbose', '--print-certs', args.apk])
(args.evidence/'signature.txt').write_text(signature)
check('Signature verified with existing certificate', args.certificate.lower() in signature.lower())
with zipfile.ZipFile(args.apk) as archive:
    check('ZIP CRC integrity', archive.testzip() is None)
    check('No PDB files', not any(n.lower().endswith('.pdb') for n in archive.namelist()))
    symbol = b'Java_crc6488302ad6e9e4df1a_MauiApplication_n_1onCreate\0'
    for abi in ['arm64-v8a','x86_64']:
        check(abi+' native startup binding', symbol in archive.read(f'lib/{abi}/libxamarin-app.so'))
sidecar = args.apk.with_suffix(args.apk.suffix+'.sha256')
check('Published hash sidecar', sidecar.read_text().split()[0].lower()==report['sha256'])

if args.serial:
    def adb(*command):
        return run([sdk/'platform-tools/adb.exe','-s',args.serial,*command])
    devices = run([sdk/'platform-tools/adb.exe','devices','-l'])
    check('Selected physical phone is connected', not args.serial.startswith('emulator-') and
          re.search(r'^'+re.escape(args.serial)+r'\s+device\s', devices, re.M) is not None)
    def identity():
        value=adb('shell','dumpsys','package',package)
        def field(pattern):
            match=re.search(pattern,value)
            return match.group(1).strip() if match else None
        return dict(version=field(r'versionName=([^\r\n]+)'),code=field(r'versionCode=(\d+)'),
                    appId=field(r'(?:appId|userId)=(\d+)'),firstInstallTime=field(r'firstInstallTime=([^\r\n]+)'))
    before=identity()
    check('Existing production installation found', bool(before['version'] and before['firstInstallTime']))
    report['phone']=dict(serial=args.serial,before=before)
    install=adb('install','--no-streaming','-r',str(args.apk.resolve()))
    (args.evidence/'install.txt').write_text(install)
    check('In-place installation succeeded', 'Success' in install)
    after=identity();report['phone']['after']=after
    check('Installed version/code',after['version']==args.version and after['code']==str(args.code))
    check('Original installation identity preserved',before['firstInstallTime']==after['firstInstallTime'] and
          before['appId'] is not None and before['appId']==after['appId'])
    installed=adb('shell','pm','path',package).strip()
    check('Single installed production APK',installed.startswith('package:/data/app/') and '\n' not in installed)
    installed_hash=adb('shell','sha256sum',installed.removeprefix('package:')).split()[0]
    check('Installed APK matches delivered bytes',installed_hash==report['sha256'])
    activity=adb('shell','cmd','package','resolve-activity','--brief',package).strip().splitlines()[-1]
    check('Production launcher resolved',activity.startswith(package+'/'))
    adb('shell','am','force-stop',package)
    launch=adb('shell','am','start','-W','-n',activity)
    report['phone']['launch']=launch
    check('Cold launch succeeded','Status: ok' in launch and 'LaunchState: COLD' in launch)
    time.sleep(10)
    dump='/sdcard/Android/data/'+package+'/files/release-'+args.version+'-ui.xml'
    deadline=time.monotonic()+180
    last_error=''
    try:
        while time.monotonic()<deadline:
            try:
                adb('shell','uiautomator','dump',dump)
                raw=adb('shell','cat',dump)
                ui=ET.fromstring(raw[raw.index('<?xml'):])
            except (RuntimeError,ET.ParseError,ValueError) as error:
                last_error=str(error)
                time.sleep(3)
                continue
            nodes=[n for n in ui.iter('node') if n.get('package')==package]
            enabled=[n for n in nodes if n.get('enabled')=='true' and n.get('clickable')=='true']
            if enabled:
                report['phone']['ui']=dict(nodes=len(nodes),enabledInteractive=len(enabled))
                break
            time.sleep(3)
        else:
            raise TimeoutError('Production UI did not become interactive: '+last_error)
    finally:
        adb('shell','rm','-f',dump)
    check('Production UI became interactive',bool(enabled))
    check('App remains running',bool(adb('shell','pidof',package).strip()))
else:
    report['phone']='Not run: no physical device selected'
(args.evidence/'results.json').write_text(json.dumps(report,indent=2)+'\n')

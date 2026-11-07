"""Run the separate WPF diagnostic host against its own marked fixture directory."""
import argparse
import json
import pathlib
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument('--database', type=pathlib.Path, required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
args = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parents[1]
exe = repo / 'tools/GrimList.WindowsPerformance/bin/Performance/GrimList.WindowsPerformance.exe'
args.output.mkdir(parents=True, exist_ok=True)
startup = subprocess.STARTUPINFO()
startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0

for label in ['initial', 'editing', 'startup-1', 'startup-2', 'startup-3', 'startup-4', 'startup-5']:
    output = args.output / label
    output.mkdir(exist_ok=True)
    command = [str(exe), str(args.database.resolve()), str(output.resolve())]
    if label != 'editing':
        command.append('--startup-only')
    began = time.monotonic()
    with (output / 'process.log').open('w', encoding='utf-8') as log:
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, startupinfo=startup)
        try:
            process.wait(timeout=1200)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            raise
    if (output / 'error.txt').exists():
        raise RuntimeError((output / 'error.txt').read_text())
    if process.returncode:
        raise RuntimeError(f'Windows benchmark exited {process.returncode}: {output}')
    report = json.loads((output / 'startup.json').read_text(encoding='utf-8-sig'))
    if label == 'editing':
        samples = json.loads((output / 'performance.json').read_text(encoding='utf-8-sig'))
        if len(samples) != 160:
            raise RuntimeError(f'Expected 160 WPF samples; received {len(samples)}')
    print(label, round(report['AppEntryToUsableMs'], 2), 'ms startup;',
          round(time.monotonic() - began, 2), 's process', flush=True)

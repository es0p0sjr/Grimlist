"""Summarize measured stages; never substitute PC results for Android measurements."""
import json
import math
import pathlib
import re
import statistics

root = pathlib.Path(__file__).resolve().parents[1]
data = root / '.local/staged-performance'

def read_log(path):
    raw = path.read_bytes()
    return raw.decode('utf-16' if raw.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig')

def distribution(values):
    return {'samples': len(values), 'median': statistics.median(values),
            'p95': sorted(values)[math.ceil(.95 * len(values)) - 1]}

def startup(paths):
    runs = [json.loads(p.read_text(encoding='utf-8-sig')) for p in paths]
    return {key: distribution([run[key] for run in runs]) for key in
            ['LibraryReadyMs', 'StartupAllocatedBytes', 'ManagedAfterRecoveryBytes']}

def editing(phase):
    samples = [sample for run in [1, 2] for sample in
               json.loads((data / f'phase{phase}-pc/round-{run}.json').read_text())['Samples']]
    return [{
        'entries': size, 'operation': operation,
        **{key: distribution([sample[key] for sample in samples if sample['Entries'] == size and sample['Operation'] == operation])
           for key in ['CommandMs', 'AllocatedBytes']}}
        for size in [3, 20] for operation in ['add', 'equipment', 'enhancement', 'attachment', 'detachment']]

home = json.loads((data / 'phase4-home/home.json').read_text())
comparison = [{'method': method, 'comparisons': 500, 'ms': float(ms), 'allocatedBytes': int(allocated)}
              for method, ms, allocated in re.findall(r'(JSON|Structural): ([0-9.]+) ms, (\d+) allocated bytes',
                  read_log(root / '.local/staged-comparison.log'))]
checks = read_log(root / '.local/staged-final-tests.log')
report = {
    'environment': 'Windows shared Release harness, .NET 10.0.10; no Android rendering in PC timings',
    'method': 'Nearest-rank p95. Editing: two alternating runs of ten samples per operation/roster, plus unrecorded warm-up. Fresh processes with existing library for startup; OS file cache is not reset.',
    'limitations': [
        'Physical Android installation was blocked by INSTALL_FAILED_USER_RESTRICTED; user deferred phone tests.',
        'Android cold-to-usable timing, managed/native heap, PSS/swap and native virtualization acceptance remain pending.',
        'Editing latency varied substantially; lower allocation and isolated comparison cost do not establish an end-to-end latency improvement.',
        'Five baseline starts and four cache-hit starts give limited startup-tail estimates.',
        'WorkingSetBytes in raw host reports is sampled after workload completion, not a native-heap measurement.',
        'AOT is an opt-in experiment; no compilation setting has been adopted based on unmeasured device gains.'
    ],
    'phase1': {
        'baseline': startup(sorted((data / 'baseline-pc').glob('startup-*.json'))),
        'indexedCacheMiss': startup([data / 'phase1-pc/startup-0.json']),
        'indexedCacheHit': startup([data / f'phase1-pc/startup-{i}.json' for i in range(1, 5)])
    },
    'phase2': {'before': editing(1), 'after': editing(2), 'isolatedComparison': comparison,
               'stageNote': 'Macro timings use frozen stage binaries. The final comparer additionally enforces ordinal dictionary-key equality; the isolated comparison and full regression suite include that guard.'},
    'phase3': {'implementation': 'Data-backed CollectionViews; no prebuilt item Views; incremental updates and 250 ms search debounce retained.',
               'deviceGain': None, 'deviceAcceptance': 'Deferred'},
    'phase4': [{
        'entriesPerArmy': size, 'savedArmies': 10, 'cached': cached,
        **{key: distribution([s[key] for s in home if s['Entries'] == size and s['Cached'] == cached])
           for key in ['Ms', 'AllocatedBytes']}}
        for size in [3, 20] for cached in [False, True]],
    'phase5': {'profiledAotBuild': 'Rejected by installed SDK: XA1030 requires PublishTrimmed=true. Unusable experimental profile removed; trimming was not enabled.',
               'deviceBenchmarks': 'Deferred', 'otherCompilationBenchmarks': 'Deferred', 'shippingCompilationChanged': False},
    'validation': {
        'regressionChecks': len(re.findall(r'^PASS ', checks, re.MULTILINE)),
        'regressionResult': 'All checks passed.' if 'All checks passed.' in checks else 'Incomplete',
        'windowsBuild': 'Passed; two existing CS4014 smoke-dispatch warnings',
        'windowsRecipientUiSmoke': 'Passed leader/bodyguard build/play, detach, undo and redo',
        'androidDebugBuild': 'Passed, no warnings or errors',
        'androidReleaseTestPackage': '.local/staged-performance/release-apk/com.grimlist.perftest-Signed.apk',
        'androidReleasePackageIdentity': 'Verified com.grimlist.perftest, arm64-v8a and x86_64; not installed',
        'productionReleaseArtifactsReplaced': False
    }
}
output = root / 'docs/performance/staged-results.json'
output.parent.mkdir(exist_ok=True)
output.write_text(json.dumps(report, indent=2) + '\n')
print(output)

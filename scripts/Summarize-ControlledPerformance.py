"""Summarize balanced comparison blocks, keeping absolute delays and variability."""
import argparse
import json
import math
import pathlib
import statistics

parser = argparse.ArgumentParser()
parser.add_argument('--input', type=pathlib.Path, required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
args = parser.parse_args()
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
complete = read(args.input/'complete.json')
reports = [read(p) for p in sorted(args.input.glob('block-*.json'))]
assert complete['exactParity'] and len(reports)==12
modes = ['optimized','full','uncached']
operations = ['add','equipment','enhancement','attachment','detachment','home']

def describe(values):
    v=sorted(values)
    return dict(n=len(v),mean=statistics.mean(v),median=statistics.median(v),
                p95=v[math.ceil(.95*len(v))-1],minimum=v[0],maximum=v[-1],
                above500Ms=sum(x>500 for x in v),above1000Ms=sum(x>1000 for x in v))

def paired(operation, field, baseline, exclude_group=None):
    differences=[]
    for start in range(0,12,3):
        if exclude_group is not None and start//3==exclude_group:
            continue
        block={r['Mode']:statistics.mean(s[field] for s in r['Samples'] if s['Operation']==operation) for r in reports[start:start+3]}
        differences.append(block['optimized']-block[baseline])
    mean=statistics.mean(differences)
    critical={3:4.302652730,4:3.182446305}[len(differences)]
    half=critical*statistics.stdev(differences)/math.sqrt(len(differences))
    return dict(groupMeanDeltasMs=differences,meanDeltaMs=mean,exploratory95PercentIntervalMs=[mean-half,mean+half])

summary=[]
for operation in operations:
    row=dict(operation=operation,modes={},optimizedMinusFull=paired(operation,'UsableMs','full'),optimizedMinusUncached=paired(operation,'UsableMs','uncached'),
        sensitivityExcludingGroupContainingBlock03=dict(
            optimizedMinusFull=paired(operation,'UsableMs','full',1),
            optimizedMinusUncached=paired(operation,'UsableMs','uncached',1)))
    for mode in modes:
        values=[s for r in reports if r['Mode']==mode for s in r['Samples'] if s['Operation']==operation]
        row['modes'][mode]=dict(usableMs=describe([s['UsableMs'] for s in values]),
            commandMs=describe([s['CommandMs'] for s in values]),
            presentationMs=describe([s['UsableMs']-s['CommandMs'] for s in values]),
            meanAllocatedMiB=statistics.mean(s['AllocatedBytes']/1048576 for s in values),
            blockMeanMs=[statistics.mean(s['UsableMs'] for s in r['Samples'] if s['Operation']==operation) for r in reports if r['Mode']==mode])
    summary.append(row)
result=dict(identity=read(args.input/'identity.json'),complete=complete,summary=summary,
    incrementalSamples=sum(v['Incremental'] for r in reports if r['Mode']=='optimized' for v in r['Verification']),
    inputHash=reports[0]['InputHash'],coreModule=reports[0]['CoreModule'],blocks=reports,
    limitations=['Mode bypasses isolate current rule optimizations, not a historical build or the entire architecture.',
        'Four balanced three-mode groups; intervals are exploratory t intervals over group means, not independent-sample claims.',
        'Primary results retain every block. Post-hoc sensitivity excludes the entire second group: Windows block03 overlapped a source search; Android block03 was abnormally slow with unchanged code and no user interaction.',
        'Warm caches and fresh processes; three action warm-ups are excluded. Background OS work/frequency scaling remain.',
        'Full oracle parity and serialization run outside the timing interval. Windows measures shared commands only; Android includes presentation.'])
args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
for row in summary:
    medians=[round(row['modes'][m]['usableMs']['median'],2) for m in modes]
    print(row['operation'], 'median optimized/full/uncached',medians,
          'optimized minus full paired mean',round(row['optimizedMinusFull']['meanDeltaMs'],2),
          'interval',[round(x,2) for x in row['optimizedMinusFull']['exploratory95PercentIntervalMs']])
print('Incremental samples:',result['incrementalSamples'],'of',complete['samples']//3)

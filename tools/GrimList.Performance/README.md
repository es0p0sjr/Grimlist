# Editing performance harness

Run from the repository root on Windows with .NET 10:

```powershell
dotnet build tools/GrimList.Performance/GrimList.Performance.csproj -c Release -m:1 -nr:false
dotnet tools/GrimList.Performance/bin/Release/net10.0/GrimList.Performance.dll tests/fixtures/libraries/GrimList-Library.json
```

An optional second argument selects an output directory. Use a new directory for each run. The default creates `.local/performance-<timestamp>`.

The harness uses the real library and shared services, saves only to its disposable database, and reports individual samples as JSON. It checks zero library-blob reads, one calculation and zero availability rebuilds per equipment command. It exercises three-entry and twenty-entry Death Guard fixtures, plus cold/warm, equipment, add/remove, detachment and faction cache transitions. Unit names here identify regression workloads; they are not production performance exceptions.

The host harness measures shared commands and library lookups only. The duplicated mobile preparation probe was removed because it no longer matched the frontend. Use the isolated Android performance package below to measure actual mobile preparation and presentation. The twenty-entry army is a load fixture, not a legal-list recommendation.

Times are host wall-clock measurements. Allocations are cumulative managed bytes, not retained RAM. Command time includes saving and calculation; total adds an availability cache lookup. Old reports containing equipment/editor preparation timings describe the retired probe and are not directly comparable to current totals. Do not add nested measurements together. Source, revision, file size and runtime are recorded with each run.

## Staged performance acceptance

`--acceptance <disposable-directory> [samples=20]` measures startup/recovery and
add, equipment, enhancement, attachment and detachment commands against the
bundled real library, using three-entry and twenty-entry workloads. Run a second
process against the same directory to distinguish first bootstrap, recovery-cache
miss and cache hit. `--home <disposable-directory>` compares recalculation with
cached Home summaries for ten saved armies. `--comparison <disposable-directory>`
isolates legacy JSON comparison versus structural effective-state comparison.
Use sequential runs with no concurrent builds/tests; use multiple alternating
before/after runs and report distributions rather than a single best sample.

Android: publish with `-p:IncludePerformanceChecks=true` into a separate directory.
This builds `com.grimlist.perftest` with separate storage; it must never replace
the production APK. `scripts/Measure-AndroidPerformance.py` installs that package,
measures fresh-process starts, and runs the same workload through actual mobile
preparation/presentation. It records two-vsync UI completion, managed/native heap,
PSS/swap snapshots and graphics diagnostics. Its startup timing starts at managed
MAUI entry; external launch-to-ready polling is separately reported. Neither
measurement is interchangeable with Android TTID. The startup-only runs leave
the library installed; the first ever run includes bootstrap. A device-side
USB installation confirmation may be required.

The installed Android SDK rejects `RunAOTCompilation=true` with
`PublishTrimmed=false` (XA1030). The attempted profiled-AOT build is recorded in
`.local/staged-aot-build.log`; its unusable experimental profile was removed.
Normal release compilation and trimming remain unchanged. AOT experiments need
an isolated trimming configuration with proven serialization/reflection safety,
then comparisons using the same test package, architecture set, fixture and
device. Phone measurements and compilation changes are deferred. No AOT speedup
is established by an APK build or PC-only result.

Recovery resource generation: after `--build-effective-metadata`, run
`python scripts/Build-RecoveryIndex.py`. The canonical aggregate is build input
only. The app embeds source-indexed ZIPs and the small version manifest.

Architectural assertions also run in `tests/GrimList.Tests/PerformanceChecks.cs`; they use operation counters and failure injection rather than timing thresholds.

Run `python scripts/Summarize-Performance.py` to regenerate
`docs/performance/staged-results.json` from the retained raw stage measurements.
The report includes regressions and deferred acceptance checks, not just gains.

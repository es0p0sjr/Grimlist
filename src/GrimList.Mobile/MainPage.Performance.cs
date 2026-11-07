#if PERFORMANCE_CHECKS
using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    internal static readonly Stopwatch PerformanceClock = new();
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<LocalPerformance.Sample> StartupStages = new();
    private double performanceLibraryMs;
    private bool performanceStarted;
    private sealed class PerformanceFrame(Action action) : Java.Lang.Object, Android.Views.Choreographer.IFrameCallback
    { public void DoFrame(long nanos) { action(); } }
    private static async Task DisplayFrames()
    {
        // Wait for two vsync callbacks, allowing submitted layout/drawing to run.
        for (var i = 0; i < 2; i++)
        {
            var completion = new TaskCompletionSource();
            using var callback = new PerformanceFrame(() => completion.TrySetResult());
            Android.Views.Choreographer.Instance!.PostFrameCallback(callback);
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { Android.Views.Choreographer.Instance!.RemoveFrameCallback(callback); GC.KeepAlive(callback); }
        }
    }
    private async Task RunPerformance()
    {
        if (performanceStarted) return; performanceStarted = true;
        if (Android.App.Application.Context.PackageName != "com.grimlist.perftest") throw new InvalidOperationException("Performance checks require isolated app storage.");
        var directory = Android.App.Application.Context.GetExternalFilesDir(null)!.AbsolutePath;
        busy = true; // Benchmark owns presentation; suppress duplicate SessionChanged renders.
        try
        {
            await DisplayFrames();
            var usable = PerformanceClock.Elapsed.TotalMilliseconds;
            var memory = GC.GetTotalMemory(false);
            File.WriteAllText(Path.Combine(directory, "startup.json"), DataJson.Write(new { LibraryReadyMs = performanceLibraryMs,
                MauiEntryToUsableMs = usable, ManagedBytes = memory, AllocatedBytes = GC.GetTotalAllocatedBytes(false),
                NativeAllocatedBytes = Android.OS.Debug.NativeHeapAllocatedSize, Stages = StartupStages.ToArray(),
                RecoveryContract = LibraryRecoveryVersion.Current, ApplicationModule = typeof(ArmySession).Module.ModuleVersionId,
                FullReads = Session.FactionLoading?.FullReads, ShardReads = Session.FactionLoading?.ShardReads,
                DependencyCalculations = Session.FactionLoading?.DependencyCalculations, DependencyReuses = Session.FactionLoading?.DependencyReuses,
                MigratedManifest = Session.FactionLoading?.MigratedManifest,
                Context = new { Session.Army.Id, Session.Army.Faction, Entries = Session.Army.Entries.Count, Session.Context.Screen } }));
            // Startup-only runs use an intent extra, leaving the workload for explicit runs.
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("startupOnly", false) == true) return;
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetStringExtra("controlledMode") is { } controlledMode)
            {
                var report = await ControlledComparison.Run(Session, async operation =>
                {
                    screen = operation is "add" or "detachment" ? Screen.Editor : operation == "home" ? Screen.Home : Screen.Unit;
                    unitTab = operation == "equipment" ? "equipment" : "datasheet";
                    await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
                }, controlledMode, Microsoft.Maui.ApplicationModel.Platform.CurrentActivity.Intent.GetIntExtra("iterations", 10));
                File.WriteAllText(Path.Combine(directory, "controlled.json"), DataJson.Write(report));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("weaponMetadataChecks", false) == true)
            { await RunWeaponMetadataChecks(directory); return; }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("enhancementEvidenceChecks", false) == true)
            { await RunEnhancementEvidenceChecks(directory); return; }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("marineStratagemChecks", false) == true)
            {
                var data = await Task.Run(() => workspace!.Repository.LoadDataset()!);
                var army = ArmyList.New("Dark Angels") with { Id = "marine-stratagem-ui-check", Name = "Current Chapter stratagems", Detachment = "Wrath of the Rock" };
                var saved = workspace.Repository.LoadArmies().Where(a => a.Id != army.Id).ToList();
                await Session.RunAsync(s => s.Open(army));
                screen = Screen.Rules; await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
                var cards = references.Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
                if (cards.Count != 6 || !cards.Any(c => c.Name == "KNIGHTS OF IRON") || !cards.Any(c => c.Name == "IN SACRIFICE, VICTORY")
                    || cards.Any(c => c.Name == "ARMOUR OF CONTEMPT" || c.Source?.Date != "2026-10-14" || c.Details!.Effect.Length == 0))
                    throw new InvalidDataException("Native Rules screen did not receive the current Chapter stratagems.");
                var generic = ArmyReferences.ForArmy(ArmyList.New("Space Marines") with { Detachment = "Ironstorm Spearhead" }, data)
                    .Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
                if (generic.Count != 3) throw new InvalidDataException("Old Ironstorm cards survived Android recovery.");
                if (StratagemTiming.FromText(cards.Single(c => c.Name == "KNIGHTS OF IRON").DisplayText) != StratagemTurn.Yours)
                    throw new InvalidDataException("Wrong turn colour for Knights of Iron.");
                File.WriteAllText(Path.Combine(directory, "marine-stratagems.json"), DataJson.Write(new {
                    Cards = cards.Select(c => new { c.Name, c.Cost, c.Source }), IronstormCount = generic.Count, Armies = saved, Rendered = true }));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("armyRuleChecks", false) == true)
            {
                var data = await Task.Run(() => workspace!.Repository.LoadDataset()!);
                var army = ArmyList.New("Ultramarines") with { Id = "army-rule-ui-check", Name = "Combat Doctrines check" };
                var cards = ArmyReferences.ForArmy(army, data).Where(c => c.Section == "Army Rules").ToList();
                var rule = cards.Single(c => c.Name == "Combat Doctrines");
                if (!rule.Text.Contains("[ASSAULT]") || !rule.Text.Contains("once per battle") || cards.Any(c => c.Name == "Oath of Moment"))
                    throw new InvalidDataException("Native army rule reference failed.");
                var saved = workspace.Repository.LoadArmies().Where(a => a.Id != army.Id).ToList();
                await Session.RunAsync(s => s.Open(army));
                screen = Screen.Rules; await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
                if (references.Count(c => c.Section == "Army Rules" && c.Name == "Combat Doctrines") != 1)
                    throw new InvalidDataException("Army Rules screen did not receive the current doctrine card.");
                File.WriteAllText(Path.Combine(directory, "army-rule.json"), DataJson.Write(new {
                    Rule = rule.Name, rule.Text, rule.Source, Armies = saved, Rendered = true }));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("detachmentCatalogueChecks", false) == true)
            {
                var data = await Task.Run(() => workspace!.Repository.LoadDataset()!);
                var options = DetachmentRules.Options("Ultramarines", data);
                if (data.Detachments!.Any(d => DetachmentCatalogue.IsRetired(data, d.Faction, d.Name))
                    || !options.Any(d => d.Name == "Assault Brethren") || options.Any(d => d.Name == "Vow-Sworn Crusaders"))
                    throw new InvalidDataException("Native detachment catalogue or Chapter restrictions failed.");
                var legacyArmy = ArmyList.New("Ultramarines") with { Detachment = "Anvil Siege Force" };
                if (!DetachmentRules.Issues(legacyArmy, data).Any(s => s.Contains("retired after")))
                    throw new InvalidDataException("Retired selection diagnostic missing.");
                var supplemented = ReferenceLibraryRecovery.Supplement(data, ReferenceLibraryRecovery.ReadBundled());
                if (supplemented.Detachments!.Any(d => DetachmentCatalogue.IsRetired(supplemented, d.Faction, d.Name)))
                    throw new InvalidDataException("Android supplement restored retired data.");
                File.WriteAllText(Path.Combine(directory, "detachment-catalogue.json"), DataJson.Write(new {
                    Checks = new[] { "No retired definitions", "Core option available", "Chapter restrictions retained", "Retired army diagnostic", "Android supplement cannot resurrect retired entries" },
                    SpaceMarineDefinitions = data.Detachments.Count(d => d.Faction == "Space Marines"),
                    UltramarineOptions = options.Select(d => d.Name), Armies = workspace!.Repository.LoadArmies() }));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("importStorageChecks", false) == true)
            {
                await RunImportStorageChecks(directory, Microsoft.Maui.ApplicationModel.Platform.CurrentActivity.Intent.GetBooleanExtra("importStorageReopen", false));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("incrementalEvaluationChecks", false) == true)
            {
                List<string> checks = [], measurements = [];
                await Task.Run(() => IncrementalEvaluationWorkload.Run((name, check) => { check(); checks.Add(name); }, measurements.Add));
                File.WriteAllText(Path.Combine(directory, "incremental-evaluation.json"), DataJson.Write(new { Checks = checks, Measurements = measurements }));
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("catalogueStorageChecks", false) == true)
            {
                var temporary = Path.Combine(FileSystem.Current.CacheDirectory, "catalogue-storage-" + Guid.NewGuid().ToString("N"));
                await Task.Run(() => CatalogueStorageWorkload.Run(temporary, new AndroidSqliteConnectionFactory()));
                var checks = await Task.Run(() => CatalogueWorkflowWorkload.Run(Path.Combine(temporary, "workflow"), new AndroidSqliteConnectionFactory()));
                var report = File.ReadAllText(Path.Combine(temporary, "catalogue-storage.json"));
                File.WriteAllText(Path.Combine(directory, "catalogue-workflow.json"), DataJson.Write(new { Checks = checks }));
                GC.Collect(); GC.WaitForPendingFinalizers();
                Directory.Delete(temporary, true);
                File.WriteAllText(Path.Combine(directory, "catalogue-storage.json"), report);
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("factionLoadingChecks", false) == true)
            {
                var report = await Task.Run(() => FactionLoadingWorkload.Run(workspace!.Repository,
                    r => ReferenceLibraryRecovery.OpenLibrary(r).Library, ReferenceLibraryRecovery.RecoveryVersion));
                File.WriteAllText(Path.Combine(directory, "faction-loading.json"), DataJson.Write(report));
                await RunUnitSizeChecks(directory);
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("unitSizeChecks", false) == true)
            { await RunUnitSizeChecks(directory); return; }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("attachmentChecks", false) == true)
            { await RunAttachmentChecks(directory); return; }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("enhancementChoiceChecks", false) == true)
            { await RunEnhancementChoiceChecks(directory); return; }
            var presentation = new List<object>();
            var samples = await AcceptanceWorkload.Run(Session, async operation =>
            {
                screen = operation is "add" or "detachment" ? Screen.Editor : operation == "home" ? Screen.Home : Screen.Unit;
                unitTab = operation == "equipment" ? "equipment" : "datasheet";
                var clock = Stopwatch.StartNew(); var builds = FullPageBuildCount;
                await PrepareAsync(); var prepared = clock.Elapsed.TotalMilliseconds;
                RefreshPresentation(); var rendered = clock.Elapsed.TotalMilliseconds;
                await DisplayFrames();
                presentation.Add(new { Operation = operation, PrepareMs = prepared, RenderMs = rendered - prepared,
                    FrameWaitMs = clock.Elapsed.TotalMilliseconds - rendered, FullPageBuilds = FullPageBuildCount - builds });
            }, Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetIntExtra("iterations", 20) ?? 20);
            File.WriteAllText(Path.Combine(directory, "presentation.json"), DataJson.Write(presentation));
            File.WriteAllText(Path.Combine(directory, "performance.json"), DataJson.Write(samples));
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "performance-error.txt"), error.ToString()); }
        finally { busy = false; }
    }
}
#endif

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GrimList.Core;
using GrimList.Application;
using GrimList.Platform;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckImportResponsiveness(MainWindow owner, string directory, string imagePath)
    {
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "async-import-" + Guid.NewGuid().ToString("N")));
        var original = new Dataset(new("Async import fixture", "1", "", "", Edition: "11"),
            [new("Death Guard", "Test unit", [new(5, 100)], Profile: new(Notes: "Retain datasheet"))]);
        store.Import(original);
        var repository = new ImportProbe(store, Environment.CurrentManagedThreadId);
        var refreshCount = 0;
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var import = new ImportWindow(repository, initialLayer: ImportLayer.Combined, refreshLibrary: async progress =>
        {
            refreshCount++;
            progress.Report("Finishing update · Checking saved armies…");
            if (refreshCount == 1)
            {
                refreshEntered.SetResult();
                await refreshRelease.Task;
                throw new IOException("Injected refresh failure");
            }
            await Task.Run(() => new ArmySession(store, useFactionLoading: true));
        }) { Owner = owner };
        var incoming = original with { Source = original.Source with { Version = "2" },
            Units = [original.Units[0] with { Options = [new(5, 125)] }] };
        Find<TextBox>(import, "RawText").Text = DataJson.Write(incoming);
        Find<TextBox>(import, "VersionBox").Text = "Reviewed version 2";
        void Start(string name) => Find<Button>(import, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Responsive(string stage)
        {
            var heartbeat = false;
            await import.Dispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
            Check(heartbeat && Find<ProgressBar>(import, "ImportProgress").IsIndeterminate, stage + " blocked the dispatcher or hid progress");
            Check(!Find<Button>(import, "ApplyButton").IsEnabled && !Find<Button>(import, "DownloadButton").IsEnabled, stage + " allows overlapping updates");
            Check(Find<TextBlock>(import, "Summary").Text.Length > 0, stage + " has no status");
        }
        Exception? failure = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        import.ContentRendered += async (_, _) =>
        {
            try
            {
                using (var gate = new ImportGate())
                {
                    repository.ReadGate = gate;
                    Start("PreviewButton");
                    try
                    {
                        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await Responsive("Preview");
                        Start("StopButton");
                    }
                    finally { gate.Release.Set(); }
                    await import.PendingOperation;
                    repository.ReadGate = null;
                }
                Check(!Find<Button>(import, "ApplyButton").IsEnabled && Find<Button>(import, "PreviewButton").IsEnabled, "Cancelled preview left stale apply or disabled controls");
                Check(store.LoadDataset()!.Source.Version == "1", "Preview changed the installed library");
                Check(Find<TextBox>(import, "EditionBox").Text == "11" && Find<TextBox>(import, "VersionBox").Text == "Reviewed version 2",
                    "Deferred metadata parsing lost the pasted edition or the user's version edit");
                Start("PreviewButton"); await import.PendingOperation;
                Check(Find<Button>(import, "ApplyButton").IsEnabled, Find<TextBlock>(import, "Summary").Text);

                // A source replaced after review must still fail its transactional revision check.
                store.ApplyImport(original with { Source = original.Source with { Version = "external" } });
                Start("ApplyButton"); await import.PendingOperation;
                Check(Find<TextBlock>(import, "Summary").Text.Contains("changed since preview") && !Find<Button>(import, "ApplyButton").IsEnabled,
                    "Async apply lost stale-preview protection");
                Start("PreviewButton"); await import.PendingOperation;
                repository.SuccessfulApplies = 0;
                using (var gate = new ImportGate())
                {
                    repository.SaveGate = gate;
                    Start("ApplyButton");
                    var pending = import.PendingOperation;
                    try
                    {
                        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await Responsive("Save");
                        Check(!Find<Button>(import, "CancelButton").IsEnabled && Find<Button>(import, "StopButton").Visibility == Visibility.Collapsed,
                            "Save offers cancellation during the transaction");
                        Start("ApplyButton");
                        Check(ReferenceEquals(pending, import.PendingOperation), "Duplicate Apply replaced the active operation");
                        import.Close();
                        Check(import.IsVisible, "Dialog closed before its save finished");
                    }
                    finally { gate.Release.Set(); }
                    await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await Responsive("Library reload");
                    Capture(import, imagePath + ".progress.png");
                    refreshRelease.SetResult();
                    await pending;
                    repository.SaveGate = null;
                }
                Check(repository.SuccessfulApplies == 1 && store.LoadDataset()!.Units[0].Options[0].Points == 125, "Save failed or ran twice");
                Check(Find<TextBlock>(import, "Summary").Text.StartsWith("Update saved") && Find<Button>(import, "ApplyButton").IsEnabled,
                    "Refresh failure misreported the successful commit or prevented retry");
                Check(!Find<Button>(import, "PreviewButton").IsEnabled, "Committed update permits changing its source during refresh retry");
                Start("ApplyButton"); await import.PendingOperation;
                Check(import.DialogResult == true && refreshCount == 2 && repository.SuccessfulApplies == 1, "Refresh retry duplicated the import");
            }
            catch (Exception ex)
            {
                failure = ex;
                repository.ReadGate?.Release.Set(); repository.SaveGate?.Release.Set(); refreshRelease.TrySetResult();
                await import.PendingOperation;
                if (import.IsVisible) import.DialogResult = false;
            }
            finally { finished.TrySetResult(); }
        };
        import.ShowDialog();
        PumpUntil(finished.Task);
        if (failure != null) throw failure;
        File.WriteAllText(imagePath + ".async-import.txt", "PASS: responsive preview/save/reload, visible stages, cancellation, stale revision rejection, duplicate-apply and close protection, refresh failure and retry without a duplicate commit.");
    }

    public static void CheckFullLibraryImport(MainWindow owner, string directory, string imagePath)
    {
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "full-import-" + Guid.NewGuid().ToString("N")));
        var session = new ArmySession(store, useFactionLoading: true);
        var samples = new List<object>();
        // Exercise both first installation and replacement of an existing full library.
        foreach (var pass in new[] { "initial", "replacement" })
        {
            var import = new ImportWindow(store, initialLayer: ImportLayer.Combined, refreshLibrary: async progress =>
            {
                progress.Report("Finishing update · Rebuilding the army library…");
                await session.RunAsync(s => { s.ReloadLibrary(); s.LoadArmySummaries(); });
            }) { Owner = owner };
            Exception? failure = null;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            import.ContentRendered += async (_, _) =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var ticks = 0; double last = 0, longest = 0;
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; longest = Math.Max(longest, now - last); last = now; ticks++; };
                async Task Measure(string stage, Func<Task> action)
                {
                    ticks = 0; longest = 0; last = 0; clock.Restart(); timer.Start();
                    try
                    {
                        await action();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds - last);
                        samples.Add(new { Pass = pass, Stage = stage, ElapsedMs = clock.Elapsed.TotalMilliseconds, DispatcherTicks = ticks, LongestDispatcherGapMs = longest });
                    }
                    finally { timer.Stop(); }
                }
                try
                {
                    await Measure("read", () => import.LoadDocumentAsync(Path.Combine("tests", "fixtures", "libraries", "GrimList-Library.json")));
                    Check(Find<TextBox>(import, "RawText").Text.Length == 0, "Full library was copied into the text editor");
                    await Measure("preview", () => { Find<Button>(import, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return import.PendingOperation; });
                    Check(Find<Button>(import, "ApplyButton").IsEnabled, Find<TextBlock>(import, "Summary").Text);
                    Capture(import, imagePath + "." + pass + ".png");
                    await Measure("apply-and-reload", () => { Find<Button>(import, "ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return import.PendingOperation; });
                    Check(import.DialogResult == true, Find<TextBlock>(import, "Summary").Text);
                }
                catch (Exception ex) { failure = ex; if (import.IsVisible) import.DialogResult = false; }
                finally { timer.Stop(); finished.TrySetResult(); }
            };
            import.ShowDialog();
            PumpUntil(finished.Task);
            if (failure != null) throw failure;
        }
        File.WriteAllText(imagePath + ".full-import.json", DataJson.Write(samples));
        File.WriteAllText(imagePath + ".full-import.txt", "PASS: full bundled library read, preview, apply and faction-cache rebuild, for initial installation and replacement; no full JSON text-box copy.");
    }

    private sealed class ImportGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Wait() { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Import test gate timed out"); }
        public void Dispose() => Release.Dispose();
    }
    private sealed class ImportProbe(IArmyRepository inner, int uiThread) : IArmyRepository
    {
        public ImportGate? ReadGate, SaveGate;
        public int SuccessfulApplies;
        public string DirectoryPath => inner.DirectoryPath;
        public Dataset? LoadDataset() => LoadLibrary().Dataset;
        public LoadedLibrary LoadLibrary()
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Library read ran on the UI thread");
            ReadGate?.Wait(); return inner.LoadLibrary();
        }
        public void ApplyImportAtRevision(Dataset data, string revision)
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Library save ran on the UI thread");
            SaveGate?.Wait(); inner.ApplyImportAtRevision(data, revision); SuccessfulApplies++;
        }
        public void ApplyImport(Dataset data, string? expectedSnapshot = null) => throw new Exception("Revision check was bypassed");
        public List<ArmyList> LoadArmies() => inner.LoadArmies();
        public void Save(ArmyList army, EditorContext? context = null) => inner.Save(army, context);
        public void Delete(string id) => inner.Delete(id);
        public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => inner.PreviewImport(data, layer, completeFactions);
        public EditorContext LoadContext() => inner.LoadContext();
        public void SaveContext(EditorContext context) => inner.SaveContext(context);
        public void Dispose() { }
    }
}

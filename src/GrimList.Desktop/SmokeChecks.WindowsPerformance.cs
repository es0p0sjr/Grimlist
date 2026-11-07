using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    public static void CheckWindowsPerformance(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "attachment-combination-fixture.json")));
        using var store = WindowsServices.OpenStore(Path.Combine(root, "windows-performance-ui-" + Guid.NewGuid().ToString("N")));
        store.Import(fixture);
        var army = ArmyList.New("Death Guard") with { Name = "Deferred attachment regression", Entries =
            new[] { "Plague Marines", "Malignant Plaguecaster", "Biologus Putrifier" }.Select((name, i) =>
            {
                var unit = fixture.Units.Single(u => u.Name == name);
                return new ArmyEntry(i.ToString(), unit.Key, unit.Name, i == 0 ? 10 : 1);
            }).ToList() };
        store.Save(army); store.Save(army.Duplicate());
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var session = (ArmySession)typeof(MainWindow).GetField("session", flags)!.GetValue(window)!;
        var cache = (Dictionary<string, Task<List<ArmyEntry>>>)typeof(MainWindow).GetField("attachmentChoices", flags)!.GetValue(window)!;
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void View(string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
        DeferredAttachmentPicker Picker() => Descendants<DeferredAttachmentPicker>(Find<StackPanel>(window, "AttachmentEditor")).Single();
        Task<List<ArmyEntry>> Choices()
        {
            OpenAttachmentPicker(Picker());
            var id = session.Context.EntryId!;
            Check(cache.ContainsKey(id), "Opening the attachment picker did not request choices");
            return cache[id];
        }
        try
        {
            Invoke("OpenRoster", army); View("1");
            Check(cache.Count == 0, "Viewing a unit eagerly calculated attachment choices");
            var task = Choices();
            Check(task.Result.Any(e => e.Id == "0"), "Deferred leader picker omitted its bodyguard");
            Invoke("RefreshDetails"); window.UpdateLayout();
            Check(ReferenceEquals(task, Choices()), "Unchanged refresh repeated attachment calculation");

            // An in-flight result must not write into a picker that has been
            // replaced by navigation, an edit, undo/redo, or a library reload.
            foreach (var transition in new Action[] {
                () => View("0"),
                () => { session.Attach("2", "0"); Invoke("Render"); },
                () => { session.Undo(); Invoke("Render"); },
                () => { session.Redo(); Invoke("Render"); },
                () => { session.ReloadLibrary(); Invoke("Render"); } })
            {
                View("1");
                Invoke("RefreshDetails"); window.UpdateLayout();
                var old = Picker();
                var oldItems = old.ItemsSource;
                var pending = new TaskCompletionSource<List<ArmyEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
                cache["1"] = pending.Task;
                old.IsDropDownOpen = true;
                PumpUntil(old.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task);
                Check(!old.Loading.IsCompleted, "Attachment request did not remain asynchronous");
                old.IsDropDownOpen = false;
                var beforeArmy = session.Army; var beforeData = session.Dataset;
                transition(); window.UpdateLayout();
                if (!ReferenceEquals(beforeArmy, session.Army) || !ReferenceEquals(beforeData, session.Dataset))
                    Check(!cache.TryGetValue("1", out var stale) || !ReferenceEquals(stale, pending.Task), "Snapshot change retained an in-flight cache entry");
                pending.SetResult([session.Army.Entries.Single(e => e.Id == "0")]);
                PumpUntil(old.Loading);
                Check(ReferenceEquals(old.ItemsSource, oldItems), "Stale attachment result rewrote a replaced picker");
                View("1");
                if (cache.TryGetValue("1", out var remaining) && ReferenceEquals(remaining, pending.Task))
                    cache.Remove("1"); // Context-only navigation legitimately retains its snapshot cache.
                var refreshed = Choices();
                Check(!ReferenceEquals(refreshed, task), "Changed snapshot retained stale candidates");
                task = refreshed;
            }

            var rosterItems = Find<ListBox>(window, "Roster").ItemsSource;
            var mutation = session.Mutation.Version;
            Invoke("CommitFields", true);
            Check(ReferenceEquals(rosterItems, Find<ListBox>(window, "Roster").ItemsSource)
                && session.Mutation.Version == mutation, "Unchanged metadata refreshed or mutated the editor");
            Find<TextBox>(window, "ArmyName").Text = "Saved while leaving";
            Invoke("GoHome", window, new RoutedEventArgs());
            Check(store.LoadArmies().Single(a => a.Id == army.Id).Name == "Saved while leaving", "Leaving Home lost changed metadata");
            var count = session.SummaryCalculationCount;
            Invoke("RefreshHome");
            Check(session.SummaryCalculationCount == count, "Windows Home recalculated unchanged armies");
            var home = Find<HomeView>(window, "Home");
            Find<TextBox>(home, "HomeSearch").Text = "Saved while leaving";
            Check(Find<WrapPanel>(home, "TilePanel").Children.OfType<Grid>().Count() == 1, "Cached Home search did not filter armies");
            Capture(window, imagePath + ".windows-performance.png");
            File.WriteAllText(imagePath + ".windows-performance.txt", "PASS: deferred choices, snapshot reuse, asynchronous stale-result rejection for navigation/edit/undo/redo/import, metadata commit and cached Home search.");
        }
        finally { window.Close(); }
    }
}

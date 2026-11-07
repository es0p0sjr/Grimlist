#if PERFORMANCE_CHECKS
using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private async Task RunEnhancementChoiceChecks(string directory)
    {
        List<string> checks = []; List<object> timings = [];
        void Check(bool value, string message)
        { if (!value) throw new InvalidDataException(message); checks.Add(message); }
        var data = Session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        var leader = data.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Lord of Contagion");
        var guard = data.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Deathshroud Terminators");
        const string bearer = "enhancement-choice-leader", bodyguard = "enhancement-choice-guard";
        var roster = ArmyList.New("Death Guard") with { Id = "enhancement-choice-checks", Limit = 2000,
            Detachments = ["Virulent Vectorium"], Entries = [new(bearer, leader.Key, leader.Name, 1, Loadout: Loadouts.Create(leader, 1)),
                new(bodyguard, guard.Key, guard.Name, 3, Loadout: Loadouts.Create(guard, 3))] };
        busy = true;
        async Task Show()
        {
            await Session.RunAsync(s => s.SelectEntry(bearer));
            screen = Screen.Unit; playMode = false; previewKey = ""; unitTab = "datasheet";
            await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
        }
        (VerticalStackLayout Body, VerticalStackLayout Options, Button Button) Controls()
        {
            var body = datasheetRegions["enhancements"].Body;
            return (body, (VerticalStackLayout)body.Children.Last(), body.Children.OfType<Button>().Single(b => b.AutomationId == "choose-enhancement"));
        }
        Task Load()
        {
            var ui = Controls(); busy = false;
            var task = LoadEnhancementChoices(ui.Body, ui.Options, ui.Button, Session.Army, Session.Dataset!, bearer);
            busy = true; return task;
        }
        for (var i = -1; i < 5; i++)
        {
            await Session.RunAsync(s => s.Open(roster)); await Show();
            Check(enhancementChoices.Count == 0, "Datasheet does not calculate enhancement choices");
            var timer = Stopwatch.StartNew(); var loading = Load();
            await DisplayFrames(); var frames = timer.Elapsed.TotalMilliseconds;
            var sessionTimer = Stopwatch.StartNew(); await Session.RunAsync(_ => { }); var sessionWait = sessionTimer.Elapsed.TotalMilliseconds;
            await loading; await DisplayFrames(); var cold = timer.Elapsed.TotalMilliseconds;
            var original = enhancementChoices[bearer];
            Check(!Controls().Button.IsVisible && Controls().Options.Children.Count > 0, "Native enhancement chooser rendered");
            foreach (var option in original.Result.Options)
            {
                string? rejection = null;
                if (!option.Selected) try { Enhancements.Select(Session.Army, Session.Dataset!, bearer, new(option.Card.Detachment, option.Card.Name)); }
                    catch (InvalidDataException error) { rejection = error.Message; }
                Check(rejection == option.Rejection, "Prepared enhancement matches authoritative acceptance");
            }
            Render(); await DisplayFrames(); timer.Restart(); await Load(); await DisplayFrames();
            Check(ReferenceEquals(original, enhancementChoices[bearer]), "Repeated chooser reuses snapshot calculation");
            if (i >= 0) timings.Add(new { ColdMs = cold, WarmMs = timer.Elapsed.TotalMilliseconds, FramesWhileLoadingMs = frames, SessionWaitMs = sessionWait });
        }
        foreach (var change in new[] { "navigation", "attachment", "undo", "redo", "enhancement", "library" })
        {
            await Session.RunAsync(s => s.Open(roster)); await Show();
            if (change == "undo") { await Session.RunAsync(s => s.Attach(bearer, bodyguard)); await Show(); }
            if (change == "redo") { await Session.RunAsync(s => { s.Attach(bearer, bodyguard); s.Undo(); }); await Show(); }
            var stale = Controls(); var army = Session.Army; var library = Session.Dataset!;
            var pending = new TaskCompletionSource<EnhancementPresentation>(); enhancementChoices[bearer] = pending.Task;
            var loading = Load();
            await Session.RunAsync(s =>
            {
                switch (change)
                {
                    case "navigation": s.SelectEntry(bodyguard); break;
                    case "attachment": s.Attach(bearer, bodyguard); break;
                    case "undo": s.Undo(); break;
                    case "redo": s.Redo(); break;
                    case "enhancement": var card = Enhancements.Options(s.Army, s.Dataset, leader).First(); s.SelectEnhancement(bearer, new(card.Detachment, card.Name)); break;
                    case "library": s.ReloadLibrary(); break;
                }
            });
            await PrepareAsync(); RefreshPresentation();
            pending.SetResult(EnhancementPresentation.Prepare(army, library, bearer, true)); await loading;
            Check(stale.Options.Children.Count == 0, "Pending enhancement result ignored after " + change);
            if (change != "navigation") Check(!enhancementChoices.ContainsKey(bearer), "Enhancement cache invalidated after " + change);
        }
        await Session.RunAsync(s => s.Open(roster)); await Show();
        var retry = Controls(); var failed = new TaskCompletionSource<EnhancementPresentation>(); enhancementChoices[bearer] = failed.Task;
        var failure = Load(); failed.SetException(new InvalidDataException("Test retry")); await failure;
        Check(retry.Button.IsEnabled && retry.Button.IsVisible, "Failed enhancement calculation permits retry");
        await Load(); Check(!retry.Button.IsVisible, "Retry replaces failed enhancement calculation");
        var choice = enhancementChoices[bearer].Result.Options.First(o => o.Rejection == null && !o.Selected);
        var button = retry.Options.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>())
            .Single(b => b.AutomationId == "enhancement-" + choice.Card.Name);
        await Session.RunAsync(s => s.SelectEntry(bodyguard));
        busy = false; ((IButtonController)button).SendClicked();
        for (var attempt = 0; attempt < 200 && busy; attempt++) await Task.Delay(50);
        Check(Session.Army.Entries.All(e => e.Enhancement == null), "Old enhancement button cannot edit a previous unit");
        busy = true; await Show(); await Load();
        button = Controls().Options.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>())
            .Single(b => b.AutomationId == "enhancement-" + choice.Card.Name);
        busy = false; ((IButtonController)button).SendClicked();
        for (var attempt = 0; attempt < 200 && (busy || Session.SelectedEntry?.Enhancement == null); attempt++) await Task.Delay(50);
        Check(Session.SelectedEntry?.Enhancement?.Name == choice.Card.Name, "Native button applies enhancement through session");
        busy = true; await Show();
        Check(unitView!.EnhancementSummary?.SelectedCard?.Name == choice.Card.Name && enhancementChoices.Count == 0,
            "Selected enhancement remains visible without preparing options");
        File.WriteAllText(Path.Combine(directory, "enhancement-choice-checks.json"), DataJson.Write(new { Checks = checks, Timings = timings }));
    }
}
#endif

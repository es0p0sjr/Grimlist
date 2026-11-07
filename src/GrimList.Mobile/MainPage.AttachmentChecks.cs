#if PERFORMANCE_CHECKS
using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    // Invoked only by the isolated performance package, never by the user's app.
    private async Task RunAttachmentChecks(string directory)
    {
        List<string> checks = [];
        List<object> timings = [];
        void Check(bool pass, string name)
        { if (!pass) throw new InvalidOperationException(name); checks.Add(name); }
        var data = Session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        UnitDefinition Unit(string name) => data.Units.Single(u => u.Faction == "Death Guard" && u.Name == name);
        ArmyEntry Entry(UnitDefinition u, int size, string id) => new(id, u.Key, u.Name, size, Loadout: Loadouts.Create(u, size));
        var marine = Unit("Plague Marines"); var guard = Unit("Deathshroud Terminators"); var leader = Unit("Lord of Contagion");
        var roster = ArmyList.New("Death Guard") with { Id = "attachment-checks", Limit = 2000, Detachments = ["Virulent Vectorium"],
            Entries = [Entry(marine, 10, "attachment-marines"), Entry(guard, 3, "attachment-guard"), Entry(leader, 1, "attachment-leader")] };
        roster = roster with { Entries = [.. roster.Entries, .. data.Units.Where(u => u.Faction == "Death Guard" && !u.Name.Contains("variant")
            && !PricingRepair.IsCrucible(u) && u.Options.Count > 0 && roster.Entries.All(e => e.UnitKey != u.Key)).Take(17)
            .Select((u, i) => Entry(u, u.Options[0].Models, "attachment-extra" + i))] };
        busy = true;
        async Task Show(string entryId = "attachment-leader")
        {
            await Session.RunAsync(s => s.SelectEntry(entryId));
            screen = Screen.Unit; playMode = false; previewKey = ""; unitTab = "datasheet";
            await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
        }
        (VerticalStackLayout Body, VerticalStackLayout Choices, Button Button) Controls()
        {
            var body = datasheetRegions["attachments"].Body;
            return (body, (VerticalStackLayout)body.Children.Last(), body.Children.OfType<Button>().Single(b => b.AutomationId == "choose-attachment"));
        }
        Task Load()
        {
            var ui = Controls(); busy = false;
            var task = LoadAttachmentChoices(ui.Body, ui.Choices, ui.Button, Session.Army, Session.Dataset, Session.Context.EntryId!,
                LeaderRules.IsLeader(unitView!.Unit), "Choose attachment");
            busy = true; return task;
        }
        for (var i = -1; i < 5; i++)
        {
            await Session.RunAsync(s => s.Open(roster)); await Show();
            Check(attachmentChoices.Count == 0, "Datasheet does not calculate attachment choices");
            var clock = Stopwatch.StartNew(); var loading = Load();
            await DisplayFrames(); var framesWhileLoading = clock.Elapsed.TotalMilliseconds;
            var sessionClock = Stopwatch.StartNew(); await Session.RunAsync(_ => { }); var sessionWait = sessionClock.Elapsed.TotalMilliseconds;
            await loading; await DisplayFrames(); var cold = clock.Elapsed.TotalMilliseconds;
            var originalTask = attachmentChoices["attachment-leader"];
            await Session.RunAsync(s => Check(originalTask.Result.Select(e => e.Id).SequenceEqual(s.AttachmentCandidates("attachment-leader").Select(e => e.Id)),
                "Deferred choices match full session rules"));
            var ui = Controls(); Check(!ui.Button.IsVisible && ui.Choices.Children.OfType<Button>().Any(b => b.AutomationId == "attach-attachment-guard"), "Native chooser offers bodyguard");
            Render(); await DisplayFrames(); clock.Restart(); await Load(); await DisplayFrames(); var warm = clock.Elapsed.TotalMilliseconds;
            Check(ReferenceEquals(originalTask, attachmentChoices["attachment-leader"]), "Repeated chooser reuses calculation");
            if (i >= 0) timings.Add(new { ColdMs = cold, WarmMs = warm, FramesWhileLoadingMs = framesWhileLoading, SessionWaitMs = sessionWait });
        }
        foreach (var change in new[] { "navigation", "attachment", "undo", "redo", "enhancement", "library" })
        {
            await Session.RunAsync(s => s.Open(roster)); await Show();
            if (change == "undo") { await Session.RunAsync(s => s.Attach("attachment-leader", "attachment-guard")); await Show(); }
            if (change == "redo") { await Session.RunAsync(s => { s.Attach("attachment-leader", "attachment-guard"); s.Undo(); }); await Show(); }
            var stale = Controls(); var pending = new TaskCompletionSource<List<ArmyEntry>>();
            var pageBuilds = FullPageBuildCount;
            var previousDatasheet = datasheetBody;
            attachmentChoices["attachment-leader"] = pending.Task;
            var loading = Load();
            await Session.RunAsync(s =>
            {
                switch (change)
                {
                    case "navigation": s.SelectEntry("attachment-marines"); break;
                    case "attachment": s.Attach("attachment-leader", "attachment-guard"); break;
                    case "undo": s.Undo(); break;
                    case "redo": s.Redo(); break;
                    case "enhancement": var card = Enhancements.Options(s.Army, s.Dataset, leader).First(); s.SelectEnhancement("attachment-leader", new(card.Detachment, card.Name)); break;
                    case "library": s.ReloadLibrary(); break;
                }
            });
            await PrepareAsync(); RefreshPresentation();
            if (change is "attachment" or "undo" or "redo" or "enhancement")
                Check(FullPageBuildCount == pageBuilds && ReferenceEquals(previousDatasheet, datasheetBody), "Datasheet retained after " + change);
            pending.SetResult([roster.Entries.Single(e => e.Id == "attachment-guard")]); await loading;
            Check(stale.Choices.Children.Count == 0, "Pending result ignored after " + change);
            if (change != "navigation") Check(!attachmentChoices.ContainsKey("attachment-leader"), "Cache invalidated after " + change);
            await Show();
        }
        await Session.RunAsync(s => s.Open(roster)); await Show();
        var retry = Controls(); var failed = new TaskCompletionSource<List<ArmyEntry>>(); attachmentChoices["attachment-leader"] = failed.Task;
        var failure = Load(); failed.SetException(new InvalidDataException("Test retry")); await failure;
        Check(retry.Button.IsEnabled && retry.Button.IsVisible, "Failed calculation permits retry");
        await Load(); Check(!retry.Button.IsVisible, "Retry replaces faulted calculation");
        File.WriteAllText(Path.Combine(directory, "attachment-checks.json"), DataJson.Write(new { Checks = checks, Timings = timings }));
    }
}
#endif

#if PERFORMANCE_CHECKS
using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private async Task RunEnhancementEvidenceChecks(string directory)
    {
        var scenarios = new Dictionary<string, EnhancementAcceptance.Scenario>();
        var report = await Task.Run(() => EnhancementAcceptance.Run(s => scenarios.TryAdd(s.Army.Faction, s)));
        List<string> rendered = [];
        foreach (var scenario in scenarios.Values)
        {
            const string bearer = "enhancement-evidence-bearer";
            await Session.RunAsync(s => { s.Open(scenario.Army with { Id = "enhancement-native-check", Name = "Enhancement acceptance",
                Entries = scenario.Army.Entries.Select(e => e with { Id = e.Id == "bearer" ? bearer : "enhancement-evidence-" + e.Id,
                    AttachedToId = e.AttachedToId == "bearer" ? bearer : e.AttachedToId == null ? null : "enhancement-evidence-" + e.AttachedToId }).ToList(),
                WarlordEntryId = scenario.Army.WarlordEntryId == "bearer" ? bearer : scenario.Army.WarlordEntryId }); s.SelectEntry(bearer); });
            screen = Screen.Unit; playMode = false; previewKey = ""; unitTab = "datasheet";
            await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
            var body = datasheetRegions["enhancements"].Body;
            var options = (VerticalStackLayout)body.Children.Last();
            var chooser = body.Children.OfType<Button>().Single(b => b.AutomationId == "choose-enhancement");
            busy = false;
            await LoadEnhancementChoices(body, options, chooser, Session.Army, Session.Dataset!, bearer);
            busy = true;
            var button = options.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>())
                .Single(b => b.AutomationId == "enhancement-" + scenario.Card.Name);
            if (!button.IsEnabled) throw new InvalidDataException("Native picker disabled eligible enhancement: " + scenario.Card.Name);
            busy = false;
            ((IButtonController)button).SendClicked();
            for (var attempt = 0; attempt < 200 && (busy || Session.SelectedEntry?.Enhancement == null); attempt++) await Task.Delay(50);
            if (Session.SelectedEntry?.Enhancement?.Name != scenario.Card.Name) throw new InvalidDataException("Native selection failed.");
            busy = true;
            await Session.RunAsync(s => s.Undo());
            if (Session.SelectedEntry?.Enhancement != null) throw new InvalidDataException("Native selection undo failed.");
            await Session.RunAsync(s => s.Redo());
            if (Session.SelectedEntry?.Enhancement?.Name != scenario.Card.Name) throw new InvalidDataException("Native selection redo failed.");
            rendered.Add(scenario.Army.Faction);
        }
        if (rendered.Count < 20) throw new InvalidDataException("Native picker coverage is incomplete.");
        File.WriteAllText(Path.Combine(directory, "enhancement-evidence.json"), DataJson.Write(new { report, NativePickerFactions = rendered }));
    }
}
#endif

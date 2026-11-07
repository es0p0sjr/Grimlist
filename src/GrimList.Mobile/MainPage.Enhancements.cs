using GrimList.Application;
using GrimList.Core;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private ArmyList? enhancementChoiceArmy;
    private Dataset? enhancementChoiceData;
    private readonly Dictionary<string, Task<EnhancementPresentation>> enhancementChoices = [];
    private void InvalidateEnhancementChoices()
    {
        if (ReferenceEquals(Session.Army, enhancementChoiceArmy) && ReferenceEquals(Session.Dataset, enhancementChoiceData)) return;
        enhancementChoices.Clear(); enhancementChoiceArmy = Session.Army; enhancementChoiceData = Session.Dataset;
    }
    private bool EnhancementChooserCurrent(VerticalStackLayout body, ArmyList army, Dataset data, string entry)
        => ReferenceEquals(Session.Army, army) && ReferenceEquals(Session.Dataset, data) && screen == Screen.Unit
            && !playMode && previewKey.Length == 0 && unitTab == "datasheet" && Session.Context.EntryId == entry
            && datasheetRegions.TryGetValue("enhancements", out var region) && ReferenceEquals(region.Body, body);
    private void AddEnhancementChooser(VerticalStackLayout body, string entry)
    {
        var army = Session.Army; var data = Session.Dataset!;
        var options = new VerticalStackLayout { Spacing = 10 };
        Button? button = null;
        button = Ui.Button("Choose enhancement / upgrade", async () => await LoadEnhancementChoices(body, options, button!, army, data, entry),
            "choose-enhancement", Accent);
        body.Add(button); body.Add(options);
    }
    private async Task LoadEnhancementChoices(VerticalStackLayout body, VerticalStackLayout options, Button button,
        ArmyList army, Dataset data, string entry)
    {
        if (busy || !button.IsEnabled || !EnhancementChooserCurrent(body, army, data, entry)) return;
        button.IsEnabled = false; button.Text = "Loading enhancement choices…";
        try
        {
            if (!enhancementChoices.TryGetValue(entry, out var task) || task.IsFaulted || task.IsCanceled)
                enhancementChoices[entry] = task = Task.Run(() => EnhancementPresentation.Prepare(army, data, entry, true));
            var result = await task;
            if (!EnhancementChooserCurrent(body, army, data, entry)) return;
            options.Clear(); RenderEnhancementOptions(options, result, army, data, entry); button.IsVisible = false;
        }
        catch (Exception error)
        {
            if (!EnhancementChooserCurrent(body, army, data, entry)) return;
            options.Clear(); options.Add(Ui.Text("Could not load enhancement choices. " + error.Message, 14, colour: Ui.Muted));
            button.Text = "Choose enhancement / upgrade"; button.IsEnabled = true;
        }
    }
}

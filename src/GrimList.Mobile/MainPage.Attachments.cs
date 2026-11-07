using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private ArmyList? attachmentChoiceArmy;
    private Dataset? attachmentChoiceData;
    private readonly Dictionary<string, Task<List<ArmyEntry>>> attachmentChoices = [];

    private void InvalidateAttachmentChoices()
    {
        if (ReferenceEquals(Session.Army, attachmentChoiceArmy) && ReferenceEquals(Session.Dataset, attachmentChoiceData)) return;
        attachmentChoices.Clear(); attachmentChoiceArmy = Session.Army; attachmentChoiceData = Session.Dataset;
    }

    private Task<List<ArmyEntry>> AttachmentChoices(ArmyList snapshot, Dataset? library, string entryId)
    {
        // Edits, imports, undo/redo and opening another army replace these inputs.
        // Context-only navigation can reuse the completed or in-flight calculation.
        if (!ReferenceEquals(snapshot, attachmentChoiceArmy) || !ReferenceEquals(library, attachmentChoiceData))
        {
            attachmentChoices.Clear(); attachmentChoiceArmy = snapshot; attachmentChoiceData = library;
        }
        if (!attachmentChoices.TryGetValue(entryId, out var task) || task.IsFaulted || task.IsCanceled)
            attachmentChoices[entryId] = task = Task.Run(() => ArmySession.AttachmentCandidates(snapshot, library, entryId));
        return task;
    }

    private bool AttachmentChooserCurrent(VerticalStackLayout body, ArmyList snapshot, Dataset? library, string entryId)
        => ReferenceEquals(Session.Army, snapshot) && ReferenceEquals(Session.Dataset, library)
            && screen == Screen.Unit && !playMode && previewKey.Length == 0 && unitTab == "datasheet"
            && Session.Context.EntryId == entryId && datasheetRegions.TryGetValue("attachments", out var region)
            && ReferenceEquals(region.Body, body);

    private void AddAttachmentChooser(VerticalStackLayout body, UnitView view)
    {
        var snapshot = Session.Army; var library = Session.Dataset; var entryId = view.Entry!.Id;
        var isLeader = LeaderRules.IsLeader(view.Unit);
        var label = isLeader ? "Choose a bodyguard unit" : "Choose a leader or support";
        var choices = new VerticalStackLayout { Spacing = 10 };
        Button? button = null;
        button = Ui.Button(label, async () => await LoadAttachmentChoices(body, choices, button!, snapshot, library, entryId, isLeader, label),
            "choose-attachment", Accent);
        body.Add(button); body.Add(choices);
    }

    private async Task LoadAttachmentChoices(VerticalStackLayout body, VerticalStackLayout choices, Button button,
        ArmyList snapshot, Dataset? library, string entryId, bool isLeader, string label)
    {
        if (busy || !button.IsEnabled || !AttachmentChooserCurrent(body, snapshot, library, entryId)) return;
        button.IsEnabled = false; button.Text = "Loading attachment choices…";
        try
        {
            // Only the chooser waits. Scrolling, Back and the rest of the page remain
            // available, and the calculation never holds the mutable session lock.
            var candidates = await AttachmentChoices(snapshot, library, entryId);
            if (!AttachmentChooserCurrent(body, snapshot, library, entryId)) return;
            choices.Clear();
            foreach (var candidate in candidates)
                choices.Add(Action((isLeader ? "Attach to " : "Attach ") + LeaderRules.EntryLabel(snapshot, candidate.Id),
                    () => Session.RunAsync(s =>
                    {
                        if (!ReferenceEquals(s.Army, snapshot) || !ReferenceEquals(s.Dataset, library)) return;
                        // Attach revalidates the complete proposed group, including
                        // permissions granted by enhancements and other members.
                        if (isLeader) s.Attach(entryId, candidate.Id); else s.Attach(candidate.Id, entryId);
                    }), "attach-" + candidate.Id));
            if (candidates.Count == 0) choices.Add(Ui.Text("No eligible attachment targets in this army.", 14, colour: Ui.Muted));
            button.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (AttachmentChooserCurrent(body, snapshot, library, entryId))
            {
                choices.Clear(); choices.Add(Ui.Text("Could not load attachment choices. " + ex.Message, 14, colour: Ui.Muted));
                button.Text = label; button.IsEnabled = true;
            }
        }
    }
}

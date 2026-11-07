using System.Windows;
using System.Windows.Controls;
using System.IO;
using GrimList.Core;

namespace GrimList.Desktop;
public partial class MainWindow
{
    private void RefreshEnhancements(UnitDefinition? unit, ArmyEntry? entry)
    {
        var choices = Enhancements.Options(army, data, unit);
        var selectedKey = entry?.Enhancement is { } selection ? Enhancements.Key(selection) : "";
        EnhancementsSection.Visibility = entry?.Enhancement != null || !playMode && choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnhancementEditor.Visibility = !playMode && choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnitEnhancementChoices.ItemsSource = choices.Select(c =>
        {
            var selected = Enhancements.Key(c) == selectedKey;
            var occupied = !selected && (selectedKey.Length > 0 || entry != null && Enhancements.Slot(army, entry.Id).Any(e => e.Enhancement != null));
            var selection = new EnhancementSelection(c.Detachment, c.Name);
            var reason = "";
            if (entry != null && !selected)
                try { Enhancements.Select(army, data, entry.Id, selection); }
                catch (InvalidDataException ex) { reason = ex.Message; }
            return new EnhancementChoice(Enhancements.Key(c), c.Name, c.Cost, $"{Enhancements.Kind(c)} · {c.Detachment}", c.DisplayText,
                selection, selected, selected ? "Selected · Remove" : occupied ? "Slot occupied" : reason.Length > 0 ? "Limit reached" : "+ Add",
                occupied || reason.Length == 0, reason.Length > 0 ? reason : c.DisplayText);
        }).ToList();
        UnitEnhancementChoices.IsEnabled = entry != null;
        var usage = Enhancements.Usage(army, data);
        EnhancementEditorHint.Text = $"{usage.Choices}/{Enhancements.Budget(army.Limit)?.ToString() ?? "?"} enhancement choices · {usage.Upgrades} Upgrades (up to 3 copies of each share 1 choice).\n"
            + (entry == null ? "Add this unit to your roster to choose an enhancement or upgrade."
            : "One enhancement or upgrade across this unit and its attached leaders. Click to equip; click again to remove.");
        var chosen = Enhancements.Find(army, data, entry?.Enhancement);
        SelectedEnhancementSection.Visibility = entry?.Enhancement != null ? Visibility.Visible : Visibility.Collapsed;
        SelectedEnhancementName.Text = entry?.Enhancement?.Name ?? "";
        SelectedEnhancementMeta.Text = chosen == null ? entry?.Enhancement?.Detachment ?? "" : $"{Enhancements.Kind(chosen)} · {chosen.Detachment} · {chosen.Cost}";
        SelectedEnhancementRule.Text = chosen?.DisplayText ?? "";
        RemoveEnhancementButton.Content = Enhancements.Upgrade(chosen) ? "Remove upgrade" : "Remove enhancement";
        EnhancementWarning.Text = entry == null ? "" : string.Join("\n", Enhancements.Issues(army, data, entry));
        EnhancementWarning.Visibility = EnhancementWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveEnhancementButton.Visibility = !playMode && entry?.Enhancement != null && !choices.Any(c => Enhancements.Key(c) == selectedKey)
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChooseEnhancement(object sender, RoutedEventArgs e)
    {
        if (rendering || renderingDetails || playMode || detailEntryId == null || sender is not Button { Tag: EnhancementSelection choice }) return;
        Run(() => Edit(() => session.ToggleEnhancement(detailEntryId, choice)));
    }
    private void RemoveEnhancement(object sender, RoutedEventArgs e)
    {
        if (playMode || detailEntryId == null) return;
        Run(() => Edit(() => session.SelectEnhancement(detailEntryId, null)));
    }
    private record EnhancementChoice(string Key, string Name, string Cost, string Detachment, string Text, EnhancementSelection Selection, bool Selected, string Action, bool Available, string Hint);
}

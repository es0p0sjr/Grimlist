using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private bool playMode;
    private GridLength libraryWidth = new(235), loadoutWidth = new(1, GridUnitType.Star);
    private readonly HashSet<string> closedLoadoutCards = [];
    private string? loadoutEntryId;
    private record EquipmentAction(int Model, string Group, string Option, int Count);
    private void TogglePlayMode(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        playMode = !playMode;
        if (playMode)
        {
            libraryWidth = LibraryColumn.Width; loadoutWidth = LoadoutColumn.Width;
            detailEntryId = army.Entries.FirstOrDefault(a => a.Id == detailEntryId)?.Id ?? army.Entries.FirstOrDefault()?.Id;
        }
        ApplyPlayLayout(); Render();
        Status(playMode ? "Play mode · equipped weapons only" : "Build mode · edit composition and equipment");
    }

    private void ApplyPlayLayout()
    {
        Tag = playMode;
        PlayModeButton.Content = playMode ? "Build mode" : "Play mode";
        LibraryColumn.MinWidth = playMode ? 0 : 180; LibraryColumn.Width = playMode ? new(0) : libraryWidth;
        LibraryDividerColumn.Width = new(playMode ? 0 : 5);
        LibraryPanel.Visibility = LibraryDivider.Visibility = playMode ? Visibility.Collapsed : Visibility.Visible;
        LoadoutColumn.Width = loadoutWidth;
        LoadoutGap.Width = new(12);
        LoadoutEditorRow.Height = playMode ? new(0) : new(1.2, GridUnitType.Star);
        LoadoutEditor.Visibility = playMode ? Visibility.Collapsed : Visibility.Visible;
        ArmyName.IsReadOnly = playMode;
        LimitBox.IsReadOnly = playMode;
        UndoButton.Visibility = RedoButton.Visibility = playMode ? Visibility.Collapsed : Visibility.Visible;
        WarlordPicker.IsHitTestVisible = !playMode; WarlordPicker.Focusable = !playMode;
        Roster.IsEnabled = true;
    }

    private void RefreshLoadout(UnitDefinition? unit, ArmyEntry? entry)
    {
        var context = entry?.Id ?? unit?.Key;
        if (loadoutEntryId != context) { closedLoadoutCards.Clear(); loadoutEntryId = context; }
        LoadoutCards.Children.Clear();
        LoadoutCards.IsEnabled = entry != null;
        ResetLoadoutButton.IsEnabled = entry != null && unit != null;
        ImportLoadoutButton.Visibility = unit != null && unit.Profile?.Loadout == null ? Visibility.Visible : Visibility.Collapsed;
        LoadoutSummary.Text = "";
        PlayLoadoutNotice.Text = "";
        if (unit == null)
        {
            LoadoutWarnings.Visibility = Visibility.Collapsed;
            LoadoutHint.Text = "Select a unit to view its equipment.";
            PlayLoadoutNotice.Text = playMode ? "Your roster is empty. Return to Build mode to add units." : "";
            PlayLoadoutNotice.Visibility = playMode ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var size = entry?.Models ?? (SizePicker.SelectedItem as PointOption)?.Models ?? 1;
        var definition = Loadouts.Definition(unit, entry?.Loadout);
        var loadout = entry?.Loadout ?? Loadouts.Create(unit, size);
        var issues = entry == null ? [] : Loadouts.Issues(unit, entry);
        LoadoutHint.Text = entry == null ? "Add this unit to your roster to select its equipment. Defaults are previewed below."
            : loadout.Manual ? "Set the quantities this unit carries. Review replacement limits against its source."
            : "Set option quantities. Standard equipment updates automatically.";
        LoadoutWarnings.Text = string.Join("\n", issues.Distinct());
        LoadoutWarnings.Visibility = LoadoutWarnings.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetLoadoutButton.Content = unit.Profile?.Loadout == null ? "Clear weapon choices" : "Reset to defaults";
        var equipped = Loadouts.Equipment(unit, entry?.Loadout).GroupBy(e => e.Option.Name)
            .Select(g => $"{g.Sum(e => e.Count)} × {g.Key}");
        LoadoutSummary.Text = string.Join(" · ", equipped);
        LoadoutSummary.Visibility = playMode && LoadoutSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (playMode)
        {
            PlayLoadoutNotice.Text = string.Join("\n", issues.Distinct());
            PlayLoadoutNotice.Visibility = PlayLoadoutNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        PlayLoadoutNotice.Visibility = Visibility.Collapsed;
        foreach (var group in definition.UnitGroups) LoadoutCards.Children.Add(EquipmentChoices(unit, entry, loadout, group, -1, size));
        foreach (var section in loadout.Manual ? [] : Loadouts.Sections(unit))
            LoadoutCards.Children.Add(SectionChoices(unit, entry, loadout, definition, section, size));
    }
    private TextBlock Label(string text, bool bold = false) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 0, 0, 6) };

    private FrameworkElement EquipmentChoices(UnitDefinition unit, ArmyEntry? entry, UnitLoadout loadout, EquipmentGroup group, int model, int size)
    {
        var panel = new StackPanel { Margin = new(0, 0, 0, 10) };
        var selections = model < 0 ? loadout.Equipment : loadout.Models[model].Equipment;
        var exclusive = group.Maximum.At(size) == 1;
        if (group.Options.Count == 1 && group.Minimum.At(size) == group.Maximum.At(size) && group.Minimum.At(size) > 0)
        {
            var fixedOption = group.Options[0];
            panel.Children.Add(Label($"{selections.Where(s => s.GroupId == group.Id).Sum(s => s.Count)} × {WargearPricing.Label(unit, fixedOption)}", true));
            return panel;
        }
        panel.Children.Add(Label(group.Name.Length > 0 ? group.Name : "Equipment", true));
        if (exclusive && group.Options.Count > 1) panel.Children.Add(Label(group.Minimum.At(size) > 0 ? "Select one" : "Select up to one"));
        var options = new UniformGrid { Columns = exclusive && group.Options.Count > 1 ? 2 : 1 };
        foreach (var option in group.Options)
        {
            var count = selections.Where(s => s.GroupId == group.Id && s.OptionId == option.Id).Sum(s => s.Count);
            if (exclusive && group.Options.Count > 1)
            {
                var nextCount = count > 0 && group.Minimum.At(size) == 0 ? 0 : 1;
                var action = new EquipmentAction(model, group.Id, option.Id, nextCount);
                var button = ChoiceButton((count > 0 ? "✓ " : "") + WargearPricing.Label(unit, option), action);
                button.Background = (Brush)FindResource(count > 0 ? "AccentDark" : "Input");
                button.BorderBrush = count > 0 ? Brushes.White : (Brush)FindResource("Line");
                button.IsEnabled = CanChoose(unit, entry, action);
                options.Children.Add(button);
            }
            else
            {
                var row = new Grid { Margin = new(0, 2, 0, 4) };
                row.ColumnDefinitions.Add(new() { Width = new(32) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(32) });
                var minus = new EquipmentAction(model, group.Id, option.Id, count - 1);
                var plus = new EquipmentAction(model, group.Id, option.Id, count + 1);
                var decrease = ChoiceButton("−", minus); decrease.Padding = new(0); decrease.IsEnabled = count > option.Minimum.At(size) && CanChoose(unit, entry, minus);
                var increase = ChoiceButton("+", plus); increase.Padding = new(0); increase.IsEnabled = count < option.Maximum.At(size) && CanChoose(unit, entry, plus);
                var text = Label($"{count} × {WargearPricing.Label(unit, option)}"); text.Margin = new(7, 5, 7, 5); text.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(text, 1); Grid.SetColumn(increase, 2);
                row.Children.Add(decrease); row.Children.Add(text); row.Children.Add(increase); options.Children.Add(row);
            }
        }
        panel.Children.Add(options); return panel;
    }

    private Button ChoiceButton(string title, EquipmentAction action)
    {
        var button = new Button { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, Tag = action, Margin = new(2), MinHeight = 32, Padding = new(6) };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        button.Click += SelectEquipment; return button;
    }
    private static bool CanChoose(UnitDefinition unit, ArmyEntry? entry, EquipmentAction action)
    {
        if (entry == null || action.Count < 0) return false;
        try { Loadouts.Choose(unit, entry, action.Model, action.Group, action.Option, action.Count); return true; }
        catch (InvalidDataException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    private void SelectEquipment(object sender, RoutedEventArgs e)
    {
        if (playMode || sender is not Button { Tag: EquipmentAction action }) return;
        var entry = army.Entries.FirstOrDefault(e => e.Id == detailEntryId); var unit = entry == null ? null : data?.FindUnit(entry.UnitKey);
        if (entry == null || unit == null) return;
        Run(() => Edit(() => session.ChooseEquipment(entry.Id, action.Model, action.Group, action.Option, action.Count)));
    }
    private void ResetLoadout(object sender, RoutedEventArgs e)
    {
        if (playMode) return;
        var entry = army.Entries.FirstOrDefault(e => e.Id == detailEntryId); var unit = entry == null ? null : data?.FindUnit(entry.UnitKey);
        if (entry == null || unit == null) return;
        Run(() => Edit(() => session.ResetLoadout(entry.Id)));
    }
    private void PreviewSizeChanged(object sender, SelectionChangedEventArgs e) { if (!rendering && !renderingDetails && store != null) RefreshDetails(); }
}

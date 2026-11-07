using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckEnhancements(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixturePath = Path.Combine(root, "enhancement-fixture.json");
        if (!File.Exists(fixturePath)) return;
        var data = DataJson.Read<Dataset>(File.ReadAllText(fixturePath));
        var testPath = Path.Combine(root, "enhancement-ui");
        using var store = WindowsServices.OpenStore(testPath);
        store.Import(data);
        var originalUnits = data.Units;
        data = store.LoadDataset()!;
        var army = ArmyList.New("Death Guard") with { Name = "Enhancement eligibility", Detachment = "Enhancement host",
            Entries = originalUnits.Select(u => data.Units.Single(imported => imported.Name == u.Name))
                .Select((u, i) => new ArmyEntry($"enh-ui-{i}", u.Key, u.Name, 1)).ToList() };
        store.Save(army);
        var window = new MainWindow(testPath) { Owner = owner, ShowInTaskbar = false };
        string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                var home = Find<HomeView>(window, "Home");
                Find<WrapPanel>(home, "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var roster = Find<ListBox>(window, "Roster");
                var picker = Find<ItemsControl>(window, "UnitEnhancementChoices");
                Check(window.FindName("UnitEnhancementPicker") == null, "Enhancements must not use a dropdown.");
                var section = Find<StackPanel>(window, "EnhancementsSection");
                var weaponSection = Find<StackPanel>(window, "RangedWeaponsSection");
                Check(section.Parent == weaponSection.Parent && section.Parent is StackPanel panel && panel.Children.IndexOf(section) < panel.Children.IndexOf(weaponSection), "Enhancement controls must appear above ranged weapons.");
                string Key(string name) => Enhancements.Key(data.ReferenceCards!.Single(c => c.Name == name));
                ArmyList Saved() => store.LoadArmies().Single();
                bool Has(string name) => picker.Items.Cast<object>().Any(o => (string?)o.GetType().GetProperty("Key")!.GetValue(o) == Key(name));
                void Choose(string name)
                {
                    window.UpdateLayout();
                    Descendants<Button>(picker).Single(b => b.Tag is EnhancementSelection selection && selection.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                roster.SelectedValue = "enh-ui-0";
                Check(Find<StackPanel>(window, "EnhancementEditor").Visibility == Visibility.Visible && Has("Caster blessing") && Has("Infantry blessing") && !Has("Terminator blessing") && !Has("Other blessing"), "Non-Terminator character saw invalid enhancement choices.");
                roster.SelectedValue = "enh-ui-1";
                Check(Has("Terminator blessing") && !Has("Caster blessing") && !Has("Infantry blessing"), "Terminator character restrictions were ignored.");
                Choose("Terminator blessing");
                Check(Saved().Entries[1].Enhancement?.Name == "Terminator blessing" && ArmyCalculator.Calculate(Saved(), data).Total == 335, "Selecting an enhancement must save it and add its points.");
                Choose("General blessing");
                Check(Saved().Entries[1].Enhancement?.Name == "Terminator blessing" && Find<Border>(window, "ErrorBanner").Visibility == Visibility.Visible
                    && Find<TextBlock>(window, "ErrorMessage").Text.Contains("only one enhancement or upgrade"), "Second enhancement must be rejected with a visible error.");
                Capture(window, imagePath + ".selection-error.png");
                Click(window, "DismissErrorButton"); Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Error cannot be dismissed.");
                Choose("Terminator blessing"); Choose("General blessing");
                Choose("General blessing"); Check(Saved().Entries[1].Enhancement == null && ArmyCalculator.Calculate(Saved(), data).Total == 320, "Clicking a selected enhancement must remove it and its points.");
                Choose("Terminator blessing");
                Check(Find<TextBlock>(window, "SelectedEnhancementRule").Text.StartsWith("TERMINATOR model only"), "Selected enhancement rule missing from datasheet.");
                Capture(window, imagePath + ".enhancement.png");
                Click(window, "UndoButton"); Check(Saved().Entries[1].Enhancement == null, "Undo did not remove enhancement.");
                Click(window, "RedoButton"); Check(Saved().Entries[1].Enhancement != null, "Redo did not restore enhancement.");
                roster.SelectedValue = "enh-ui-1"; Click(window, "PlayModeButton");
                Check(Find<StackPanel>(window, "SelectedEnhancementSection").Visibility == Visibility.Visible &&
                    Find<StackPanel>(window, "EnhancementEditor").Visibility == Visibility.Collapsed && Find<Button>(window, "RemoveEnhancementButton").Visibility == Visibility.Collapsed,
                    "Play mode must show the equipped enhancement without editing controls.");
                Capture(window, imagePath + ".enhancement-play.png");
                Click(window, "PlayModeButton");
                roster.SelectedValue = "enh-ui-2";
                Check(Find<StackPanel>(window, "EnhancementEditor").Visibility == Visibility.Collapsed && !Has("Terminator blessing"), "Epic Hero saw enhancement choices.");
                roster.SelectedValue = "enh-ui-3";
                Check(Find<StackPanel>(window, "EnhancementEditor").Visibility == Visibility.Collapsed, "Non-character saw enhancement choices.");
                Click(window, "RulesButton");
                DetachmentAction(window, "CurrentDetachments", "Enhancement host").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Click(window, "CloseRulesButton"); roster.SelectedValue = "enh-ui-1";
                Check(Find<StackPanel>(window, "EnhancementEditor").Visibility == Visibility.Collapsed && Find<TextBlock>(window, "EnhancementWarning").Text.Contains("no longer eligible"), "Detachment removal must hide options and flag the retained selection.");
                Click(window, "RemoveEnhancementButton");
                Check(Saved().Entries[1].Enhancement == null && ArmyCalculator.Calculate(Saved(), data).Total == 320, "Removing an unavailable enhancement did not clear its points.");
            }
            catch (Exception ex) { failure = ex.Message; }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckAttachedEnhancementErrors(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!; var fixture = Path.Combine(root, "upgrade-fixture.json");
        if (!File.Exists(fixture)) return;
        var path = Path.Combine(root, "attached-enhancement-errors");
        using var store = WindowsServices.OpenStore(path); store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        var data = store.LoadDataset()!; var leader = data.Units.Single(u => u.Name == "Upgrade character"); var troops = data.Units.Single(u => u.Name == "Upgrade troops");
        store.Save(ArmyList.New("Death Guard") with { Name = "Attached enhancement limits", Detachment = "Test host", Entries = [
            new("leader", leader.Key, leader.Name, 1), new("bodyguard", troops.Key, troops.Name, 1)] });
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                Find<WrapPanel>(Find<HomeView>(window, "Home"), "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var roster = Find<ListBox>(window, "Roster");
                void Choose(string id, string name)
                {
                    roster.SelectedValue = id; window.UpdateLayout();
                    var button = Descendants<Button>(Find<ItemsControl>(window, "UnitEnhancementChoices")).Single(b => b.Tag is EnhancementSelection s && s.Name == name);
                    Check(button.IsEnabled, "Conflicting enhancement should explain the occupied slot when clicked");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                ArmyList Saved() => store.LoadArmies().Single();
                ComboBox AttachmentPicker()
                {
                    roster.SelectedValue = "leader"; window.UpdateLayout();
                    return OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(window, "AttachmentEditor")).Single())!;
                }
                Choose("leader", "Character blessing"); Choose("bodyguard", "Infantry blessing");
                var picker = AttachmentPicker(); picker.SelectedValue = "bodyguard";
                Check(Saved().Entries[0].AttachedToId == "" && (string)picker.SelectedValue == "", "Rejected attachment changed the saved or displayed selection");
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Visible && Find<TextBlock>(window, "ErrorMessage").Text.Contains("across its bodyguard and leaders"), "Attachment conflict did not show unit-local error");
                Check(Descendants<Border>(Find<Grid>(window, "BuilderScreen")).Contains(Find<Border>(window, "ErrorBanner")), "Error note is still a global overlay");
                roster.SelectedValue = "bodyguard";
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Leader error leaked into bodyguard details");
                Check(!Find<TextBlock>(window, "StatusText").Text.Contains("across its bodyguard"), "Unit enhancement error leaked into global footer");
                roster.SelectedValue = "leader";
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Visible, "Returning to affected unit lost its pending error");
                Capture(window, imagePath + ".attached-error.png");
                Choose("bodyguard", "Infantry blessing"); AttachmentPicker().SelectedValue = "bodyguard";
                Check(Saved().Entries[0].AttachedToId == "bodyguard", "Clearing conflicting upgrade did not allow attachment");
                window.UpdateLayout();
                Check(Descendants<TextBlock>(roster).Count(t => t.IsVisible && t.Text.StartsWith("ATTACHED UNIT")) == 1, "Attached units are missing their shared roster group");
                Capture(window, imagePath + ".attached-group.png");
                AttachmentPicker().SelectedValue = "";
                window.UpdateLayout();
                Check(!Descendants<TextBlock>(roster).Any(t => t.IsVisible && t.Text.StartsWith("ATTACHED UNIT")), "Detached units retained attachment group");
                Click(window, "UndoButton"); window.UpdateLayout();
                Check(Saved().Entries[0].AttachedToId == "bodyguard" && Descendants<TextBlock>(roster).Count(t => t.IsVisible && t.Text.StartsWith("ATTACHED UNIT")) == 1, "Undo did not restore attachment group");
                Choose("bodyguard", "Infantry blessing");
                Check(Saved().Entries[1].Enhancement == null && Find<Border>(window, "ErrorBanner").Visibility == Visibility.Visible, "Attached bodyguard accepted second enhancement/upgrade");
                Choose("leader", "Character blessing"); Choose("bodyguard", "Infantry blessing");
                Choose("leader", "Character blessing");
                Check(Saved().Entries[0].Enhancement == null && Find<TextBlock>(window, "ErrorMessage").Text.Contains("across its bodyguard and leaders"), "Leader accepted enhancement over bodyguard upgrade");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
    private static void CheckSelectionLimits(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "plague-marines-limit-fixture.json");
        if (!File.Exists(fixture)) return;
        var path = Path.Combine(root, "selection-limit-ui");
        using var store = WindowsServices.OpenStore(path);
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        var unit = store.LoadDataset()!.Units.Single();
        var army = ArmyList.New("Death Guard") with { Name = "Plague Marine launcher limits", Entries = [new("marines", unit.Key, unit.Name, 5, Loadout: Loadouts.Create(unit, 5))] };
        store.Save(army);
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false }; string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                Find<WrapPanel>(Find<HomeView>(window, "Home"), "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Button AddLauncher()
                {
                    window.UpdateLayout();
                    return Descendants<Button>(Find<StackPanel>(window, "LoadoutCards")).Single(b => AutomationProperties.GetName(b) == "Add Blight launcher");
                }
                Check(AddLauncher().IsEnabled, "First launcher should be selectable");
                AddLauncher().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!AddLauncher().IsEnabled, "Five models allowed a second launcher");
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Launcher cap must disable the button without an error popup");
                Capture(window, imagePath + ".launcher-cap.png");
                var sizes = Find<ComboBox>(window, "EntrySizePicker");
                sizes.SelectedValue = 7;
                Check(!AddLauncher().IsEnabled, "Seven models allowed a second launcher");
                sizes.SelectedValue = 10;
                Check(AddLauncher().IsEnabled, "Ten models did not unlock a second launcher");
                AddLauncher().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!AddLauncher().IsEnabled, "Ten models allowed a third launcher");
                Check(Find<Border>(window, "ErrorBanner").Visibility == Visibility.Collapsed, "Legal squad resizing or launcher selection showed an error");
                var saved = store.LoadArmies().Single();
                Check(Loadouts.Weapons(unit, saved.Entries[0].Loadout).Single(w => w.Profile.Name == "Blight launcher").Count == 2, "Launcher selections were not saved");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
    }
}

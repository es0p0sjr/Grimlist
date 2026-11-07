using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckDatasheetReferences(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "muster-fixture.json");
        if (!File.Exists(fixture)) return;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "references-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        var data = store.LoadDataset()!;
        var army = ArmyList.New("Death Guard") with { Name = "Datasheet reference UI", Entries = new[] { "Plague Marines", "Poxwalkers", "Typhus" }.Select(name =>
        { var u = data.Units.Single(u => u.Name == name); var models = u.Options.First().Models; return new ArmyEntry(name, u.Key, name, models, Loadout: Loadouts.Create(u, models)); }).ToList() };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        void Select(string id) { Find<ListBox>(window, "Roster").SelectedValue = id; window.UpdateLayout(); }
        void Dialog(Action action, Action<RuleReferenceWindow> verify)
        {
            Exception? failure = null; var opened = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var dialog = System.Windows.Application.Current.Windows.OfType<RuleReferenceWindow>().SingleOrDefault();
                try { Check(dialog != null, "Reference dialog did not open"); opened = true; verify(dialog!); }
                catch (Exception ex) { failure = ex; }
                finally { dialog?.Close(); }
            }));
            action(); Check(opened, "Weapon event did not open the reference dialog");
            if (failure != null) throw failure;
        }
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Select("Plague Marines");
            var cards = Find<ItemsControl>(window, "AbilityCards").Items.Cast<AbilityProfile>().ToList();
            Check(cards.Any(a => a.Name.StartsWith("Icon of Despair")) && !cards.Any(a => a.Name is "Lethal Hits" or "Rapid Fire" or "Anti"), "Sidebar removed equipment ability or retained weapon keyword definitions");
            var table = Find<DataGrid>(window, "RangedWeaponTable");
            Check(Find<ScrollViewer>(window, "AbilitySidebar").TranslatePoint(new Point(), window).X > table.TranslatePoint(new Point(), window).X + table.ActualWidth - 2, "Abilities are not beside the weapons");
            var weapon = table.Items.Cast<WeaponProfile>().First(w => w.Keywords.Contains("Lethal Hits"));
            table.ScrollIntoView(weapon); table.UpdateLayout();
            var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(weapon);
            Dialog(() => row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }), dialog =>
            {
                var rules = Find<ItemsControl>(dialog, "DefinitionCards").Items.Cast<RuleDefinition>().ToList();
                Check(rules.Any(r => r.Name == "Lethal Hits" && r.Text.Contains("critical hit", StringComparison.OrdinalIgnoreCase)), "Tapped weapon did not resolve its imported keyword rule");
                Check(rules.Count == WeaponRuleReference.Keywords(weapon.Keywords).Count, "Popup included other weapons' keywords");
                Capture(dialog, imagePath + ".weapon-rules.png");
            });
            table.SelectedItem = weapon;
            Dialog(() => table.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }), _ => { });
            Select("Poxwalkers");
            Check(Find<TextBlock>(window, "FeelNoPainValue").Text == "5+++ FNP" && Find<Button>(window, "FeelNoPainPanel").IsVisible, "Real Poxwalker FNP missing from header");
            Dialog(() => Click(window, "FeelNoPainPanel"), dialog => Check(Find<ItemsControl>(dialog, "DefinitionCards").Items.Cast<RuleDefinition>().Single().Text.Contains("would lose a wound"), "FNP badge lost its rule text"));
            Capture(window, imagePath + ".abilities-sidebar.png");
            Click(window, "PlayModeButton"); window.UpdateLayout();
            Check(Find<ScrollViewer>(window, "AbilitySidebar").IsVisible && !Find<ScrollViewer>(window, "LoadoutEditor").IsVisible
                && Find<Button>(window, "FeelNoPainPanel").IsVisible, "Play mode hid abilities/FNP with the loadout editor");
            Capture(window, imagePath + ".abilities-play.png");
            Select("Typhus");
            Check(Find<Button>(window, "FeelNoPainPanel").Visibility == Visibility.Collapsed && Find<TextBlock>(window, "InvulnerableValue").Text == "4+", "Switching units retained another unit's FNP or lost invulnerable save");
            Click(window, "PlayModeButton");
            Check(Find<ScrollViewer>(window, "LoadoutEditor").Visibility == Visibility.Visible, "Build mode failed to restore loadout controls");
            File.WriteAllText(imagePath + ".references.txt", "PASS: real imported weapon definitions; pointer and keyboard dialogs; sidebar retains unit/equipment abilities; Poxwalker 5+++ FNP and rule dialog; switching units; Build/Play layout.");
        }
        finally { window.Close(); }
    }
}

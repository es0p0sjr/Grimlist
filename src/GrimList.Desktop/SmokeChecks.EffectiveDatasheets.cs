using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckEffectiveDatasheets(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var path = Path.Combine(root, "effective-import.json");
        if (!File.Exists(path)) throw new InvalidDataException("Run the shared regression suite to create effective-import.json before the desktop smoke test.");
        using var store = WindowsServices.OpenStore(Path.Combine(root, "effective-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(path)));
        var data = store.LoadDataset()!;
        var unit = data.Units.Single(u => u.Faction == "Chaos Daemons" && u.Name == "Daemon Prince of Chaos");
        var card = data.ReferenceCards!.First(c => c.Name == "A'rgath, the King of Blades");
        var army = ArmyList.New(unit.Faction) with { Name = "Effective datasheet UI", Detachment = card.Detachment,
            Entries = [new("p", unit.Key, unit.Name, 1, Loadout: Loadouts.Create(unit, 1))] };
        store.Save(army);
        var open = typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        ComboBox Picker() => Find<StackPanel>(window, "DatasheetChoices").Children.OfType<ComboBox>().Single();
        WeaponProfile Strike() => Find<DataGrid>(window, "MeleeWeaponTable").Items.Cast<WeaponProfile>().Single(w => w.Name.EndsWith("strike"));
        void Select() { Find<ListBox>(window, "Roster").SelectedValue = "p"; window.UpdateLayout(); }
        void Enhancement()
        {
            window.UpdateLayout();
            Descendants<Button>(Find<ItemsControl>(window, "UnitEnhancementChoices")).Single(b => b.Tag is EnhancementSelection c && c.Name == card.Name)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        try
        {
            open.Invoke(window, [army]); Select();
            Check(Picker().Items.Count == 5, "Allegiance selector did not offer four choices plus unselected state");
            var group = unit.Profile!.Semantics!.Choices.Single();
            Picker().SelectedValue = group.Options.Single(o => o.Name == "Khorne").Id;
            Check(Strike().Strength == "10", "Khorne effective Strength not visible");
            Enhancement();
            Check(Strike().Strength == "11" && Strike().Attacks == "7", "Enhancement effective weapon values not visible");
            Check(window.FindName("EffectiveChangesPanel") == null, "Changes and sources section remains on the datasheet");
            Click(window, "UndoButton"); Select(); Check(Strike().Strength == "10", "Undo retained enhancement modifier");
            Click(window, "RedoButton"); Select(); Check(Strike().Strength == "11", "Redo did not reapply enhancement modifier");
            Capture(window, imagePath + ".effective-datasheet.png");
            Enhancement();
            Picker().SelectedValue = group.Options.Single(o => o.Name == "Nurgle").Id;
            Check(Strike().Strength == "8", "Changing allegiance left old Strength");
            Check(Find<ItemsControl>(window, "Stats").Items.Cast<EffectiveCharacteristic>().Any(p => p.Key == "T" && p.Value == "11" && p.Modified), "Nurgle Toughness not highlighted");
            Check(!Find<TextBlock>(window, "UnitKeywords").Text.Contains("Khorne", StringComparison.OrdinalIgnoreCase), "Old allegiance keyword remained visible");
        }
        finally { window.Close(); }
        File.WriteAllText(imagePath + ".effective-datasheet.txt", "PASS: real source allegiance selector, Khorne/A'rgath current values only, no change-history panel, undo/redo, removal and Nurgle transition.");
    }
}

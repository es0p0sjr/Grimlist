using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckWeaponRules(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = Path.Combine(root, "weapon-rules-ui.json");
        using var store = WindowsServices.OpenStore(Path.Combine(root, "weapon-rules-ui-" + Guid.NewGuid().ToString("N")));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(fixture)));
        var data = store.LoadDataset()!; var unit = data.Units.Single();
        var army = ArmyList.New(unit.Faction) with { Name = "Weapon rule rendering", Detachment = "Host", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Refresh()
        {
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Find<ListBox>(window, "Roster").SelectedValue = "bearer"; window.UpdateLayout();
        }
        TextBlock Stat(string field) => Descendants<TextBlock>(Find<ItemsControl>(window, "Stats"))
            .Single(t => t.DataContext is EffectiveCharacteristic stat && stat.Key == field && t.Text == stat.Value);
        TextBlock WeaponStat(string tableName, string field)
        {
            var table = Find<DataGrid>(window, tableName); table.UpdateLayout();
            var column = table.Columns.Single(c => c.Header.ToString() == field);
            return (TextBlock)column.GetCellContent(table.Items[0]);
        }
        bool Highlighted(TextBlock value) => value.Foreground.ToString() == MainWindow.ModifiedValueBrush.ToString()
            && value.TextDecorations?.Contains(TextDecorations.Underline[0]) == true;
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]); Refresh();
            Check(Stat("T").Text == "11" && Highlighted(Stat("T")) && !Highlighted(Stat("M")), "Windows characteristic highlight/value incorrect");
            Check(WeaponStat("RangedWeaponTable", "S").Text == "6" && Highlighted(WeaponStat("RangedWeaponTable", "S"))
                && !Highlighted(WeaponStat("RangedWeaponTable", "A")), "Windows weapon highlight/value incorrect");
            session.SelectEnhancement("bearer", new("Host", "Mobile bearer")); Refresh();
            Check(Find<DataGrid>(window, "RangedWeaponTable").Items.Cast<WeaponProfile>().All(w => w.Keywords.Contains("ASSAULT")), "Windows enhancement keyword missing");
            Check(Descendants<WeaponKeywords>(window).Any(t => t.Keywords.Contains("ASSAULT")), "Windows keyword control not updated");
            Check(Find<DataGrid>(window, "MeleeWeaponTable").Items.Cast<WeaponProfile>().All(w => w.Keywords.Contains("SUSTAINED HITS 1")), "Windows detachment keyword missing");
            Capture(window, imagePath + ".weapon-rules.png");
            session.SelectEnhancement("bearer", null); session.RemoveDetachment("Host"); Refresh();
            Check(Find<DataGrid>(window, "RangedWeaponTable").Items.Cast<WeaponProfile>().All(w => !w.Keywords.Contains("ASSAULT"))
                && Find<DataGrid>(window, "MeleeWeaponTable").Items.Cast<WeaponProfile>().All(w => !w.Keywords.Contains("SUSTAINED")), "Windows removal left keyword");
            Check(Descendants<TextBlock>(Find<ItemsControl>(window, "Stats")).All(t => !t.Text.Contains('→')), "Windows shows base-to-effective history");
            File.WriteAllText(imagePath + ".weapon-rules.txt", "PASS: Windows final-value highlighting, unchanged values, effective enhancement/detachment keywords and removal.");
        }
        finally { window.Close(); }
        CheckSourceWeaponRules(owner, directory, imagePath);
    }

    private static void CheckSourceWeaponRules(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var fixture = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "source-weapon-rules-ui.json")));
        using var store = WindowsServices.OpenStore(Path.Combine(root, "source-weapon-ui-" + Guid.NewGuid().ToString("N")));
        store.Import(fixture);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Refresh()
        {
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Find<ListBox>(window, "Roster").SelectedValue = session.Army.Entries[0].Id; window.UpdateLayout();
        }
        bool Assault(string text) => WeaponRules.Tokens(text).Any(k => k.Equals("ASSAULT", StringComparison.OrdinalIgnoreCase));
        void CheckVisible(bool expected)
        {
            var ranged = Find<DataGrid>(window, "RangedWeaponTable"); ranged.UpdateLayout();
            var profiles = ranged.Items.Cast<WeaponProfile>().ToList();
            Check(profiles.Count > 0 && profiles.All(w => Assault(w.Keywords) == expected), "Real ranged weapon rows lost Assault state");
            var labels = Descendants<WeaponKeywords>(ranged).ToList();
            Check(labels.Count > 0 && labels.All(w => Assault(w.Keywords) == expected), "Rendered weapon keyword controls lost Assault state");
            Check(Find<DataGrid>(window, "MeleeWeaponTable").Items.Cast<WeaponProfile>().All(w => !Assault(w.Keywords)), "Assault leaked into melee display");
        }
        try
        {
            foreach (var unit in fixture.Units)
            {
                var rule = fixture.ReferenceCards!.Single(c => c.Faction == unit.Faction);
                var models = unit.Options.First().Models;
                var entryId = "unit-" + EffectiveDatasheets.Key(unit.Faction);
                var army = ArmyList.New(unit.Faction) with { Name = "Source weapon rendering", Detachment = rule.Detachment,
                    Entries = [new(entryId, unit.Key, unit.Name, models, Loadout: Loadouts.Create(unit, models))] };
                typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
                if (rule.Section == "Enhancements") session.SelectEnhancement(entryId, new(rule.Detachment, rule.Name));
                Refresh(); CheckVisible(true);
                if (rule.Section == "Enhancements") Check(Find<TextBlock>(window, "SelectedEnhancementRule").Text == rule.Text, "Unrequested reminder remains");
                Capture(window, imagePath + ".source-" + EffectiveDatasheets.Key(unit.Faction) + ".png");
                Click(window, "PlayModeButton"); Refresh(); CheckVisible(true);
                Click(window, "PlayModeButton");
                if (rule.Section == "Enhancements") session.SelectEnhancement(entryId, null);
                else session.RemoveDetachment(rule.Detachment);
                Refresh(); CheckVisible(false);
            }
            File.WriteAllText(imagePath + ".source-weapons.txt", "PASS: Actual source upgrade and detachment Assault rendered in Windows editing/play modes, removal, ranged-only targets and exact upgrade text.");
        }
        finally { window.Close(); }
    }
}

using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckEnhancementWeapons(MainWindow owner, string directory, string imagePath)
    {
        var fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/bsdata/imperiums-sword.json"));
        var source = new SourceInfo("BSData test fixture", "1", "", "", Edition: "11");
        var card = BsDataImporter.ImportEnhancementReferences([BsDataDocument.Parse("sword.json", File.ReadAllText(fixturePath))], source).Single()
            with { Weapons = null, Text = "Melee\n6\n2+\n7\n-3\n3\n" };
        var unit = new UnitDefinition("Space Marines", "Captain", [new(1, 80)], Category: "Characters", Profile: new(
            Characteristics: new() { ["M"] = "6", ["T"] = "4", ["Sv"] = "3+", ["W"] = "5", ["LD"] = "6+", ["OC"] = "1" },
            Weapons: [new("Chainsword", "Melee", "Melee", "5", "2+", "4", "-1", "1")], Keywords: ["Character"]));
        using var store = WindowsServices.OpenStore(Path.Combine(directory, "sword-ui"));
        store.Import(new(source, [unit], ReferenceCards: [card]));
        var army = ArmyList.New(unit.Faction) with { Detachment = card.Detachment, Entries = [new("bearer", unit.Key, unit.Name, 1,
            Loadout: new([], [new("manual", "manual:Chainsword", 1)], true))] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Refresh()
        {
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Find<ListBox>(window, "Roster").SelectedValue = "bearer"; window.UpdateLayout();
        }
        void CheckRows(bool selected, bool play = false)
        {
            var table = Find<DataGrid>(window, "MeleeWeaponTable"); table.UpdateLayout();
            var rows = table.Items.Cast<WeaponProfile>().ToList();
            Check(rows.Count == (selected ? 2 : 1), "Incorrect melee row count");
            Check(rows.Any(w => w.Name.EndsWith("Chainsword")), "Original weapon disappeared");
            if (!selected) return;
            var sword = rows.Single(w => w.Name == (play ? "1 × " : "") + card.Name);
            foreach (var (field, expected) in new[] { ("A", "6"), ("WS", "2+"), ("S", "7"), ("AP", "-3"), ("D", "3") })
            {
                table.ScrollIntoView(sword); table.UpdateLayout();
                var column = table.Columns.Single(c => c.Header.ToString() == field);
                Check(column.GetCellContent(sword) is TextBlock text && text.Text == expected, "Weapon cell incorrect: " + field);
            }
            Check(Find<TextBlock>(window, "SelectedEnhancementRule").Text.Contains("in addition"), "Raw enhancement stats remain");
        }
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            session.SelectEnhancement("bearer", new(card.Detachment, card.Name)); Refresh(); CheckRows(true);
            Capture(window, imagePath + ".build.png");
            Click(window, "PlayModeButton"); Refresh(); CheckRows(true, true);
            Capture(window, imagePath + ".play.png");
            Click(window, "PlayModeButton"); session.SelectEnhancement("bearer", null); Refresh(); CheckRows(false);
            File.WriteAllText(imagePath + ".result.txt", "PASS: Imperium's Sword renders in normal melee cells in build/play, one equipped copy, original weapon retained, raw stats removed, removal clears sword.");
        }
        finally { window.Close(); }
    }
}

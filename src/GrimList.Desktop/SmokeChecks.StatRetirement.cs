using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckStatRetirement(MainWindow owner, string directory, string imagePath)
    {
        using var store = WindowsServices.OpenStore(Path.Combine(directory, "stat-retirement"));
        var source = new SourceInfo("BSData UI fixture", new string('a', 40), "2026-10-01", "", Edition: "11", ContentHash: new string('a', 64));
        var unit = new UnitDefinition("Test", "Captain", [new(1, 100)], Id: "captain", Category: "Characters", Profile: new(
            new() { ["M"] = "6\"", ["T"] = "4", ["Sv"] = "3+", ["W"] = "5", ["LD"] = "6+", ["OC"] = "2" }, Keywords: ["Character"]));
        var card = new ReferenceCard("Test", "Enhancements", "Unit stats", "Character model only.", "Old Host", "10 pts", source,
            Modifiers: [new("move", "Unit stats", "weapon", "M", "increment", "2", WeaponPredicate: new("unknown", ModelStatModifiers.UnitGroupSelector)),
                new("oc", "Unit stats", "weapon", "OC", "increment", "1", WeaponPredicate: new("unknown", ModelStatModifiers.UnitGroupSelector))]);
        var data = new Dataset(source, [unit], Detachments: [new("Test", "Old Host", Cost: "1 DP", Source: source, Id: "old", FactionId: "test")], ReferenceCards: [card]);
        store.ApplyImport(data);
        var army = ArmyList.New("Test") with { Detachment = "Old Host", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Refresh()
        {
            typeof(MainWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(window, null);
            Find<ListBox>(window, "Roster").SelectedValue = "bearer"; window.UpdateLayout();
        }
        void Stats(string movement, string oc)
        {
            var stats = Find<ItemsControl>(window, "Stats").Items.Cast<EffectiveCharacteristic>().ToList();
            Check(stats.Single(s => s.Key == "M").Value == movement && stats.Single(s => s.Key == "OC").Value == oc, "Native stat cells incorrect");
        }
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            session.SelectEnhancement("bearer", new("Old Host", "Unit stats")); Refresh(); Stats("8\"", "3");
            Click(window, "PlayModeButton"); Refresh(); Stats("8\"", "3");
            Click(window, "PlayModeButton"); session.SelectEnhancement("bearer", null); Refresh(); Stats("6\"", "2");
            session.Undo(); Refresh(); Stats("8\"", "3");
            var newer = source with { Version = new string('b', 40), ContentHash = new string('b', 64), Date = "2026-10-26" };
            var incoming = data with { Source = newer, SchemaVersion = 4, IsBsDataDownload = true, ReferenceCards = [],
                Detachments = [new("Test", "New Host", Cost: "1 DP", Source: newer, Id: "new", FactionId: "test")],
                DetachmentSources = [new("Test", "test", newer, [new("new", "New Host")], [])] };
            var workflow = new LibraryWorkflow(store); workflow.Apply(workflow.Prepare(incoming, ImportLayer.Combined));
            session.ReloadLibrary(); Refresh(); Stats("6\"", "2");
            Check(session.Army.Detachment == "Old Host" && session.Result.Issues.Any(i => i.Contains("obsolete")), "Retired selection not retained for review");
            Check(!Find<ItemsControl>(window, "AvailableDetachments").Items.Cast<object>()
                .Any(d => d.GetType().GetProperty("Name")?.GetValue(d)?.ToString() == "Old Host"), "Retired option in picker");
            File.WriteAllText(imagePath + ".result.txt", "PASS: native Movement/OC cells in build/play, removal/undo, applied retirement clears bonuses, preserves saved selection, warns and removes obsolete picker option.");
        }
        finally { window.Close(); }
    }
}

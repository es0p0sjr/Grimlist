using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckReferenceRecovery(MainWindow window, string directory, string imagePath)
    {
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var checks = new List<string>();
        foreach (var (faction, detachment, count) in new[] {
            ("Adeptus Custodes", "Shield Host", 6), ("Necrons", "Canoptek Court", 6), ("Orks", "Dread Mob", 3),
            ("Ultramarines", "Assault Brethren", 3), ("Dark Angels", "Wrath of the Rock", 6), ("Orks", "Wurrband", 0) })
        {
            var army = DetachmentRules.WithSelection(ArmyList.New(faction) with { Name = faction + " Incursion", Limit = 1000 }, [detachment]);
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Click(window, "RulesButton");
            var actual = Find<ItemsControl>(window, "StratagemCards").Items.Cast<ReferenceCard>().ToList();
            Check(actual.Count == count && actual.All(c => c.Detachment == detachment), faction + ": incorrect Rules cards");
            Check(Find<ItemsControl>(window, "CoreStratagemCards").Items.Count == 10, faction + ": missing core cards");
            Check(ArmyReferences.MissingStratagemDetachments(army, session.Dataset).Count == 0, faction + ": false missing warning");
            var selector = new DetachmentSelector();
            // The index can supply normalized Chapter references absent from the raw list.
            // Both setup and Rules must show the same resolved cards.
            var raw = session.Dataset! with {
                ReferenceCards = session.Dataset!.ReferenceCards!.Where(c => c.Faction != "Ultramarines" || c.Section != "Stratagems").ToList(),
                ReferenceCoverage = session.Dataset.ReferenceCoverage!.Where(c => c.Faction != "Ultramarines" || c.Category != "Stratagems").ToList()
            };
            selector.Begin(army, raw);
            Find<ListBox>(selector, "OptionsList").SelectedValue = detachment;
            var preview = Find<ItemsControl>(selector, "StratagemCards").Items.Cast<ReferenceCard>().ToList();
            Check(preview.Count == count && preview.All(c => c.Detachment == detachment), faction + ": preview differs from Rules");
            checks.Add($"PASS {faction} / {detachment}: {count} detachment + 10 core cards in Incursion; setup and Rules agree.");
        }
        window.UpdateLayout(); Capture(window, imagePath);
        File.WriteAllLines(imagePath + ".result.txt", checks);
    }
}

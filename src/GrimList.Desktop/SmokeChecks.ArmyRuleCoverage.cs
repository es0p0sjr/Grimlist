using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckArmyRuleCoverage(MainWindow window, string output)
    {
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var factions = session.Factions.ToList();
        var rendered = new Dictionary<string, List<string>>();
        foreach (var faction in factions)
        {
            var army = ArmyList.New(faction) with { Id = "army-rule-native-check", Name = "Army-rule coverage" };
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Click(window, "RulesButton"); window.UpdateLayout();
            var actual = Find<ItemsControl>(window, "ArmyRuleCards").Items.Cast<ReferenceCard>().Select(c => c.Name).Order().ToList();
            var expected = ArmyReferences.ForArmy(session.Army, session.Dataset).Where(c => c.Section == "Army Rules").Select(c => c.Name).Order().ToList();
            Check(actual.SequenceEqual(expected), faction + ": native rules view differs from shared reference display");
            rendered.Add(faction, actual);
            if (faction == "Tyranids") Capture(window, output + ".tyranids.png");
        }
        Check(rendered.Count == 36, "All-faction native coverage incomplete");
        Check(rendered["Tyranids"].Contains("Synapse") && rendered["Tyranids"].Contains("Shadow in the Warp"), "Tyranid army rules missing");
        Check(rendered["Astra Militarum"].Contains("Voice Of Command") && rendered["Drukhari"].Contains("Power from Pain"), "Shared-library army rules missing");
        Check(rendered["Titan Legions"].Contains("Towering Example") && rendered["Chaos Titan Legions"].Contains("Towering Example"), "Detachment-free army rules missing");
        Check(!rendered["Aeldari"].Contains("Power from Pain"), "Foreign army rule displayed");
        File.WriteAllText(output + ".result.txt", "PASS " + DataJson.Write(rendered));
    }
}

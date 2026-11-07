using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckSpaceMarineArmyRule(MainWindow window, string directory, string imagePath)
    {
        var army = ArmyList.New("Ultramarines") with { Name = "Combat Doctrines check" };
        typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
        Click(window, "RulesButton");
        var cards = Find<ItemsControl>(window, "ArmyRuleCards").Items.Cast<ReferenceCard>().ToList();
        var rule = cards.Single(c => c.Name == "Combat Doctrines");
        Check(rule.DisplayText.Contains("[ASSAULT]") && rule.DisplayText.Contains("once per battle"), "Incomplete doctrine reference");
        Check(cards.All(c => c.Name != "Oath of Moment"), "Obsolete army rule still displayed");
        window.UpdateLayout(); Capture(window, imagePath);
        File.WriteAllText(imagePath + ".result.txt", "PASS: Native Windows Army Rules displays one current Combat Doctrines card without requiring a detachment.\n" + rule.DisplayText);
    }
}

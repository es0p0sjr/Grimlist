using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckDetachmentBudget(MainWindow owner, string directory, string imagePath)
    {
        var data = new Dataset(new("Incursion UI fixture", "1", "", "", Edition: "11"), [], Detachments:
            [new("Fixture", "Large", Cost: "3 DP"), new("Fixture", "Small", Cost: "1 DP"), new("Fixture", "Peer", Cost: "1 DP")]);
        var selector = new DetachmentSelector();
        var window = new Window { Content = selector, Width = 1100, Height = 800, Owner = owner, ShowInTaskbar = false };
        try
        {
            selector.Begin(DetachmentRules.WithSelection(ArmyList.New("Fixture") with { Limit = 1000 }, []), data);
            window.Show(); window.UpdateLayout();
            void Choose(string name) { Find<ListBox>(selector, "OptionsList").SelectedValue = name; window.UpdateLayout(); }
            void Toggle() => Find<Button>(selector, "ToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Choose("Large");
            Check(Find<Button>(selector, "ToggleButton").IsEnabled, "The 3-DP option is disabled at 1,000 points.");
            Check(!Find<TextBlock>(selector, "SelectionHint").Text.Contains("available"), "Preview still claims insufficient DP.");
            Toggle();
            Check(selector.CanApply && DetachmentRules.Selected(selector.Draft).SequenceEqual(["Large"]), "Single 3-DP selection cannot be applied.");
            Check(!Find<TextBlock>(selector, "BudgetHint").Text.Contains("-1"), "Budget hint shows negative remaining DP for a valid selection.");
            Choose("Small"); Check(!Find<Button>(selector, "ToggleButton").IsEnabled, "A second detachment can be added to the 3-DP selection.");
            Choose("Large"); Toggle(); Choose("Small"); Toggle(); Choose("Peer"); Toggle();
            Check(selector.CanApply && DetachmentRules.Spent(selector.Draft, data) == 2, "Two 1-DP detachments rejected.");
            Choose("Large"); Check(!Find<Button>(selector, "ToggleButton").IsEnabled, "An over-budget combination can be added.");
            File.WriteAllText(Path.Combine(directory, "incursion.result.txt"), "PASS: native selector permits one 3-DP detachment or two 1-DP detachments, blocks over-budget additions and displays accurate hints.");
        }
        finally { window.Close(); }
    }
}

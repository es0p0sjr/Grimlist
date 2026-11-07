using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GrimList.Desktop;

public sealed partial class BattleWindow
{
    private StackPanel SecondarySection(int player)
    {
        var b = battle!;
        var key = (b.Id, player);
        var content = new StackPanel();
        AutomationProperties.SetAutomationId(content, "battle-secondaries-content-" + player);
        var heading = new DockPanel { LastChildFill = true };
        var summary = Text("", 17, true);
        var toggle = Button("", () => { }, "battle-secondaries-toggle-" + player);
        toggle.MinHeight = 44;
        toggle.Margin = new Thickness(8, 0, 0, 0);
        toggle.VerticalAlignment = VerticalAlignment.Top;
        void Update()
        {
            var collapsed = collapsedSecondaries.Contains(key);
            content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            toggle.Content = collapsed ? "Show \u25be" : "Hide \u25b4";
            AutomationProperties.SetName(toggle, (collapsed ? "Show" : "Hide") + " secondaries for " + b.Players[player].Name);
            AutomationProperties.SetHelpText(toggle, collapsed ? "Secondaries collapsed" : "Secondaries expanded");
            toggle.ToolTip = AutomationProperties.GetName(toggle);
        }
        toggle.Click += (_, _) =>
        {
            if (!collapsedSecondaries.Add(key)) collapsedSecondaries.Remove(key);
            Update();
        };
        refresh.Add(() => summary.Text = b.Summary(player, false));
        DockPanel.SetDock(toggle, Dock.Right);
        heading.Children.Add(toggle); heading.Children.Add(summary);
        body.Children.Add(heading); body.Children.Add(content);
        Update();
        return content;
    }
}

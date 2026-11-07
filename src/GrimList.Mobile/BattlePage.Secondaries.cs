namespace GrimList.Mobile;

public sealed partial class BattlePage
{
    private VerticalStackLayout SecondarySection(int player)
    {
        var b = battle!;
        var key = (b.Id, player);
        var content = new VerticalStackLayout { Spacing = 10, AutomationId = "battle-secondaries-content-" + player };
        var heading = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        var summary = Ui.Text("", 17, true);
        var toggle = Ui.Button("", () => { }, "battle-secondaries-toggle-" + player, Ui.Surface);
        toggle.MinimumHeightRequest = 48;
        toggle.VerticalOptions = LayoutOptions.Start;
        void Update()
        {
            var collapsed = collapsedSecondaries.Contains(key);
            content.IsVisible = !collapsed;
            toggle.Text = collapsed ? "Show \u25be" : "Hide \u25b4";
            SemanticProperties.SetDescription(toggle, (collapsed ? "Show" : "Hide") + " secondaries for " + b.Players[player].Name);
            SemanticProperties.SetHint(toggle, collapsed ? "Secondaries collapsed" : "Secondaries expanded");
        }
        toggle.Clicked += (_, _) =>
        {
            if (!collapsedSecondaries.Add(key)) collapsedSecondaries.Remove(key);
            Update();
        };
        refresh.Add(() => summary.Text = b.Summary(player, false));
        heading.Add(summary); heading.Add(toggle, 1);
        body.Add(heading); body.Add(content);
        Update();
        return content;
    }
}

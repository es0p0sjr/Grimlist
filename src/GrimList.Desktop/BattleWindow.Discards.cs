using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Application;

namespace GrimList.Desktop;

public sealed partial class BattleWindow
{
    private void DiscardOptions(Panel target, int player, BattleSecondary selected)
    {
        var b = battle!; var suffix = player + "-" + selected.CardId;
        var state = Text("", 14, true); target.Children.Add(state);
        refresh.Add(() => state.Text = b.SecondaryStatus(player, selected));
        var discard = Button("Discard", () => Change(() => b.DiscardCard(player, selected), true), "discard-card-" + suffix);
        refresh.Add(() => { discard.IsEnabled = b.CardRemovalProblem(player, selected) == null; discard.ToolTip = b.CardRemovalProblem(player, selected) ?? "Discard this card for the rest of the battle. CP stays unchanged."; });
        target.Children.Add(discard);
        var shuffle = Button("Shuffle Back", () => Change(() => b.ShuffleBack(player, selected), true), "shuffle-back-" + suffix);
        refresh.Add(() => { shuffle.IsEnabled = b.CardRemovalProblem(player, selected) == null; shuffle.ToolTip = b.CardRemovalProblem(player, selected) ?? "Return this card to the draw pool. CP stays unchanged."; });
        target.Children.Add(shuffle);
    }
    private void ShowDiscardPool(Panel target, int player)
    {
        var b = battle!; var panel = new StackPanel(); var cards = b.DiscardPool(player).ToList();
        if (cards.Count == 0) panel.Children.Add(Text("Discard pool is empty.", 14));
        foreach (var selected in cards)
        {
            panel.Children.Add(Text(BattleMissions.Secondary(selected.CardId, "Tactical").Name, 16, true));
            var recover = Button("Recover", () => Change(() => b.RecoverCard(player, selected), true), "recover-card-" + player + "-" + selected.CardId);
            recover.IsEnabled = b.RecoverCardProblem(player, selected) == null;
            recover.ToolTip = b.RecoverCardProblem(player, selected) ?? "Return this card to play. CP stays unchanged.";
            panel.Children.Add(recover);
        }
        var pool = new Expander { Header = $"See discard pool ({cards.Count})", Content = panel };
        AutomationProperties.SetAutomationId(pool, "discard-pool-" + player); target.Children.Add(pool);
    }
}

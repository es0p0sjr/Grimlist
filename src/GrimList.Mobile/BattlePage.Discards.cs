using GrimList.Application;

namespace GrimList.Mobile;

public sealed partial class BattlePage
{
    private void DiscardOptions(VerticalStackLayout target, int player, BattleSecondary selected)
    {
        var b = battle!; var suffix = player + "-" + selected.CardId;
        var state = Ui.Text("", 14, true); target.Add(state); refresh.Add(() => state.Text = b.SecondaryStatus(player, selected));
        var discard = Ui.Button("Discard", () => Change(() => b.DiscardCard(player, selected), true), "discard-card-" + suffix, Ui.Surface);
        refresh.Add(() => discard.IsEnabled = b.CardRemovalProblem(player, selected) == null); target.Add(discard);
        var shuffle = Ui.Button("Shuffle Back", () => Change(() => b.ShuffleBack(player, selected), true), "shuffle-back-" + suffix, Ui.Surface);
        refresh.Add(() => shuffle.IsEnabled = b.CardRemovalProblem(player, selected) == null); target.Add(shuffle);
    }
    private void ShowDiscardPool(VerticalStackLayout target, int player)
    {
        var b = battle!; var panel = new VerticalStackLayout { Spacing = 10 }; var cards = b.DiscardPool(player).ToList();
        if (cards.Count == 0) panel.Add(Ui.Text("Discard pool is empty.", 14));
        foreach (var selected in cards)
        {
            panel.Add(Ui.Text(BattleMissions.Secondary(selected.CardId, "Tactical").Name, 16, true));
            var recover = Ui.Button("Recover", () => Change(() => b.RecoverCard(player, selected), true), "recover-card-" + player + "-" + selected.CardId, Ui.Surface);
            recover.IsEnabled = b.RecoverCardProblem(player, selected) == null; panel.Add(recover);
        }
        var pool = Ui.Fold($"See discard pool ({cards.Count})", () => panel, Ui.Surface);
        pool.AutomationId = "discard-pool-" + player; target.Add(pool);
    }
}

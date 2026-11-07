using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckBattleActions(MainWindow owner, string directory, string imagePath)
    {
        var data = new Dataset(new("Battle actions fixture", "1", "2026-10-14", "", Edition: "11"), [],
            Detachments: [new("Fixture", "Orders", Dispositions: ["Take and Hold"])]);
        var army = ArmyList.New("Fixture") with { Name = "Battle actions army", Detachments = ["Orders"], Disposition = "Take and Hold" };
        var path = Path.Combine(directory, "battle-actions");
        var approveDelete = false;
        var window = new BattleWindow(army, data, path, _ => Task.FromResult(approveDelete)) { Owner = owner, ShowInTaskbar = false };
        Button Action(string id) { window.UpdateLayout(); return Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == id); }
        void Press(string id) => Action(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void WaitFor(Func<bool> ready)
        {
            async Task Wait()
            {
                for (var i = 0; i < 150; i++)
                {
                    window.UpdateLayout(); if (ready()) return; await Task.Delay(20);
                }
                throw new Exception("Battle actions UI did not finish updating");
            }
            PumpUntil(Wait());
        }
        bool Has(string id) => Descendants<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == id);
        BattleRecord Record() => (BattleRecord)typeof(BattleWindow).GetField("battle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Flush() => PumpUntil((Task)typeof(BattleWindow).GetField("pendingSave", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        IReadOnlyList<BattleRecord> Load()
        {
            var task = new BattleStore(path).LoadAsync(army.Id); PumpUntil(task); return task.GetAwaiter().GetResult();
        }
        try
        {
            window.Show(); WaitFor(() => Has("battle-start")); Press("battle-start");
            var battle = Record();
            Press("battle-random-0"); var card = battle.Players[0].Secondaries.Single();
            Check(card.DrawnRandomly && battle.DrawSlotsRemaining(0) == 1, "Random button did not draw a card");
            Action("discard-card-0-" + card.CardId).BringIntoView(); window.UpdateLayout();
            Capture(window, imagePath + ".discard-card.png");
            Press("discard-card-0-" + card.CardId);
            Check(battle.DiscardPool(0).Single().CardId == card.CardId && battle.Cp(0, 1) == 0 && battle.RandomDrawPool(0).All(c => c.Id != card.CardId),
                "Discard failed to exclude the card without changing CP");
            Check(!Has("discard-card-0-" + card.CardId), "Discarded card still appears among active cards");
            var pool = Descendants<Expander>(window).Single(e => AutomationProperties.GetAutomationId(e) == "discard-pool-0");
            pool.IsExpanded = true; window.UpdateLayout();
            Capture(window, imagePath + ".discard-pool.png");
            Press("recover-card-0-" + card.CardId);
            Check(!battle.DiscardPool(0).Any() && battle.VisibleSecondaries(0).Contains(card) && battle.Cp(0, 1) == 0,
                "Recover failed to restore the active card without CP changes");
            Press("shuffle-back-0-" + card.CardId);
            Check(battle.Players[0].Secondaries.Count == 0 && battle.RandomDrawPool(0).Any(c => c.Id == card.CardId), "Shuffle did not return card to pool");
            Press("battle-random-0"); Press("battle-next"); Press("battle-finish");
            Check(battle.Completed && battle.FinishedRound == 2 && battle.Scoreboard && battle.Total(0) == 10,
                "Early finish did not preserve scores and played round");
            Capture(window, imagePath + ".early-finish.png");
            Press("battle-finish");
            Check(!battle.Completed && !battle.Scoreboard && battle.Round == 2, "Reopen failed");
            Press("battle-history"); WaitFor(() => Has("battle-log-finish-" + battle.Id));
            Press("battle-log-finish-" + battle.Id);
            WaitFor(() => Has("battle-resume") && !Has("battle-log-finish-" + battle.Id));
            Check(Load().Single().Completed, "History finish was not saved");
            Capture(window, imagePath + ".history.png");
            Press("battle-log-delete-" + battle.Id);
            Check(Load().Count == 1 && Has("battle-log-delete-" + battle.Id), "Cancelled deletion removed a battle");
            approveDelete = true; Press("battle-log-delete-" + battle.Id);
            WaitFor(() => Has("battle-new") && !Has("battle-resume"));
            Check(Load().Count == 0 && army.Name == "Battle actions army", "Confirmed deletion did not clear only the battle");
            Check(Descendants<TextBlock>(window).Any(t => t.Text.Contains("No battles recorded")), "Empty history message missing");
            Flush();
        }
        finally { window.Close(); }
    }
}

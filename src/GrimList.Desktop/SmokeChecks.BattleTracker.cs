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
    public static void CheckBattleTracker(MainWindow owner, string directory, string imagePath)
    {
        var data = new Dataset(new("Battle UI fixture", "1", "2026-10-14", "", Edition: "11"), [],
            Detachments: [new("Fixture", "Orders", Dispositions: ["Take and Hold"])]);
        var army = ArmyList.New("Fixture") with { Name = "First player's army", Detachments = ["Orders"], Disposition = "Take and Hold" };
        var path = Path.Combine(directory, "battle-ui");
        var window = new BattleWindow(army, data, path) { Owner = owner, ShowInTaskbar = false };
        Button Action(string id) { window.UpdateLayout(); return Descendants<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == id); }
        void Press(string id) => Action(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void WaitFor(string id)
        {
            async Task Wait()
            {
                for (var i = 0; i < 150; i++)
                {
                    window.UpdateLayout();
                    if (Descendants<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == id)) return;
                    await Task.Delay(20);
                }
                throw new Exception("UI did not show " + id);
            }
            PumpUntil(Wait());
        }
        BattleRecord Record() => (BattleRecord)typeof(BattleWindow).GetField("battle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void CloseWindow()
        {
            var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); PumpUntil(closed.Task);
        }
        try
        {
            window.Show(); WaitFor("battle-start"); Capture(window, imagePath + ".setup.png");
            Press("battle-start"); WaitFor("battle-next"); var battle = Record();
            Check(battle.Total(0) == 10 && battle.Total(1) == 10, "Missing default Battle Ready totals");
            Check(Action("battle-previous").Visibility == Visibility.Collapsed, "Previous shown in round one");
            StackPanel SecondaryContent(int player) => Descendants<StackPanel>(window)
                .Single(p => AutomationProperties.GetAutomationId(p) == "battle-secondaries-content-" + player);
            Press("battle-secondaries-toggle-1");
            Check(SecondaryContent(1).Visibility == Visibility.Collapsed && SecondaryContent(0).Visibility == Visibility.Visible,
                "Secondary sections did not collapse independently");
            var first = BattleMissions.Primary(battle.Players[0].Primary).Groups[0].Conditions[0];
            Press($"score-0-{first.Id}-0-plus"); Check(battle.Total(0) == 12, "Inline condition did not update total");
            var scroll = Descendants<ScrollViewer>(window).First(s => s.Content is StackPanel);
            scroll.ScrollToBottom(); window.UpdateLayout();
            Press("battle-next"); window.UpdateLayout();
            PumpUntil(window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task);
            Check(battle.Round == 2 && scroll.VerticalOffset == 0, "Next round failed to return to top");
            Check(SecondaryContent(1).Visibility == Visibility.Collapsed, "Round navigation expanded secondaries");
            Press("battle-secondaries-toggle-1");
            var condition = BattleMissions.Primary(battle.Players[0].Primary).Groups[1].Conditions[0];
            for (var i = 0; i < 6; i++) Press($"score-0-{condition.Id}-0-plus");
            Check(battle.Total(0) == 27 && Descendants<TextBlock>(window).Any(t => t.Text.Contains("Round cap reached")), "Cap or live summary missing");
            var picker = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "battle-secondary-picker-0");
            var secondary = BattleMissions.Secondaries("Tactical").Single(c => c.Name == "No Prisoners"); picker.SelectedItem = secondary;
            var beforeFold = DataJson.Write(battle);
            var expandedHeight = ((StackPanel)scroll.Content).ActualHeight;
            Press("battle-secondaries-toggle-0"); window.UpdateLayout();
            Check(SecondaryContent(0).Visibility == Visibility.Collapsed && ((StackPanel)scroll.Content).ActualHeight < expandedHeight,
                "Collapsing secondaries did not reclaim layout space");
            Check(Action("battle-secondaries-toggle-0").IsVisible
                && AutomationProperties.GetName(Action("battle-secondaries-toggle-0")).StartsWith("Show secondaries"),
                "Restore control is hidden or inaccessible");
            Press("battle-secondaries-toggle-0");
            Check(SecondaryContent(0).Visibility == Visibility.Visible && ReferenceEquals(picker.SelectedItem, secondary)
                && beforeFold == DataJson.Write(battle), "Toggling changed battle data or lost the pending card selection");
            Press("battle-add-secondary-0");
            for (var i = 0; i < 3; i++) Press($"score-0-{secondary.Groups[0].Conditions[0].Id}-0-plus");
            Check(battle.Total(0) == 32 && battle.Players[0].Secondaries.Count == 1, "Secondary picker or condition control failed");
            Press("battle-secondaries-toggle-0");
            Action("battle-secondaries-toggle-0").BringIntoView(); window.UpdateLayout();
            Capture(window, imagePath + ".secondaries-collapsed.png");
            Press("battle-secondaries-toggle-0");
            Check(battle.Total(0) == 32, "Collapsing scored secondaries changed VP");
            Check(!Action("discard-card-0-" + secondary.Id).IsEnabled, "Discard would erase achieved scores");
            var secondCard = BattleMissions.Secondaries("Tactical").Single(c => c.Name == "Centre Ground");
            picker = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "battle-secondary-picker-0");
            picker.SelectedItem = secondCard; Press("battle-add-secondary-0");
            var discard = Action("discard-card-0-" + secondCard.Id);
            Check((string)discard.Content == "Discard", "Discard label is not simplified");
            Check(!Descendants<Expander>(window).Any(e => e.Header as string == "Discard / replace card"), "Extra discard controls remain");
            discard.BringIntoView(); window.UpdateLayout(); Capture(window, imagePath + ".discard-card.png");
            Press("discard-card-0-" + secondCard.Id);
            Check(battle.Cp(0, 2) == 0 && battle.Players[0].Secondaries.Count == 2 && battle.DrawSlotsRemaining(0) == 1,
                "Simple discard changed CP or failed to restore draw slot");
            window.UpdateLayout();
            void Counter(string label) => Descendants<Button>(window).First(c => AutomationProperties.GetName(c) == "Increase " + label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Counter("Core CP gained"); Counter("Extra CP gained (max 1/round)"); Counter("CP spent");
            Check(battle.Cp(0, 2) == 1 && battle.CpSpent(0, 2) == 1, "Manual CP controls failed");
            picker = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "battle-secondary-picker-0");
            var replacementCard = BattleMissions.Secondaries("Tactical").Single(c => c.Name == "Plunder");
            Check(!picker.Items.Cast<BattleMission>().Any(c => c.Id == secondCard.Id), "Discarded card can still be selected");
            picker.SelectedItem = replacementCard; Press("battle-add-secondary-0"); Press("shuffle-back-0-" + replacementCard.Id);
            Check(battle.RoundScoreboard(0, 2).ShuffledBack.Single() == "Plunder", "Shuffle history missing");
            picker = Descendants<ComboBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "battle-secondary-picker-0");
            picker.SelectedItem = replacementCard; Press("battle-add-secondary-0"); Press("discard-card-0-" + replacementCard.Id);
            Check(battle.Cp(0, 2) == 1 && battle.DiscardHistory(0, 2).Count() == 2 && !battle.CommandRedrawUsed(0),
                "Repeated simple discard changed manual CP/history");
            scroll.ScrollToTop(); window.UpdateLayout();
            Capture(window, imagePath);
            window.Width = 550; window.Height = 700; window.UpdateLayout(); Capture(window, imagePath + ".narrow.png");
            window.Width = 850; window.Height = 920;
            Press("battle-previous"); Check(battle.Round == 1 && battle.Total(0) == 32, "Navigation erased scores");
            for (var i = 0; i < 5; i++) Press("battle-next");
            Check(battle.Scoreboard && battle.Round == 5 && Descendants<TextBlock>(window).Any(t => t.Text.Contains("ROUND 1")), "Final scoreboard missing");
            var headings = Descendants<TextBlock>(window).Where(t => t.Text.StartsWith("ROUND ")).Select(t => t.Text).ToList();
            Check(headings.SequenceEqual(Enumerable.Range(1, 5).Select(r => "ROUND " + r)), "Scoreboard rounds are not in order");
            Check(Descendants<TextBlock>(window).Any(t => t.Text.Contains("Scored: No Prisoners"))
                && Descendants<TextBlock>(window).Any(t => t.Text.Contains("Discarded: Centre Ground, Plunder"))
                && !Descendants<TextBlock>(window).Any(t => t.Text.Contains(secondary.Groups[0].Conditions[0].Text)), "Scoreboard missing cards or still contains condition explanations");
            Capture(window, imagePath + ".scoreboard.png");
            Press("battle-next"); Check(battle.Completed, "Finish battle did not persist completion");
            PumpUntil((Task)typeof(BattleWindow).GetField("pendingSave", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            var loading = new BattleStore(path).LoadAsync(army.Id); PumpUntil(loading);
            var loaded = loading.GetAwaiter().GetResult().Single();
            Check(loaded.Completed && loaded.Total(0) == 32 && loaded.Cp(0, 2) == 1 && loaded.CpSpent(0, 2) == 1, "UI state not saved");
            CloseWindow();
            window = new BattleWindow(army, data, path) { Owner = owner, ShowInTaskbar = false };
            window.Show(); WaitFor("battle-resume"); Press("battle-resume"); WaitFor("battle-next");
            Check(Record().Completed && Record().Scoreboard && Record().Total(0) == 32 && Record().DiscardHistory(0, 2).Count() == 2, "Reopened UI did not resume saved result");
            CloseWindow();
            CheckBattleActions(owner, directory, imagePath);
            CheckBattleExchange(owner, directory, imagePath);
            File.WriteAllText(imagePath + ".result.txt", "PASS battle setup, live counters, cap indicator, secondary picker/scoring, achieved-card protection, simple discard, manual CP, discard history, round navigation, narrow layout, scoreboard, save and reopen through lobby; random draw, simple discard, early finish/reopen, finish from history, cancelled and confirmed deletion; host invitation, guest join/response, host acceptance, identical primary assignments and compact player details and invitation QR\n");
        }
        finally { window.Close(); }
    }
}

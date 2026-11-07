using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckPrimaryMissions(MainWindow owner, string directory, string imagePath)
    {
        var path = Path.Combine(directory, "primary-fixture");
        using var store = WindowsServices.OpenStore(path);
        string[] dispositions = ["Take and Hold", "Purge the Foe", "Disruption", "Reconnaissance", "Priority Assets"];
        var data = new Dataset(new("Primary UI fixture", "1", "2026-10-14", "", Edition: "11"),
            [new("Fixture", "Unit", [new(1, 50)])], Detachments: [new("Fixture", "Orders", Cost: "1 DP", Dispositions: [.. dispositions])]);
        store.Import(data);
        var army = ArmyList.New("Fixture") with { Detachments = ["Orders"], Disposition = dispositions[0] };
        store.Save(army);
        var window = new MainWindow(path) { Owner = owner, ShowInTaskbar = false };
        string? failure = null;
        window.ContentRendered += (_, _) =>
        {
            try
            {
                typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
                Click(window, "RulesButton");
                var picker = Find<ComboBox>(window, "DispositionPicker");
                var rows = Find<ItemsControl>(window, "PrimaryMatchups");
                foreach (var disposition in dispositions)
                {
                    picker.SelectedItem = disposition;
                    Check(rows.Items.Count == 5, "Expected all five matchups for " + disposition);
                    Check(rows.Items.Cast<PrimaryMatchup>().SequenceEqual(DispositionPrimaries.ForArmy(army with { Disposition = disposition }, data).Matchups), "Stale primary rows");
                    Check(Find<TextBlock>(window, "PrimaryMissionsTitle").Text.Contains(disposition), "Stale selected disposition heading");
                }
                Click(window, "RulesUndoButton");
                Check(((PrimaryMatchup)rows.Items[0]).YourPrimary == "Reconnaissance Sweep", "Undo did not refresh primary rows");
                Click(window, "RulesRedoButton");
                Check(((PrimaryMatchup)rows.Items[0]).YourPrimary == "Secure Asset", "Redo did not refresh primary rows");
                picker.SelectedItem = "Take and Hold";
                window.UpdateLayout(); Capture(window, imagePath);
                CheckPrimaryCardDialog(window, (PrimaryMatchup)rows.Items[0], imagePath);
                DetachmentAction(window, "CurrentDetachments", "Orders").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(rows.Items.Count == 0 && Find<TextBlock>(window, "PrimaryMissionsHint").Text.Contains("Add a detachment"), "Removal retained stale primaries");
                Click(window, "RulesUndoButton"); Check(rows.Items.Count == 5, "Undo removal did not restore primary rows");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { window.Close(); }
        };
        window.ShowDialog(); Check(failure == null, failure ?? "");
        File.WriteAllText(imagePath + ".result.txt", "PASS: All five dispositions render five primary matchups; selection, undo, redo and detachment removal refresh the Windows Army Rules view. Mission card dialog loads asynchronously, retries errors and cancels on close.");
    }

    private static void CheckPrimaryCardDialog(Window owner, PrimaryMatchup matchup, string imagePath)
    {
        var sourcePath = Path.Combine(Path.GetDirectoryName(imagePath)!, "card-source.html");
        var card = File.Exists(sourcePath)
            ? PrimaryMissionCards.Parse(File.ReadAllText(sourcePath), DateTimeOffset.UtcNow)[UnitIdentity.Normalize(matchup.YourPrimary)]
            : new PrimaryMissionCard(matchup.YourPrimary, "Synthetic primary scoring fixture: WHEN: End of turn. 2VP", DateTimeOffset.UtcNow);
        var attempts = 0;
        var dialog = new PrimaryMissionWindow(matchup, async (_, token) =>
        {
            if (++attempts == 1) throw new System.Net.Http.HttpRequestException("Offline fixture");
            await Task.Delay(100, token); return card;
        }) { Owner = owner };
        string? failure = null;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                Check(Find<Button>(dialog, "RetryButton").Visibility == Visibility.Visible, "Failed load has no retry");
                Click(dialog, "RetryButton");
                Check(Find<TextBlock>(dialog, "CardText").Text.StartsWith("Loading"), "Card load did not yield to the UI");
                for (var i = 0; i < 100 && Find<TextBlock>(dialog, "CardText").Text.StartsWith("Loading"); i++) await Task.Delay(20);
                Check(Find<TextBlock>(dialog, "CardText").Text == card.Text, "Retry did not display complete card");
                Check(Find<TextBlock>(dialog, "MissionName").Text == matchup.YourPrimary, "Wrong primary card opened");
                dialog.UpdateLayout(); Capture(dialog, imagePath + ".card.png");
            }
            catch (Exception ex) { failure = ex.ToString(); }
            finally { dialog.Close(); }
        };
        dialog.ShowDialog(); Check(failure == null, failure ?? "");
        CancellationToken pendingToken = default;
        var pending = new PrimaryMissionWindow(matchup, async (_, token) =>
        { pendingToken = token; await Task.Delay(Timeout.Infinite, token); return card; }) { Owner = owner };
        pending.ContentRendered += (_, _) => pending.Close();
        pending.ShowDialog(); Check(pendingToken.IsCancellationRequested, "Closing a loading card did not cancel its request");
    }
}

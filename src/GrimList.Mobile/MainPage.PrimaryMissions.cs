using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private async Task ShowPrimaryMission(PrimaryMatchup matchup)
    {
        using var cancellation = new CancellationTokenSource();
        var text = Ui.Text("Loading mission card…", 16);
        var source = Ui.Text("Internet is required to load the published card.", 12, colour: Ui.Muted);
        var retry = Ui.Button("Retry", () => { }, "retry-primary-card", Accent); retry.IsVisible = false;
        var body = new VerticalStackLayout { Padding = 18, Spacing = 14, Children =
        {
            Ui.Text(matchup.YourPrimary, 23, true), Ui.Text("YOUR PRIMARY MISSION", 12, true),
            Ui.Text("Against " + matchup.OpponentDisposition, 14), Ui.Card(text, Ui.Surface), source, retry
        } };
        var page = new ContentPage { Title = matchup.YourPrimary, BackgroundColor = Ui.Background };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Disappearing += (_, _) => { cancellation.Cancel(); closed.TrySetResult(); };
        var sourceButton = Ui.Button("Open card source", async () =>
        {
            try { await Microsoft.Maui.ApplicationModel.Launcher.Default.OpenAsync(PrimaryMissionCards.SourceUrl); }
            catch (Exception) { source.Text = "Could not open the browser. Source: " + PrimaryMissionCards.SourceUrl; }
        }, "primary-card-source", Ui.Surface);
        body.Add(sourceButton);
        var grid = new Grid { RowDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Padding = 8 };
        grid.Add(Ui.Scroll(body));
        grid.Add(Ui.Button("Close", async () => await Navigation.PopModalAsync(), "close-primary-card", Accent), 0, 1);
        page.Content = grid;
        async Task LoadCard()
        {
            retry.IsVisible = false; text.Text = "Loading mission card…";
            try
            {
                var card = await PrimaryMissionCards.Shared.GetAsync(matchup.YourPrimary, cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                text.Text = card.Text.Replace(new string('=', 43), new string('=', 36), StringComparison.Ordinal);
                source.Text = PrimaryMissionCards.SourceLabel + "\nRetrieved " + card.RetrievedAt.ToLocalTime().ToString("g");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception)
            {
                if (cancellation.IsCancellationRequested) return;
                text.Text = "The mission card could not be loaded. Check your connection and retry, or open the card source.";
                retry.IsVisible = true;
            }
        }
        retry.Clicked += async (_, _) => await LoadCard();
        await Navigation.PushModalAsync(page);
        await LoadCard();
        await closed.Task;
    }
}

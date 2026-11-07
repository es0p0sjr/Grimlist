using System.Diagnostics;
using System.Windows;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class PrimaryMissionWindow : Window
{
    private readonly string mission;
    private readonly Func<string, CancellationToken, Task<PrimaryMissionCard>> load;
    private readonly CancellationTokenSource cancellation = new();
    public PrimaryMissionWindow(PrimaryMatchup matchup, Func<string, CancellationToken, Task<PrimaryMissionCard>>? loader = null)
    {
        InitializeComponent(); DarkWindow.Apply(this);
        mission = matchup.YourPrimary; load = loader ?? PrimaryMissionCards.Shared.GetAsync;
        Title = mission; MissionName.Text = mission; MatchupText.Text = "Against " + matchup.OpponentDisposition;
        Loaded += async (_, _) => await LoadCard();
        Closed += (_, _) => cancellation.Cancel();
    }
    private async Task LoadCard()
    {
        RetryButton.Visibility = Visibility.Collapsed; CardText.Text = "Loading mission card…";
        CardSource.Text = "Internet is required to load the published card.";
        try
        {
            var card = await load(mission, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            CardText.Text = card.Text;
            CardSource.Text = PrimaryMissionCards.SourceLabel + "\nRetrieved " + card.RetrievedAt.ToLocalTime().ToString("g");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (cancellation.IsCancellationRequested) return;
            CardText.Text = "The mission card could not be loaded. Check your connection and retry, or open the card source.";
            RetryButton.Visibility = Visibility.Visible;
        }
    }
    private async void Retry(object sender, RoutedEventArgs e) => await LoadCard();
    private void CloseCard(object sender, RoutedEventArgs e) => Close();
    private void OpenSource(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(PrimaryMissionCards.SourceUrl) { UseShellExecute = true }); }
        catch (Exception) { CardSource.Text = "Could not open the browser. Source: " + PrimaryMissionCards.SourceUrl; }
    }
}

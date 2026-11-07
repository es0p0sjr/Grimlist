using System.Windows;
using System.Windows.Automation.Peers;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private string? errorEntryId;
    private void ShowError(string message)
    {
        if (detailEntryId == null || army.Entries.All(e => e.Id != detailEntryId)) return;
        errorEntryId = detailEntryId;
        ErrorMessage.Text = message;
        RefreshError();
        ErrorBanner.BringIntoView();
        var peer = UIElementAutomationPeer.FromElement(ErrorMessage) ?? UIElementAutomationPeer.CreatePeerForElement(ErrorMessage);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void ClearError()
    {
        errorEntryId = null;
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorMessage.Text = "";
    }
    private void RefreshError() => ErrorBanner.Visibility = !playMode && errorEntryId != null && errorEntryId == detailEntryId
        ? Visibility.Visible : Visibility.Collapsed;
    private void DismissError(object sender, RoutedEventArgs e) => ClearError();
}

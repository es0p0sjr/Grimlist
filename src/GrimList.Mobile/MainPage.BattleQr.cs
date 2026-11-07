namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private bool handlingBattleQr;
    public async Task HandleIncomingBattleQr()
    {
        if (!initialized || handlingBattleQr || !BattleQrInbox.HasPending) return;
        handlingBattleQr = true;
        try
        {
            if (Navigation.ModalStack.LastOrDefault() is BattlePage battlePage) await battlePage.ReceiveQrAsync();
            else if (Navigation.ModalStack.Count > 0)
                await DisplayAlertAsync("Battle QR received", "Close this screen, then open Battle tracker with the army you want to play.", "OK");
            else if (Session.IsOpen)
                await Navigation.PushModalAsync(new BattlePage(Session.Army, Session.Dataset, FileSystem.Current.AppDataDirectory));
            else await DisplayAlertAsync("Battle QR received", "Open the army you want to play, then open its Battle tracker to review the invitation.", "OK");
        }
        catch (Exception ex) { await DisplayAlertAsync("Battle QR", ex.Message, "OK"); }
        finally { handlingBattleQr = false; }
    }
}

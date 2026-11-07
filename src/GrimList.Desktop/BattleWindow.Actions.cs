using GrimList.Application;

namespace GrimList.Desktop;

public sealed partial class BattleWindow
{
    private async Task ShowHistory()
    {
        await pendingSave;
        if (!await Save()) return;
        battle = null; await Lobby(false);
    }
    private async Task FinishFromHistory(BattleRecord record)
    {
        try { record.Finish(); await store.SaveAsync(record); await Lobby(false); }
        catch (Exception ex) { status.Text = "Could not finish battle: " + ex.Message; }
    }
    private async Task DeleteBattle(BattleRecord record)
    {
        if (!await confirmDeletion(record)) return;
        try
        {
            await pendingSave; await store.DeleteAsync(record.Id);
            exchanges.Delete(record.Id);
            if (battle?.Id == record.Id) battle = null;
            await Lobby(false);
        }
        catch (Exception ex) { status.Text = "Could not delete battle: " + ex.Message; }
    }
}

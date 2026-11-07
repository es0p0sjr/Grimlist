using GrimList.Application;

namespace GrimList.Mobile;

public sealed partial class BattlePage
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
        if (!await DisplayAlertAsync("Delete battle?", $"Delete battle {record.Code} ({string.Join(" vs ", record.Players.Select(p => p.Name))})? Its saved scores and history will be removed. Your army is kept.", "Delete", "Cancel")) return;
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

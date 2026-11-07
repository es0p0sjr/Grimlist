using GrimList.Application;
using GrimList.Core;

namespace GrimList.Mobile;

public sealed partial class BattlePage
{
    private readonly Dataset? battleData;
    private readonly BattleExchangeStore exchanges;
    private bool exchangeBusy;
    private string playerName = "";
    private bool exchangeView;
    private async Task ExchangeAction(Func<Task> action)
    {
        if (exchangeBusy) return;
        exchangeBusy = true; body.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { exchangeBusy = false; body.IsEnabled = true; }
    }
    private void ExchangeHeader(string title, Action back)
    {
        Reset(); exchangeView = true; header.Add(Ui.Text(title, 24, true)); header.Add(Ui.Button("Back", back));
        Dispatcher.Dispatch(async () => await scroll.ScrollToAsync(0, 0, false));
    }
    private (Picker Mode, Picker One, Picker Two) ExchangeMode(string title)
    {
        body.Add(Ui.Text(title, 18, true));
        var mode = Ui.Picker("Secondary mode"); mode.ItemsSource = new[] { "Tactical", "Fixed" }; mode.SelectedIndex = 0;
        var choices = BattleMissions.Secondaries("Fixed").ToList();
        var panel = new VerticalStackLayout { Spacing = 8, IsVisible = false };
        Picker Pick(int index) { var p = Ui.Picker("Fixed mission"); p.ItemsSource = choices; p.ItemDisplayBinding = new Binding("Name"); p.SelectedIndex = index; return p; }
        var one = Pick(0); var two = Pick(1); panel.Add(one); panel.Add(two);
        mode.SelectedIndexChanged += (_, _) => panel.IsVisible = mode.SelectedIndex == 1;
        body.Add(mode); body.Add(panel); return (mode, one, two);
    }
    private async Task ExchangeHome()
    {
        ExchangeHeader("Play with another GrimList", async () => await Lobby(false));
        status.Text = "Share your name, faction and disposition. Scores stay on this device.";
        body.Add(Ui.Text("Discuss your army lists in person. Only your player name, faction and disposition are shared.", 14));
        body.Add(Ui.Button("Join / import response", ImportExchangeScreen, "exchange-import"));
        if (matchups.Matchups.Count > 0)
        {
            var name = PlayerNameInput();
            body.Add(Ui.Button("Show invitation QR", async () => await ExchangeAction(async () =>
            {
                playerName = name.Text.Trim();
                var draft = await Task.Run(() => BattleExchange.Invite(army, battleData, playerName));
                await exchanges.SaveAsync(draft); ShowExchange(draft);
            }), "exchange-create"));
        }
        else body.Add(Ui.Text(matchups.Hint));
        foreach (var draft in await exchanges.LoadAsync(army.Id))
            body.Add(Ui.Button($"{draft.Packet.Code} · {(draft.Packet.Kind == "invitation" ? "Waiting for opponent" : "View exchange")}", () => ShowExchange(draft), "exchange-saved", Ui.Surface));
    }
    private Entry PlayerNameInput()
    {
        body.Add(Ui.Text("Your player name", 14));
        var name = Ui.Entry("Your name", playerName, "exchange-player-name"); name.MaxLength = 80; body.Add(name);
        body.Add(Ui.Text($"Army: {army.Faction}\nDisposition: {army.Disposition}", 15));
        return name;
    }
    private void AddSharedArmy(BattleArmyShare share)
    {
        var panel = new VerticalStackLayout { Spacing = 8 }; panel.Add(Ui.Text(share.Name, 20, true));
        panel.Add(Ui.Text($"Army: {share.Faction}\nDisposition: {share.Disposition}", 15));
        body.Add(Ui.Card(panel, Ui.Surface));
    }
    private void ShowExchange(BattleExchangeDraft draft)
    {
        ExchangeHeader("Battle " + draft.Packet.Code, async () => await ExchangeAction(ExchangeHome));
        status.Text = draft.Packet.Kind == "invitation" ? "Let your opponent scan this invitation, then import their response." : "Both players are saved for this battle.";
        if (draft.Packet.Version == 3) AddQr(draft.Packet);
        if (draft.Packet.Version == 3) body.Add(Ui.Button("Copy exchange", async () => await ExchangeAction(async () => { await Clipboard.Default.SetTextAsync(BattleExchange.Encode(draft.Packet)); status.Text = "Exchange copied. Send it to your opponent."; }), "exchange-copy", Ui.Surface));
        if (draft.Packet.Kind == "invitation") body.Add(Ui.Button("Import opponent response", ImportExchangeScreen, "exchange-response"));
        else body.Add(Ui.Button("Open battle", async () => await ExchangeAction(() => OpenExchangeBattle(draft)), "exchange-open"));
        AddSharedArmy(draft.Packet.Host);
        if (draft.Packet.Guest is { } guest)
        {
            AddSharedArmy(guest); var m = DispositionPrimaries.Matchup(draft.Packet.Host.Disposition, guest.Disposition);
            body.Add(Ui.Text($"{draft.Packet.Host.Name}: {m.YourPrimary}\n{guest.Name}: {m.OpponentPrimary}", 17, true));
        }
    }
    private async Task OpenExchangeBattle(BattleExchangeDraft draft)
    {
        var existing = await exchanges.FindBattleAsync(draft, store);
        if (existing != null) { battle = existing; Render(); return; }
        ExchangeHeader("Start battle " + draft.Packet.Code, () => ShowExchange(draft));
        body.Add(Ui.Text("Who takes the first turn?", 18, true));
        body.Add(Ui.Text("Choose the agreed first player on both devices.", 14));
        var first = Ui.Picker("First turn", "exchange-first");
        first.ItemsSource = new[] { draft.Packet.Host.Name + (draft.IsHost ? " (You)" : " (Opponent)"),
            draft.Packet.Guest!.Name + (draft.IsHost ? " (Opponent)" : " (You)") };
        first.SelectedIndex = -1; body.Add(first);
        body.Add(Ui.Text("Agree secondary missions in person and enter them here. These choices are not shared.", 14));
        var yours = ExchangeMode("Your secondary missions"); var theirs = ExchangeMode("Opponent secondary missions");
        var start = Ui.Button("Start battle", async () => await ExchangeAction(async () =>
        {
            if (first.SelectedIndex < 0) throw new InvalidDataException("Choose who takes the first turn.");
            battle = await exchanges.StartAsync(draft, store, first.SelectedIndex == 0,
                (string)yours.Mode.SelectedItem, (string)theirs.Mode.SelectedItem,
                new[] { ((BattleMission)yours.One.SelectedItem).Id, ((BattleMission)yours.Two.SelectedItem).Id },
                new[] { ((BattleMission)theirs.One.SelectedItem).Id, ((BattleMission)theirs.Two.SelectedItem).Id }); Render();
        }), "exchange-start");
        start.IsEnabled = false;
        first.SelectedIndexChanged += (_, _) => start.IsEnabled = first.SelectedIndex >= 0;
        body.Add(start);
    }
    private void ImportExchangeScreen()
    {
        ExchangeHeader("Join / import response", async () => await ExchangeAction(ExchangeHome));
        body.Add(Ui.Button("Open exchange file", async () => await ExchangeAction(async () =>
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Open GrimList battle exchange" });
            if (picked == null) return;
            using var file = await picked.OpenReadAsync(); await ReviewExchange(BattleQr.Read(await BattleExchange.ReadAsync(file)));
        }), "exchange-load"));
        body.Add(Ui.Text("Paste the QR link or full exchange copied by your opponent. The short battle code is only a label.", 14));
        var pasted = new Editor { HeightRequest = 140, MaxLength = BattleExchange.MaxText, Placeholder = "Paste invitation or response", TextColor = Colors.White, BackgroundColor = Ui.Input }; body.Add(pasted);
        body.Add(Ui.Button("Paste from clipboard", async () => await ExchangeAction(async () => pasted.Text = await Clipboard.Default.GetTextAsync()), "exchange-paste", Ui.Surface));
        body.Add(Ui.Button("Read pasted exchange", async () => await ExchangeAction(() => ReviewExchange(BattleQr.Read(pasted.Text ?? ""))), "exchange-read"));
    }
    private async Task ReviewExchange(BattleExchangePacket packet)
    {
        var drafts = await exchanges.LoadAsync(army.Id); var prior = drafts.SingleOrDefault(d => d.Packet.BattleId == packet.BattleId);
        if (packet.Kind == "invitation" && prior != null)
        {
            if (prior.IsHost) throw new InvalidDataException("This is your own invitation. Ask the opponent to return their response.");
            if (BattleExchange.Hash(packet) != prior.Packet.InvitationHash) throw new InvalidDataException("This invitation differs from the battle you already joined.");
            ShowExchange(prior); return;
        }
        if (packet.Version != 3 && packet.Kind == "invitation")
            throw new InvalidDataException("Ask your opponent to update GrimList and generate a new QR invitation.");
        ExchangeHeader("Review battle " + packet.Code, ImportExchangeScreen); AddSharedArmy(packet.Host);
        if (packet.Kind == "response")
        {
            if (prior == null) throw new InvalidDataException("No matching invitation was created with this army on this device.");
            var accepted = BattleExchange.Accept(prior, packet); AddSharedArmy(packet.Guest!);
            body.Add(Ui.Button("Accept opponent", async () => await ExchangeAction(async () => { await exchanges.SaveAsync(accepted); ShowExchange(accepted); }), "exchange-accept"));
        }
        else
        {
            if (matchups.Matchups.Count == 0) throw new InvalidDataException(matchups.Hint);
            var matchup = DispositionPrimaries.Matchup(army.Disposition, packet.Host.Disposition);
            body.Add(Ui.Text($"Your army: {army.Name}\nYour primary: {matchup.YourPrimary}\nOpponent primary: {matchup.OpponentPrimary}", 17, true));
            var name = PlayerNameInput();
            body.Add(Ui.Button("Join & create response", async () => await ExchangeAction(async () =>
            {
                playerName = name.Text.Trim();
                var draft = await Task.Run(() => BattleExchange.Join(packet, army, battleData, playerName));
                await exchanges.SaveAsync(draft); ShowExchange(draft);
            }), "exchange-join"));
        }
    }
    private void SharedArmies()
    {
        var b = battle!; var packet = b.Exchange!;
        ExchangeHeader("Battle players · " + b.Code, () => Render()); AddSharedArmy(packet.Host); AddSharedArmy(packet.Guest!);

    }
}

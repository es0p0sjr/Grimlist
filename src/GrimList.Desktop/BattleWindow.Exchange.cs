using System.IO;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;

public sealed partial class BattleWindow
{
    private readonly Dataset? battleData;
    private readonly BattleExchangeStore exchanges;
    private bool exchangeBusy;
    private string playerName = "";
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
        Reset(); header.Children.Add(Text(title, 24, true));
        header.Children.Add(Button("Back", back)); scroll.ScrollToTop();
    }
    private (ComboBox Mode, ComboBox One, ComboBox Two) ExchangeMode(string title)
    {
        body.Children.Add(Text(title, 18, true));
        var mode = new ComboBox { ItemsSource = new[] { "Tactical", "Fixed" }, SelectedIndex = 0, MinHeight = 40 };
        var choices = BattleMissions.Secondaries("Fixed").ToList();
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        ComboBox Pick(int index) => new() { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = index, MinHeight = 40 };
        var one = Pick(0); var two = Pick(1); panel.Children.Add(one); panel.Children.Add(two);
        mode.SelectionChanged += (_, _) => panel.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(mode); body.Children.Add(panel); return (mode, one, two);
    }
    private async Task ExchangeHome()
    {
        ExchangeHeader("Play with another GrimList", async () => await Lobby(false));
        status.Text = "Share your name, faction and disposition. Scores stay on this device.";
        body.Children.Add(Text("Discuss your army lists in person. Only your player name, faction and disposition are shared.", 14));
        body.Children.Add(Button("Join / import response", ImportExchangeScreen, "exchange-import"));
        if (matchups.Matchups.Count > 0)
        {
            var name = PlayerNameInput();
            body.Children.Add(Button("Show invitation QR", async () => await ExchangeAction(async () =>
            {
                playerName = name.Text.Trim();
                var draft = await Task.Run(() => BattleExchange.Invite(army, battleData, playerName));
                await exchanges.SaveAsync(draft); ShowExchange(draft);
            }), "exchange-create"));
        }
        else body.Children.Add(Text(matchups.Hint));
        foreach (var draft in await exchanges.LoadAsync(army.Id))
            body.Children.Add(Button($"{draft.Packet.Code} · {(draft.Packet.Kind == "invitation" ? "Waiting for opponent" : "View exchange")}", () => ShowExchange(draft), "exchange-saved"));
    }
    private TextBox PlayerNameInput()
    {
        body.Children.Add(Text("Your player name", 14));
        var name = new TextBox { Text = playerName, MaxLength = 80, MinHeight = 40 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(name, "exchange-player-name"); body.Children.Add(name);
        body.Children.Add(Text($"Army: {army.Faction}\nDisposition: {army.Disposition}", 15));
        return name;
    }
    private void AddSharedArmy(BattleArmyShare share)
    {
        var panel = new StackPanel(); panel.Children.Add(Text(share.Name, 20, true));
        panel.Children.Add(Text($"Army: {share.Faction}\nDisposition: {share.Disposition}", 15)); body.Children.Add(Card(panel));
    }
    private void ShowExchange(BattleExchangeDraft draft)
    {
        ExchangeHeader("Battle " + draft.Packet.Code, async () => await ExchangeAction(ExchangeHome));
        status.Text = draft.Packet.Kind == "invitation" ? "Let your opponent scan this invitation, then import their response." : "Both players are saved for this battle.";
        if (draft.Packet.Version == 3) AddQr(draft.Packet);
        if (draft.Packet.Version == 3) body.Children.Add(Button("Copy exchange", () => { Clipboard.SetText(BattleExchange.Encode(draft.Packet)); status.Text = "Exchange copied. Send it to your opponent."; }, "exchange-copy"));
        if (draft.Packet.Kind == "invitation") body.Children.Add(Button("Import opponent response", ImportExchangeScreen, "exchange-response"));
        else body.Children.Add(Button("Open battle", async () => await ExchangeAction(() => OpenExchangeBattle(draft)), "exchange-open"));
        AddSharedArmy(draft.Packet.Host);
        if (draft.Packet.Guest is { } guest)
        {
            AddSharedArmy(guest); var m = DispositionPrimaries.Matchup(draft.Packet.Host.Disposition, guest.Disposition);
            body.Children.Add(Text($"{draft.Packet.Host.Name}: {m.YourPrimary}\n{guest.Name}: {m.OpponentPrimary}", 17, true));
        }
    }
    private async Task OpenExchangeBattle(BattleExchangeDraft draft)
    {
        var existing = await exchanges.FindBattleAsync(draft, store);
        if (existing != null) { battle = existing; Render(); return; }
        ExchangeHeader("Start battle " + draft.Packet.Code, () => ShowExchange(draft));
        body.Children.Add(Text("Who takes the first turn?", 18, true));
        body.Children.Add(Text("Choose the agreed first player on both devices.", 14));
        var first = new ComboBox
        {
            ItemsSource = new[] { draft.Packet.Host.Name + (draft.IsHost ? " (You)" : " (Opponent)"),
                draft.Packet.Guest!.Name + (draft.IsHost ? " (Opponent)" : " (You)") },
            SelectedIndex = -1, MinHeight = 40
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(first, "exchange-first");
        body.Children.Add(first);
        body.Children.Add(Text("Agree secondary missions in person and enter them here. These choices are not shared.", 14));
        var yours = ExchangeMode("Your secondary missions"); var theirs = ExchangeMode("Opponent secondary missions");
        var start = Button("Start battle", async () => await ExchangeAction(async () =>
        {
            if (first.SelectedIndex < 0) throw new InvalidDataException("Choose who takes the first turn.");
            battle = await exchanges.StartAsync(draft, store, first.SelectedIndex == 0,
                (string)yours.Mode.SelectedItem, (string)theirs.Mode.SelectedItem,
                new[] { ((BattleMission)yours.One.SelectedItem).Id, ((BattleMission)yours.Two.SelectedItem).Id },
                new[] { ((BattleMission)theirs.One.SelectedItem).Id, ((BattleMission)theirs.Two.SelectedItem).Id }); Render();
        }), "exchange-start");
        start.IsEnabled = false;
        first.SelectionChanged += (_, _) => start.IsEnabled = first.SelectedIndex >= 0;
        body.Children.Add(start);
    }
    private void ImportExchangeScreen()
    {
        ExchangeHeader("Join / import response", async () => await ExchangeAction(ExchangeHome));
        body.Children.Add(Button("Open exchange file", async () => await ExchangeAction(async () =>
        {
            var dialog = new OpenFileDialog { Filter = "GrimList battle exchange|*.grimbattle|All files|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            using var file = File.OpenRead(dialog.FileName); await ReviewExchange(BattleQr.Read(await BattleExchange.ReadAsync(file)));
        }), "exchange-load"));
        body.Children.Add(Text("Paste the QR link or full exchange copied by your opponent. The short battle code is only a label.", 14));
        var pasted = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, MaxLength = BattleExchange.MaxText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(pasted);
        body.Children.Add(Button("Read pasted exchange", async () => await ExchangeAction(() => ReviewExchange(BattleQr.Read(pasted.Text))), "exchange-read"));
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
        ExchangeHeader("Review battle " + packet.Code, ImportExchangeScreen);
        AddSharedArmy(packet.Host);
        if (packet.Kind == "response")
        {
            if (prior == null) throw new InvalidDataException("No matching invitation was created with this army on this device.");
            var accepted = BattleExchange.Accept(prior, packet); AddSharedArmy(packet.Guest!);
            body.Children.Add(Button("Accept opponent", async () => await ExchangeAction(async () => { await exchanges.SaveAsync(accepted); ShowExchange(accepted); }), "exchange-accept"));
        }
        else
        {
            if (matchups.Matchups.Count == 0) throw new InvalidDataException(matchups.Hint);
            var matchup = DispositionPrimaries.Matchup(army.Disposition, packet.Host.Disposition);
            body.Children.Add(Text($"Your army: {army.Name}\nYour primary: {matchup.YourPrimary}\nOpponent primary: {matchup.OpponentPrimary}", 17, true));
            var name = PlayerNameInput();
            body.Children.Add(Button("Join & create response", async () => await ExchangeAction(async () =>
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
        ExchangeHeader("Battle players · " + b.Code, () => Render());
        AddSharedArmy(packet.Host); AddSharedArmy(packet.Guest!);

    }
}

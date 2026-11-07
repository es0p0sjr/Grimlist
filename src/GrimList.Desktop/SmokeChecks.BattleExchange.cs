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
    private static void CheckBattleExchange(MainWindow owner, string directory, string imagePath)
    {
        Dataset Data(string faction, string disposition) => new(new("Exchange UI", "1", "2026-10-15", "", Edition: "11"), [],
            Detachments: [new(faction, "Orders", Dispositions: [disposition])]);
        ArmyList Army(string faction, string disposition) => ArmyList.New(faction) with { Name = faction + " army", Disposition = disposition, Detachments = ["Orders"],
            Entries = [new("squad", "squad", "Battle squad", 5, "Plasma rifle")] };
        var hostArmy = Army("Host", "Take and Hold"); var guestArmy = Army("Guest", "Purge the Foe");
        var hostPath = Path.Combine(directory, "exchange-ui-host", hostArmy.Id); var guestPath = Path.Combine(directory, "exchange-ui-guest", guestArmy.Id);
        var host = new BattleWindow(hostArmy, Data("Host", "Take and Hold"), hostPath) { Owner = owner, ShowInTaskbar = false };
        var guest = new BattleWindow(guestArmy, Data("Guest", "Purge the Foe"), guestPath) { Owner = owner, ShowInTaskbar = false };
        Button Action(BattleWindow w, string id) { w.UpdateLayout(); return Descendants<Button>(w).Single(b => AutomationProperties.GetAutomationId(b) == id); }
        void Press(BattleWindow w, string id) => Action(w, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void WaitFor(BattleWindow w, string id, bool requireEnabled = true)
        {
            async Task Wait()
            {
                for (var i = 0; i < 200; i++)
                {
                    w.UpdateLayout();
                    if (Descendants<Button>(w).Any(b => AutomationProperties.GetAutomationId(b) == id && (!requireEnabled || b.IsEnabled))) return;
                    await Task.Delay(20);
                }
                throw new Exception("Exchange UI did not show " + id);
            }
            PumpUntil(Wait());
        }
        BattleRecord Record(BattleWindow w) => (BattleRecord)typeof(BattleWindow).GetField("battle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
        void StartGuestFirst(BattleWindow w)
        {
            Press(w, "exchange-open"); WaitFor(w, "exchange-start", false);
            var first = Descendants<ComboBox>(w).Single(c => AutomationProperties.GetAutomationId(c) == "exchange-first");
            Check(first.SelectedIndex == -1 && !Action(w, "exchange-start").IsEnabled, "First turn was preselected");
            first.SelectedIndex = 1; WaitFor(w, "exchange-start");
            Capture(w, imagePath + (w == host ? ".host-start.png" : ".guest-start.png"));
            Press(w, "exchange-start"); WaitFor(w, "battle-armies");
        }
        void Review(BattleWindow w, BattleExchangePacket packet) => PumpUntil((Task)typeof(BattleWindow).GetMethod("ReviewExchange", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [packet])!);
        BattleExchangeDraft Load(string path, string armyId)
        {
            var task = new BattleExchangeStore(path).LoadAsync(armyId); PumpUntil(task); return task.Result.Single();
        }
        void Close(BattleWindow w)
        {
            var closed = new TaskCompletionSource(); w.Closed += (_, _) => closed.TrySetResult(); w.Close(); PumpUntil(closed.Task);
        }
        try
        {
            host.Show(); WaitFor(host, "battle-connect"); Press(host, "battle-connect"); WaitFor(host, "exchange-create");
            Check(!Descendants<TextBlock>(host).Any(t => t.Text.Contains("first turn")), "Code generation asks for first turn");
            Descendants<TextBox>(host).Single(c => AutomationProperties.GetAutomationId(c) == "exchange-player-name").Text = "Host player";
            Press(host, "exchange-create"); WaitFor(host, "exchange-response");
            Check(Descendants<Image>(host).Any(i => AutomationProperties.GetAutomationId(i) == "exchange-qr"), "Invitation QR missing");
            var invitation = Load(hostPath, hostArmy.Id); Capture(host, imagePath + ".invitation.png");
            Check(invitation.Packet.HostFirst == null && Record(host) == null, "Invitation started or ordered the battle");
            guest.Show(); WaitFor(guest, "battle-connect");
            Review(guest, BattleExchange.Decode(BattleExchange.Encode(invitation.Packet))); WaitFor(guest, "exchange-join");
            Check(Descendants<TextBlock>(guest).Any(t => t.Text.Contains("Opponent primary: Immovable Object")), "Join preview missing primaries");
            Descendants<TextBox>(guest).Single(c => AutomationProperties.GetAutomationId(c) == "exchange-player-name").Text = "Guest player";
            Press(guest, "exchange-join"); WaitFor(guest, "exchange-open");
            var response = Load(guestPath, guestArmy.Id); Capture(guest, imagePath + ".response.png");
            Check(Record(guest) == null, "Joining started the battle");
            StartGuestFirst(guest);
            Review(host, BattleExchange.Decode(BattleExchange.Encode(response.Packet))); Press(host, "exchange-accept"); WaitFor(host, "exchange-open");
            Check(Record(host) == null, "Accepting started the battle");
            StartGuestFirst(host);
            Check(Record(host).Code == Record(guest).Code && Record(host).Players.Select(p => p.Primary).SequenceEqual(Record(guest).Players.Select(p => p.Primary)),
                "Windows exchange did not establish matching battles");
            Check(Record(host).Players[0].Name == "Guest player" && Record(guest).Players[0].Name == "Guest player", "Inviting player was forced to go first");
            Review(host, response.Packet); Press(host, "exchange-accept"); WaitFor(host, "exchange-open");
            Press(host, "exchange-open"); WaitFor(host, "battle-armies");
            Check(Record(host).Players[0].Name == "Guest player", "Reopening changed first turn");
            Press(host, "battle-armies"); host.UpdateLayout();
            foreach (var expander in Descendants<Expander>(host).Where(e => e.Header as string == "View army list")) expander.IsExpanded = true;
            host.UpdateLayout();
            Check(!Descendants<TextBlock>(host).Any(t => t.Text.Contains("Plasma rifle") || t.Text.Contains("Detachment:")), "Private roster details shown");
            Check(Descendants<TextBlock>(host).Any(t => t.Text == "Host player") && Descendants<TextBlock>(host).Any(t => t.Text == "Guest player"), "Player names missing");
            Capture(host, imagePath + ".shared-armies.png");
            Close(host); Close(guest);
        }
        finally { host.Close(); guest.Close(); }
    }
}

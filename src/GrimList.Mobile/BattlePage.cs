using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class BattlePage : ContentPage
{
    private readonly ArmyList army;
    private readonly DispositionPrimaryView matchups;
    private readonly List<ReferenceCard> rules;
    private readonly BattleStore store;
    private readonly VerticalStackLayout header = new() { Spacing = 6 }, body = new() { Spacing = 10 };
    private readonly Grid footer = new() { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
    private readonly Label status = Ui.Text("Loading battle history…", 12, colour: Ui.Muted);
    private readonly ScrollView scroll;
    private readonly List<Action> refresh = [];
    private BattleRecord? battle;
    private readonly HashSet<(string Battle, int Player)> collapsedSecondaries = [];
    private bool opened, closing;
    private Task pendingSave = Task.CompletedTask;

    public BattlePage(ArmyList army, Dataset? data, string directory)
    {
        this.army = army; matchups = DispositionPrimaries.ForArmy(army, data);
        battleData = data; exchanges = new(directory);
        rules = ArmyReferences.ForArmy(army, data).ToList(); store = new(directory);
        Title = "Battle tracker"; BackgroundColor = Ui.Background;
        NavigationPage.SetHasNavigationBar(this, false);
        scroll = Ui.Scroll(body);
        var grid = new Grid { Padding = 12, RowSpacing = 8, RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
        grid.Add(header); grid.Add(status, 0, 1); grid.Add(scroll, 0, 2); grid.Add(footer, 0, 3); Content = grid;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); if (opened) { await ReceiveQrAsync(); return; } opened = true; await Lobby(); await ReceiveQrAsync();
    }
    protected override bool OnBackButtonPressed() { _ = Close(); return true; }
    private async Task Close()
    {
        if (closing) return;
        closing = true; await pendingSave;
        if (await Save()) await Navigation.PopModalAsync();
        else closing = false;
    }
    private void Reset() { exchangeView = false; header.Clear(); body.Clear(); footer.Clear(); refresh.Clear(); }
    private static BoxView Line(bool large = false) => new() { Color = Ui.Line, HeightRequest = large ? 6 : 1, Margin = new Thickness(0, large ? 18 : 8) };
    private async Task Lobby(bool startIfEmpty = true)
    {
        Reset(); header.Add(Ui.Text("Battle tracker", 25, true)); header.Add(Ui.Button("Back to army", async () => await Close()));
        try
        {
            await Task.Run(() => _ = BattleMissions.All);
            var records = await store.LoadAsync(army.Id); status.Text = "Chapter Approved 2026–27 · Saved on this device";
            body.Add(Ui.Button("Play with another GrimList", async () => await ExchangeAction(ExchangeHome), "battle-connect"));
            if (matchups.Matchups.Count > 0) body.Add(Ui.Button("New battle", Setup, "battle-new")); else body.Add(Ui.Text(matchups.Hint));
            foreach (var record in records)
            {
                var panel = new VerticalStackLayout { Spacing = 8 };
                panel.Add(Ui.Text(string.Join(" vs ", record.Players.Select(p => p.Name)), 19, true));
                panel.Add(Ui.Text($"{record.Code} · {(record.Completed ? "Completed" : $"Round {record.Round}")} · {record.UpdatedAt.ToLocalTime():g}", 13));
                panel.Add(Ui.Button(record.Completed ? "View scoreboard" : "Resume battle", () => { battle = record; if (record.Completed) record.Scoreboard = true; Render(); }, "battle-resume"));
                if (!record.Completed) panel.Add(Ui.Button("Mark as finished", async () => await FinishFromHistory(record), "battle-log-finish-" + record.Id, Ui.Surface));
                panel.Add(Ui.Button("Delete battle", async () => await DeleteBattle(record), "battle-log-delete-" + record.Id, Ui.Danger)); body.Add(Ui.Card(panel, Ui.Surface));
            }
            if (records.Count == 0 && matchups.Matchups.Count > 0 && startIfEmpty) Setup();
            else if (records.Count == 0) body.Add(Ui.Text("No battles recorded for this army."));
        }
        catch (Exception ex) { status.Text = "Could not load battles: " + ex.Message; body.Add(Ui.Button("Retry", async () => await Lobby())); }
    }
    private void Setup()
    {
        Reset(); header.Add(Ui.Text("Start a battle", 25, true)); header.Add(Ui.Button("Back to army", async () => await Close()));
        body.Add(Ui.Button("Play with another GrimList", async () => await ExchangeAction(ExchangeHome), "battle-connect"));
        body.Add(Ui.Text(army.Name + " · " + army.Disposition, 18, true));
        body.Add(Ui.Text("Opponent name", 14)); var name = Ui.Entry("Opponent name", "Opponent", "battle-opponent-name"); body.Add(name);
        body.Add(Ui.Text("Opponent disposition", 14));
        var disposition = Ui.Picker("Opponent disposition", "battle-opponent-disposition");
        foreach (var m in matchups.Matchups) disposition.Items.Add(m.OpponentDisposition); disposition.SelectedIndex = 0; body.Add(disposition);
        var assignments = Ui.Text("", 16, true); body.Add(assignments);
        void Assign() { var m = matchups.Matchups[disposition.SelectedIndex]; assignments.Text = $"{army.Name}: {m.YourPrimary}\nOpponent: {m.OpponentPrimary}"; }
        disposition.SelectedIndexChanged += (_, _) => Assign(); Assign();
        body.Add(Ui.Text("Who takes the first turn?", 14)); var first = Ui.Picker("First turn", "battle-first");
        first.Items.Add(army.Name); first.Items.Add("Opponent"); first.SelectedIndex = 0; body.Add(first);
        (Picker Mode, Picker One, Picker Two) SecondaryChoice(string title)
        {
            var panel = new VerticalStackLayout { Spacing = 8 }; panel.Add(Ui.Text(title, 18, true));
            var mode = Ui.Picker("Secondary mode"); mode.Items.Add("Tactical"); mode.Items.Add("Fixed"); mode.SelectedIndex = 0; panel.Add(mode);
            var fixedPanel = new VerticalStackLayout { Spacing = 8, IsVisible = false };
            Picker Pick(int index) { var pick = Ui.Picker("Fixed mission"); pick.ItemsSource = BattleMissions.Secondaries("Fixed").ToList(); pick.ItemDisplayBinding = new Binding("Name"); pick.SelectedIndex = index; return pick; }
            var one = Pick(0); var two = Pick(1); fixedPanel.Add(one); fixedPanel.Add(two); panel.Add(fixedPanel);
            mode.SelectedIndexChanged += (_, _) => fixedPanel.IsVisible = mode.SelectedIndex == 1;
            body.Add(Ui.Card(panel, Ui.Surface)); return (mode, one, two);
        }
        var yours = SecondaryChoice(army.Name + " · Secondaries"); var theirs = SecondaryChoice("Opponent · Secondaries");
        body.Add(Ui.Text("Both players start with +10 Battle Ready VP. You can correct this on the scoring screen. Primaries are assigned automatically and locked for this battle.", 14));
        footer.Add(Ui.Button("Start battle", () =>
        {
            try
            {
                battle = BattleRecord.Create(army, rules, matchups.Matchups[disposition.SelectedIndex], name.Text, first.SelectedIndex == 0,
                    (string)yours.Mode.SelectedItem, (string)theirs.Mode.SelectedItem,
                    new[] { ((BattleMission)yours.One.SelectedItem).Id, ((BattleMission)yours.Two.SelectedItem).Id },
                    new[] { ((BattleMission)theirs.One.SelectedItem).Id, ((BattleMission)theirs.Two.SelectedItem).Id });
                Render(); QueueSave();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }, "battle-start"), 1);
    }
    private void QueueSave() => pendingSave = Save();
    private async Task<bool> Save()
    {
        if (battle == null) return true;
        try { status.Text = "Saving…"; await store.SaveAsync(battle); status.Text = "Saved on this device · " + battle.Code; return true; }
        catch (Exception ex) { status.Text = "Not saved — keep this page open. Retry save: " + ex.Message; return false; }
    }
    private void Change(Action action, bool rebuild = false, bool retainScroll = true)
    {
        try { action(); if (rebuild) Render(retainScroll); else foreach (var update in refresh) update(); QueueSave(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void Render(bool retainScroll = false)
    {
        var offset = retainScroll ? scroll.ScrollY : 0; Reset(); var b = battle!;
        var toolbar = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        toolbar.Add(Ui.Button("Save & close", async () => await Close(), "battle-close", Ui.Surface));
        toolbar.Add(Ui.Button("Retry save", QueueSave, "battle-save", Ui.Surface), 1); header.Add(toolbar);
        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        var finish = Ui.Button("Finish battle", () => Change(() => { if (b.Completed) b.Reopen(); else b.Finish(); }, true, false), "battle-finish");
        refresh.Add(() => finish.Text = b.Completed ? "Reopen battle" : "Finish battle"); actions.Add(finish);
        actions.Add(Ui.Button("Battle history", async () => await ShowHistory(), "battle-history", Ui.Surface), 1); header.Add(actions);
        var roundHeading = Ui.Text(b.Scoreboard ? b.Completed ? $"Finished · round {b.FinishedRound ?? 5}" : "Scoreboard" : $"Round {b.Round} of 5", 22, true);
        roundHeading.AutomationId = "battle-round-heading";
        // Android's swipe recognizer consumes touches when attached to a ScrollView.
        // Keep round gestures on the fixed heading, outside the scrolling subtree.
        foreach (var direction in new[] { SwipeDirection.Left, SwipeDirection.Right })
        {
            var swipe = new SwipeGestureRecognizer { Direction = direction, Threshold = 100 };
            swipe.Swiped += (_, _) => { if (battle != null && !exchangeView) Change(() => battle.Navigate(direction == SwipeDirection.Left ? 1 : -1), true, false); };
            roundHeading.GestureRecognizers.Add(swipe);
        }
        SemanticProperties.SetHint(roundHeading, "Swipe left or right to change rounds. You can also use the round buttons.");
        header.Add(roundHeading);
        var totals = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 12 };
        for (var player = 0; player < 2; player++)
        {
            var p = player; var total = Ui.Text("", 20, true); totals.Add(total, p);
            refresh.Add(() => total.Text = $"{b.Players[p].Name}\n{b.Total(p)} VP");
        }
        header.Add(totals);
        if (b.Exchange != null) body.Add(Ui.Button("View players", SharedArmies, "battle-armies", Ui.Surface));
        if (b.Scoreboard) Scoreboard();
        else for (var player = 0; player < 2; player++) { if (player > 0) body.Add(Line(true)); Player(player); }
        var previous = Ui.Button("Previous round", () => Change(() => b.Navigate(-1), true, false), "battle-previous", Ui.Surface);
        previous.IsVisible = b.Round > 1 || b.Scoreboard; footer.Add(previous);
        footer.Add(Ui.Button(b.Scoreboard ? (b.Completed ? "Save & close" : "Finish battle") : b.Round == 5 ? "Scoreboard" : "Next round", async () =>
        {
            if (b.Scoreboard && b.Completed) await Close();
            else Change(() => { if (b.Scoreboard) b.Finish(); else b.Navigate(1); }, true, false);
        }, "battle-next"), 1);
        foreach (var update in refresh) update();
        Dispatcher.Dispatch(async () => await scroll.ScrollToAsync(0, offset, false));
    }
    private void Player(int player)
    {
        var b = battle!; var p = b.Players[player];
        body.Add(Ui.Text($"{(player == 0 ? "GOING FIRST" : "GOING SECOND")} · {p.Name}", 22, true)); body.Add(Ui.Text(p.Disposition, 13, colour: Ui.Muted));
        var ready = new CheckBox { IsChecked = p.BattleReady, Color = Colors.White };
        ready.CheckedChanged += (_, _) => Change(() => { p.BattleReady = ready.IsChecked; b.Completed = false; });
        var readyRow = new HorizontalStackLayout(); readyRow.Add(ready); readyRow.Add(Ui.Text("Battle Ready · +10 VP", 14)); body.Add(readyRow);
        var cp = new VerticalStackLayout { Spacing = 8 }; var balance = Ui.Text("", 20, true); cp.Add(balance);
        refresh.Add(() => balance.Text = $"{b.Cp(player, b.Round)} CP available · through round {b.Round}");
        var legacyDiscardCp = b.HasRecordedDiscardCp(player, b.Round);
        Counter(cp, "Core CP gained", () => p.Rounds[b.Round - 1].CoreCp, d => b.ChangeCp(player, "Core", d), compactRow: true);
        Counter(cp, legacyDiscardCp ? "Other extra CP (max 1)" : "Extra CP gained (max 1)", () => p.Rounds[b.Round - 1].ExtraCp, d => b.ChangeCp(player, "Extra", d), compactRow: true);
        Counter(cp, legacyDiscardCp ? "CP spent (excluding saved redraw)" : "CP spent", () => p.Rounds[b.Round - 1].SpentCp, d => b.ChangeCp(player, "Spent", d), compactRow: true);
        if (legacyDiscardCp)
        {
            var discardCp = Ui.Text("", 13); cp.Add(discardCp); refresh.Add(() => discardCp.Text = b.DiscardCpSummary(player, b.Round));
        }
        body.Add(Ui.Card(cp, Ui.Surface));
        var primary = Ui.Text("", 17, true); refresh.Add(() => primary.Text = b.Summary(player, true)); body.Add(primary);
        Mission(body, player, BattleMissions.Primary(p.Primary)); body.Add(Line());
        var secondaries = SecondarySection(player);
        secondaries.Add(Ui.Text(p.Mode + " missions", 14));
        if (p.Mode == "Fixed") secondaries.Add(Ui.Text("Fixed missions remain active for the whole battle and cannot be discarded.", 13));
        if (p.Mode == "Tactical") ShowDiscardPool(secondaries, player);
        foreach (var selected in b.VisibleSecondaries(player).Where(c => !c.DiscardedRound.HasValue).ToList())
        {
            Mission(secondaries, player, BattleMissions.Secondary(selected.CardId, p.Mode));
            if (p.Mode == "Tactical") DiscardOptions(secondaries, player, selected);
        }
        if (p.Mode == "Tactical")
        {
            var random = Ui.Button("Draw random secondary", () => Change(() => b.DrawRandomSecondary(player), true), "battle-random-" + player);
            random.IsEnabled = b.DrawSlotsRemaining(player) > 0 && b.RandomDrawPool(player).Count > 0; secondaries.Add(random);
            secondaries.Add(Ui.Text($"{b.DrawSlotsRemaining(player)} draw slot(s) available · {b.RandomDrawPool(player).Count} eligible cards remaining", 13));
            secondaries.Add(Ui.Text("Record a card drawn at the start of your Command phase", 14));
            var picker = Ui.Picker("Choose drawn card", "battle-secondary-picker-" + player);
            picker.ItemsSource = BattleMissions.Secondaries("Tactical").Where(c => p.Secondaries.All(s => s.CardId != c.Id)).ToList(); picker.ItemDisplayBinding = new Binding("Name"); secondaries.Add(picker);
            var add = Ui.Button("Add drawn card", () => { if (picker.SelectedItem is BattleMission selected) Change(() => b.AddSecondary(player, selected.Id), true); }, "battle-add-secondary-" + player);
            add.IsEnabled = b.DrawSlotsRemaining(player) > 0; secondaries.Add(add);
        }
    }
    private void Counter(VerticalStackLayout panel, string label, Func<int> value, Action<int> change, string id = "", bool count = true, bool compactRow = false, Func<bool>? canIncrease = null)
    {
        // Controls sit beneath long conditions so neither text nor hit targets shrink on phones.
        var caption = Ui.Text(label, 15);
        var controls = new Grid { ColumnDefinitions = { new(new GridLength(52)), new(new GridLength(64)), new(new GridLength(52)) }, ColumnSpacing = 8 };
        var minus = Ui.Button("−", () => Change(() => change(-1)), id + "-minus", Ui.Surface, true);
        var number = Ui.Text("", 19, true); number.HorizontalTextAlignment = TextAlignment.Center;
        var plus = Ui.Button(count ? "+" : "✓", () => Change(() => change(1)), id + "-plus", compact: true);
        SemanticProperties.SetDescription(minus, "Decrease " + label); SemanticProperties.SetDescription(plus, (count ? "Increase " : "Mark achieved: ") + label);
        controls.Add(minus); controls.Add(number, 1); controls.Add(plus, 2);
        if (compactRow)
        {
            var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
            row.Add(caption); row.Add(controls, 1); panel.Add(row);
        }
        else { panel.Add(caption); panel.Add(controls); }
        refresh.Add(() => { number.Text = count ? value().ToString() : value() > 0 ? "✓" : "—"; minus.IsEnabled = value() > 0; plus.IsEnabled = (count || value() == 0) && (canIncrease?.Invoke() ?? true); });
    }
    private void Mission(VerticalStackLayout target, int player, BattleMission mission, bool end = false)
    {
        var b = battle!; var panel = new VerticalStackLayout { Spacing = 10 }; panel.Add(Ui.Text(mission.Name, 20, true));
        if (mission.Instructions.Length > 0) panel.Add(Ui.Fold("Card instructions & actions", () => Ui.Text(mission.Instructions, 14), Ui.Surface));
        var groups = mission.Groups.Where(g => end ? g.EndBattle : g.Applies(b.Round)).ToList();
        if (groups.Count == 0) panel.Add(Ui.Text(end ? "No end-of-battle scoring." : "No scoring in this round.", 14));
        foreach (var group in groups)
        foreach (var turn in group.Turns(player, b.Round))
        {
            panel.Add(Ui.Text(group.Label(player, turn, b.Round), 14, true, Ui.Muted));
            foreach (var condition in group.Conditions)
            {
                var label = (condition.Relation == "alternative" ? "OR · " : condition.Relation == "cumulative" ? "BONUS · " : "") + condition.Text
                    + $"\n{condition.Vp} VP{(condition.Count ? " each" : "")}" + (condition.Cap.HasValue ? $" · up to {condition.Cap} VP" : "");
                Counter(panel, label, () => b.Count(player, condition, turn, b.Round, end),
                    d => b.ChangeCondition(player, mission, group, condition, turn, d), $"score-{player}-{condition.Id}-{turn}", condition.Count,
                    canIncrease: () => mission.Mode != "Tactical" || !b.Players[player].Secondaries.Single(s => s.CardId == mission.Id).DiscardedRound.HasValue); panel.Add(Line());
            }
        }
        target.Add(Ui.Card(panel));
    }
    private void Scoreboard()
    {
        var b = battle!;
        for (var round = 1; round <= 5; round++)
        {
            var r = round; var panel = new VerticalStackLayout { Spacing = 10 };
            panel.Add(Ui.Text($"ROUND {r}", 22, true));
            if (b.FinishedRound.HasValue && r > b.FinishedRound) panel.Add(Ui.Text("Not played", 15));
            else
            {
                foreach (var primary in new[] { true, false })
                {
                    panel.Add(Ui.Text(primary ? "Primary" : "Secondary", 19, true));
                    for (var player = 0; player < 2; player++)
                    {
                        var p = player; var summary = Ui.Text("", 15); panel.Add(summary);
                        refresh.Add(() => summary.Text = b.ScoreboardCards(p, r, primary));
                    }
                    panel.Add(Line());
                }
                for (var player = 0; player < 2; player++)
                {
                    var p = player; var cp = Ui.Text("", 13); panel.Add(cp);
                    refresh.Add(() => cp.Text = $"{b.Players[p].Name} \u00b7 CP spent: {b.CpSpent(p, r)} \u00b7 Balance: {b.Cp(p, r)}");
                }
            }
            body.Add(Ui.Card(panel, Ui.Surface));
        }
        body.Add(Ui.Text("Battle totals", 23, true));
        for (var player = 0; player < 2; player++)
        {
            var p = player; var record = b.Players[p]; var total = Ui.Text("", 16, true); body.Add(total);
            refresh.Add(() => total.Text = $"{record.Name} \u00b7 {b.Total(p)} VP\nBattle Ready: {(record.BattleReady ? 10 : 0)} VP \u00b7 End-of-battle primary: {b.Scores(p).Where(l => l.Round == 6).Sum(l => l.Vp)} VP");
            var mission = BattleMissions.Primary(record.Primary);
            if (mission.Groups.Any(g => g.EndBattle))
            {
                // Build now so score counters join the normal refresh cycle.
                var scoring = new VerticalStackLayout { Spacing = 10 }; Mission(scoring, p, mission, true);
                body.Add(Ui.Fold(record.Name + " \u00b7 End-of-battle scoring", () => scoring, Ui.Surface));
            }
        }
    }
}

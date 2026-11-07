using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

public sealed partial class BattleWindow : Window
{
    private readonly ArmyList army;
    private readonly DispositionPrimaryView matchups;
    private readonly List<ReferenceCard> rules;
    private readonly BattleStore store;
    private readonly Grid root = new();
    private readonly StackPanel header = new(), body = new();
    private readonly Grid footer = new();
    private readonly ScrollViewer scroll;
    private readonly TextBlock status = Text("Loading battle history…", 12);
    private readonly List<Action> refresh = [];
    private BattleRecord? battle;
    private readonly HashSet<(string Battle, int Player)> collapsedSecondaries = [];
    private bool closing;
    private Task pendingSave = Task.CompletedTask;
    private readonly Func<BattleRecord, Task<bool>> confirmDeletion;

    public BattleWindow(ArmyList army, Dataset? data, string directory, Func<BattleRecord, Task<bool>>? confirmDeletion = null)
    {
        this.army = army; matchups = DispositionPrimaries.ForArmy(army, data);
        battleData = data; exchanges = new(directory);
        rules = ArmyReferences.ForArmy(army, data).ToList(); store = new(directory);
        this.confirmDeletion = confirmDeletion ?? (record => Task.FromResult(MessageBox.Show(this,
            $"Delete battle {record.Code} ({string.Join(" vs ", record.Players.Select(p => p.Name))})? Its saved scores and history will be removed. Your army is kept.",
            "Delete battle?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes));
        Title = "GrimList · Battle tracker"; Width = 850; Height = 920; MinWidth = 530; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window)); DarkWindow.Apply(this);
        root.Margin = new Thickness(18);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(header); Grid.SetRow(status, 1); root.Children.Add(status);
        Grid.SetRow(scroll, 2); root.Children.Add(scroll); Grid.SetRow(footer, 3); root.Children.Add(footer);
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;
        Loaded += async (_, _) => await Lobby();
        Closing += async (_, e) =>
        {
            if (closing || battle == null) return;
            e.Cancel = true;
            await pendingSave;
            if (await Save()) { closing = true; Close(); }
        };
    }
    private static TextBlock Text(string value, double size = 15, bool bold = false) => new()
    { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private static Button Button(string label, Action action, string id = "")
    {
        var button = new Button { Content = label, MinHeight = 42, MinWidth = 44, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 6, 12, 6) };
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    private static Border Card(UIElement content) => new() { Child = content, Background = new SolidColorBrush(Color.FromRgb(32, 40, 36)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(75, 83, 80)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 8, 5, 8) };
    private static Border Line(bool large = false) => new() { Height = large ? 6 : 1, Background = new SolidColorBrush(Color.FromRgb(98, 115, 108)), Margin = new Thickness(0, large ? 22 : 12, 0, large ? 22 : 12) };
    private void Reset() { header.Children.Clear(); body.Children.Clear(); footer.Children.Clear(); refresh.Clear(); }
    private async Task Lobby(bool startIfEmpty = true)
    {
        Reset(); header.Children.Add(Text("Battle tracker", 26, true));
        try
        {
            await Task.Run(() => _ = BattleMissions.All);
            var records = await store.LoadAsync(army.Id);
            status.Text = "Chapter Approved 2026–27 · Saved on this device";
            body.Children.Add(Button("Play with another GrimList", async () => await ExchangeAction(ExchangeHome), "battle-connect"));
            if (matchups.Matchups.Count > 0) body.Children.Add(Button("New battle", Setup, "battle-new"));
            else body.Children.Add(Text(matchups.Hint));
            foreach (var record in records)
            {
                var panel = new StackPanel();
                panel.Children.Add(Text(string.Join(" vs ", record.Players.Select(p => p.Name)), 18, true));
                panel.Children.Add(Text($"{record.Code} · {(record.Completed ? "Completed" : $"Round {record.Round}")} · {record.UpdatedAt.ToLocalTime():g}"));
                panel.Children.Add(Button(record.Completed ? "View scoreboard" : "Resume battle", () => { battle = record; if (record.Completed) record.Scoreboard = true; Render(); }, "battle-resume"));
                if (!record.Completed) panel.Children.Add(Button("Mark as finished", async () => await FinishFromHistory(record), "battle-log-finish-" + record.Id));
                panel.Children.Add(Button("Delete battle", async () => await DeleteBattle(record), "battle-log-delete-" + record.Id));
                body.Children.Add(Card(panel));
            }
            if (records.Count == 0 && matchups.Matchups.Count > 0 && startIfEmpty) Setup();
            else if (records.Count == 0) body.Children.Add(Text("No battles recorded for this army."));
        }
        catch (Exception ex) { status.Text = "Could not load battles: " + ex.Message; body.Children.Add(Button("Retry", async () => await Lobby())); }
    }
    private void Setup()
    {
        Reset(); header.Children.Add(Text("Start a battle", 26, true));
        body.Children.Add(Button("Play with another GrimList", async () => await ExchangeAction(ExchangeHome), "battle-connect"));
        body.Children.Add(Text(army.Name + " · " + army.Disposition, 18, true));
        body.Children.Add(Text("Opponent name")); var name = new TextBox { Text = "Opponent", MinHeight = 36 }; body.Children.Add(name);
        body.Children.Add(Text("Opponent disposition"));
        var disposition = new ComboBox { ItemsSource = matchups.Matchups.Select(m => m.OpponentDisposition), SelectedIndex = 0, MinHeight = 38 };
        AutomationProperties.SetAutomationId(disposition, "battle-opponent-disposition"); body.Children.Add(disposition);
        var assignments = Text("", 16, true); body.Children.Add(assignments);
        void Assign() { var m = matchups.Matchups[disposition.SelectedIndex]; assignments.Text = $"{army.Name}: {m.YourPrimary}\nOpponent: {m.OpponentPrimary}"; }
        disposition.SelectionChanged += (_, _) => Assign(); Assign();
        body.Children.Add(Text("Who takes the first turn?"));
        var first = new ComboBox { ItemsSource = new[] { army.Name, "Opponent" }, SelectedIndex = 0, MinHeight = 38 }; body.Children.Add(first);
        (ComboBox Mode, ComboBox One, ComboBox Two) SecondaryChoice(string title)
        {
            var panel = new StackPanel(); panel.Children.Add(Text(title, 18, true));
            var mode = new ComboBox { ItemsSource = new[] { "Tactical", "Fixed" }, SelectedIndex = 0, MinHeight = 38 }; panel.Children.Add(mode);
            var fixedPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var choices = BattleMissions.Secondaries("Fixed").ToList();
            ComboBox Pick(int index) => new() { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = index, MinHeight = 38, Margin = new Thickness(0, 6, 0, 0) };
            var one = Pick(0); var two = Pick(1); fixedPanel.Children.Add(one); fixedPanel.Children.Add(two); panel.Children.Add(fixedPanel);
            mode.SelectionChanged += (_, _) => fixedPanel.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(Card(panel)); return (mode, one, two);
        }
        var yours = SecondaryChoice(army.Name + " · Secondaries"); var theirs = SecondaryChoice("Opponent · Secondaries");
        body.Children.Add(Text("Both players start with +10 Battle Ready VP. You can correct this on the scoring screen. Primaries are assigned automatically and locked for this battle."));
        var start = Button("Start battle", () =>
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
        }, "battle-start"); Grid.SetColumn(start, 1); footer.Children.Add(start);
    }
    private void QueueSave() => pendingSave = Save();
    private async Task<bool> Save()
    {
        if (battle == null) return true;
        try { status.Text = "Saving…"; await store.SaveAsync(battle); status.Text = "Saved on this device · " + battle.Code; return true; }
        catch (Exception ex) { status.Text = "Not saved — keep this window open. Retry save: " + ex.Message; return false; }
    }
    private void Change(Action action, bool rebuild = false, bool retainScroll = true)
    {
        try { action(); if (rebuild) Render(retainScroll); else foreach (var update in refresh) update(); QueueSave(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void Render(bool retainScroll = false)
    {
        var offset = retainScroll ? scroll.VerticalOffset : 0;
        Reset(); var b = battle!;
        var toolbar = new WrapPanel();
        toolbar.Children.Add(Button("Save & close", Close)); toolbar.Children.Add(Button("Retry save", QueueSave));
        toolbar.Children.Add(Button("Battle history", async () => await ShowHistory(), "battle-history"));
        var finish = Button("Finish battle", () => Change(() => { if (b.Completed) b.Reopen(); else b.Finish(); }, true, false), "battle-finish");
        refresh.Add(() => finish.Content = b.Completed ? "Reopen battle" : "Finish battle"); toolbar.Children.Add(finish);
        toolbar.Children.Add(Text("Battle " + b.Code, 14, true)); header.Children.Add(toolbar);
        header.Children.Add(Text(b.Scoreboard ? b.Completed ? $"Finished · round {b.FinishedRound ?? 5}" : "Scoreboard" : $"Round {b.Round} of 5", 24, true));
        var totals = new Grid(); totals.ColumnDefinitions.Add(new()); totals.ColumnDefinitions.Add(new());
        for (var player = 0; player < 2; player++)
        {
            var p = player; var total = Text("", 21, true); Grid.SetColumn(total, p); totals.Children.Add(total);
            refresh.Add(() => total.Text = $"{b.Players[p].Name}\n{b.Total(p)} VP");
        }
        header.Children.Add(totals);
        if (b.Exchange != null) body.Children.Add(Button("View players", SharedArmies, "battle-armies"));
        if (b.Scoreboard) Scoreboard();
        else for (var player = 0; player < 2; player++)
        {
            if (player > 0) body.Children.Add(Line(true));
            Player(player);
        }
        var previous = Button(b.Scoreboard ? "Previous round" : "← Previous round", () => Change(() => b.Navigate(-1), true, false), "battle-previous");
        previous.Visibility = b.Round > 1 || b.Scoreboard ? Visibility.Visible : Visibility.Collapsed; footer.Children.Add(previous);
        var next = Button(b.Scoreboard ? (b.Completed ? "Completed · Save & close" : "Finish battle") : b.Round == 5 ? "Scoreboard →" : "Next round →", () =>
        {
            if (b.Scoreboard && b.Completed) Close();
            else Change(() => { if (b.Scoreboard) b.Finish(); else b.Navigate(1); }, true, false);
        }, "battle-next"); Grid.SetColumn(next, 1); footer.Children.Add(next);
        foreach (var update in refresh) update();
        scroll.ScrollToVerticalOffset(offset);
    }
    private void Player(int player)
    {
        var b = battle!; var p = b.Players[player];
        body.Children.Add(Text($"{(player == 0 ? "GOING FIRST" : "GOING SECOND")} · {p.Name}", 23, true));
        body.Children.Add(Text(p.Disposition, 13));
        var ready = new CheckBox { Content = "Battle Ready · +10 VP", IsChecked = p.BattleReady, Margin = new Thickness(0, 4, 0, 10) };
        ready.Click += (_, _) => Change(() => { p.BattleReady = ready.IsChecked == true; b.Completed = false; }); body.Children.Add(ready);
        var cp = new StackPanel(); var balance = Text("", 20, true); cp.Children.Add(balance);
        refresh.Add(() => balance.Text = $"{b.Cp(player, b.Round)} CP available · through round {b.Round}");
        var legacyDiscardCp = b.HasRecordedDiscardCp(player, b.Round);
        Counter(cp, "Core CP gained", () => p.Rounds[b.Round - 1].CoreCp, d => b.ChangeCp(player, "Core", d));
        Counter(cp, legacyDiscardCp ? "Other extra CP gained (max 1/round)" : "Extra CP gained (max 1/round)", () => p.Rounds[b.Round - 1].ExtraCp, d => b.ChangeCp(player, "Extra", d));
        Counter(cp, legacyDiscardCp ? "CP spent (excluding saved redraw)" : "CP spent", () => p.Rounds[b.Round - 1].SpentCp, d => b.ChangeCp(player, "Spent", d));
        if (legacyDiscardCp)
        {
            var discardCp = Text("", 13); cp.Children.Add(discardCp); refresh.Add(() => discardCp.Text = b.DiscardCpSummary(player, b.Round));
        }
        body.Children.Add(Card(cp));
        var primary = Text("", 17, true); refresh.Add(() => primary.Text = b.Summary(player, true)); body.Children.Add(primary);
        Mission(body, player, BattleMissions.Primary(p.Primary));
        body.Children.Add(Line());
        var secondaries = SecondarySection(player);
        secondaries.Children.Add(Text(p.Mode + " missions", 14));
        if (p.Mode == "Fixed") secondaries.Children.Add(Text("Fixed missions remain active for the whole battle and cannot be discarded.", 13));
        if (p.Mode == "Tactical") ShowDiscardPool(secondaries, player);
        foreach (var selected in b.VisibleSecondaries(player).Where(c => !c.DiscardedRound.HasValue).ToList())
        {
            var card = BattleMissions.Secondary(selected.CardId, p.Mode); Mission(secondaries, player, card);
            if (p.Mode == "Tactical") DiscardOptions(secondaries, player, selected);
        }
        if (p.Mode == "Tactical")
        {
            var random = Button("Draw random secondary", () => Change(() => b.DrawRandomSecondary(player), true), "battle-random-" + player);
            random.IsEnabled = b.DrawSlotsRemaining(player) > 0 && b.RandomDrawPool(player).Count > 0; secondaries.Children.Add(random);
            secondaries.Children.Add(Text($"{b.DrawSlotsRemaining(player)} draw slot(s) available · {b.RandomDrawPool(player).Count} eligible cards remaining", 13));
            var choices = BattleMissions.Secondaries("Tactical").Where(c => p.Secondaries.All(s => s.CardId != c.Id)).ToList();
            var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", MinHeight = 40, SelectedIndex = -1 };
            AutomationProperties.SetAutomationId(picker, "battle-secondary-picker-" + player);
            secondaries.Children.Add(Text("Record a card drawn at the start of your Command phase")); secondaries.Children.Add(picker);
            var add = Button("Add drawn card", () => { if (picker.SelectedItem is BattleMission selected) Change(() => b.AddSecondary(player, selected.Id), true); }, "battle-add-secondary-" + player);
            add.IsEnabled = b.DrawSlotsRemaining(player) > 0; secondaries.Children.Add(add);
        }
    }
    private void Counter(Panel panel, string label, Func<int> value, Action<int> change, string id = "", bool count = true, Func<bool>? canIncrease = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), LastChildFill = true };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var minus = Button("−", () => Change(() => change(-1)), id + "-minus");
        var number = Text("", 18, true); number.MinWidth = 38; number.TextAlignment = TextAlignment.Center;
        var plus = Button(count ? "+" : "✓", () => Change(() => change(1)), id + "-plus");
        AutomationProperties.SetName(minus, "Decrease " + label); AutomationProperties.SetName(plus, (count ? "Increase " : "Mark achieved: ") + label);
        controls.Children.Add(minus); controls.Children.Add(number); controls.Children.Add(plus);
        DockPanel.SetDock(controls, Dock.Right); row.Children.Add(controls); row.Children.Add(Text(label)); panel.Children.Add(row);
        refresh.Add(() => { number.Text = count ? value().ToString() : value() > 0 ? "✓" : "—"; minus.IsEnabled = value() > 0; plus.IsEnabled = (count || value() == 0) && (canIncrease?.Invoke() ?? true); });
    }
    private void Mission(Panel target, int player, BattleMission mission, bool end = false)
    {
        var b = battle!; var panel = new StackPanel(); panel.Children.Add(Text(mission.Name, 20, true));
        if (mission.Instructions.Length > 0) panel.Children.Add(new Expander { Header = "Card instructions & actions", Content = Text(mission.Instructions), Margin = new Thickness(0, 4, 0, 8) });
        var groups = mission.Groups.Where(g => end ? g.EndBattle : g.Applies(b.Round)).ToList();
        if (groups.Count == 0) panel.Children.Add(Text(end ? "No end-of-battle scoring." : "No scoring in this round."));
        foreach (var group in groups)
        foreach (var turn in group.Turns(player, b.Round))
        {
            panel.Children.Add(Text(group.Label(player, turn, b.Round), 14, true));
            foreach (var condition in group.Conditions)
            {
                var label = (condition.Relation == "alternative" ? "OR · " : condition.Relation == "cumulative" ? "BONUS · " : "") + condition.Text
                    + $"\n{condition.Vp} VP{(condition.Count ? " each" : "")}" + (condition.Cap.HasValue ? $" · up to {condition.Cap} VP" : "");
                Counter(panel, label, () => b.Count(player, condition, turn, b.Round, end),
                    d => b.ChangeCondition(player, mission, group, condition, turn, d), $"score-{player}-{condition.Id}-{turn}", condition.Count,
                    () => mission.Mode != "Tactical" || !b.Players[player].Secondaries.Single(s => s.CardId == mission.Id).DiscardedRound.HasValue);
                panel.Children.Add(Line());
            }
        }
        target.Children.Add(Card(panel));
    }
    private void Scoreboard()
    {
        var b = battle!;
        for (var round = 1; round <= 5; round++)
        {
            var r = round; var panel = new StackPanel();
            panel.Children.Add(Text($"ROUND {r}", 23, true));
            if (b.FinishedRound.HasValue && r > b.FinishedRound) panel.Children.Add(Text("Not played"));
            else
            {
                foreach (var primary in new[] { true, false })
                {
                    panel.Children.Add(Text(primary ? "Primary" : "Secondary", 19, true));
                    for (var player = 0; player < 2; player++)
                    {
                        var p = player; var summary = Text("", 15); panel.Children.Add(summary);
                        refresh.Add(() => summary.Text = b.ScoreboardCards(p, r, primary));
                    }
                    panel.Children.Add(Line());
                }
                for (var player = 0; player < 2; player++)
                {
                    var p = player; var cp = Text("", 13); panel.Children.Add(cp);
                    refresh.Add(() => cp.Text = $"{b.Players[p].Name} \u00b7 CP spent: {b.CpSpent(p, r)} \u00b7 Balance: {b.Cp(p, r)}");
                }
            }
            body.Children.Add(Card(panel));
        }
        body.Children.Add(Text("Battle totals", 23, true));
        for (var player = 0; player < 2; player++)
        {
            var p = player; var record = b.Players[p]; var total = Text("", 16, true); body.Children.Add(total);
            refresh.Add(() => total.Text = $"{record.Name} \u00b7 {b.Total(p)} VP\nBattle Ready: {(record.BattleReady ? 10 : 0)} VP \u00b7 End-of-battle primary: {b.Scores(p).Where(l => l.Round == 6).Sum(l => l.Vp)} VP");
            var mission = BattleMissions.Primary(record.Primary);
            if (mission.Groups.Any(g => g.EndBattle))
            {
                var scoring = new StackPanel(); Mission(scoring, p, mission, true);
                body.Children.Add(new Expander { Header = record.Name + " \u00b7 End-of-battle scoring", Content = scoring });
            }
        }
    }
}

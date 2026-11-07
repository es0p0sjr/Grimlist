using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;
public partial class ArmySetupView : UserControl
{
    private Dataset? data;
    public Func<string, Dataset?>? FactionData { get; set; }
    private List<ArmyList> saved = [];
    private List<string> factions = [];
    private string faction = "";
    private bool updating;
    private ArmyList? detachmentDraft;
    private bool UsesDp => DetachmentRules.UsesPoints(faction, data);
    public int Step { get; private set; }
    public event Action<ArmyList>? Completed;
    public event Action? CancelRequested;
    public ArmySetupView()
    {
        InitializeComponent();
        PointsDetachments.Changed += () =>
        {
            detachmentDraft = PointsDetachments.Draft;
            updating = true; LimitInput.Text = detachmentDraft.Limit.ToString(); updating = false;
            UpdateNext();
        };
    }
    public void Begin(Dataset? dataset, List<ArmyList> armies, bool canCancel, IEnumerable<string>? availableFactions = null)
    {
        updating = true;
        data = dataset; saved = armies; faction = ""; detachmentDraft = null; Step = 0;
        factions = (availableFactions ?? data?.Units.Select(u => u.Faction) ?? []).Concat(MfmImporter.Factions).Distinct().Order().ToList();
        FactionSearch.Clear(); FilterFactions(); FactionList.SelectedIndex = -1;
        DetachmentName.Clear(); NameInput.Clear(); LimitInput.Text = "2000";
        CancelButton.Visibility = Visibility.Visible;
        updating = false;
        ShowStep();
    }
    public void RefreshData(Dataset? dataset, List<ArmyList> armies, IEnumerable<string>? availableFactions = null)
    {
        data = dataset; saved = armies;
        factions = (availableFactions ?? data?.Units.Select(u => u.Faction) ?? []).Concat(MfmImporter.Factions).Distinct().Order().ToList();
        if (faction.Length > 0 && FactionData != null) data = FactionData(faction);
        FilterFactions();
        if (Step == 1) LoadDetachments();
        ShowStep();
    }
    private void FilterFactions()
    {
        updating = true;
        var order = new[] { "CHAOS", "IMPERIUM", "SPACE MARINES", "XENOS", "OTHER" };
        var tiles = factions.Where(f => f.Contains(FactionSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(f => new FactionTile(f, FactionPalette.Brush(f), FactionPalette.Group(f))).OrderBy(f => Array.IndexOf(order, f.Group)).ThenBy(f => f.Name).ToList();
        var view = new ListCollectionView(tiles); view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FactionTile.Group)));
        FactionList.ItemsSource = view;
        FactionList.SelectedValue = faction;
        updating = false;
    }
    private void SearchFactions(object sender, TextChangedEventArgs e) { if (!updating && FactionList != null) FilterFactions(); }
    private void FactionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || FactionList.SelectedValue is not string selected) return;
        if (faction != selected) { DetachmentName.Clear(); detachmentDraft = null; faction = selected; }
        if (FactionData != null) data = FactionData(faction);
        ShowStep();
    }
    private void FactionClicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FactionList, e.OriginalSource as DependencyObject) is ListBoxItem && NextButton.IsEnabled) Next(this, e);
    }
    private void LoadDetachments()
    {
        if (UsesDp) { PointsDetachments.Begin(detachmentDraft ?? ArmyList.New(faction), data); return; }
        var names = (data?.Detachments ?? []).Where(d => d.Faction == faction).Select(d => d.Name)
            .Concat(saved.Where(a => a.Faction == faction).Select(a => a.Detachment)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Order().ToList();
        updating = true;
        DetachmentList.ItemsSource = names;
        DetachmentList.SelectedItem = names.FirstOrDefault(n => n == DetachmentName.Text);
        NoDetachments.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        updating = false;
    }
    private void DetachmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && DetachmentList.SelectedItem is string name) DetachmentName.Text = name;
    }
    private void DetachmentTyped(object sender, TextChangedEventArgs e)
    {
        if (updating || NextButton == null) return;
        updating = true;
        DetachmentList.SelectedItem = DetachmentList.Items.Cast<string>().FirstOrDefault(n => n == DetachmentName.Text);
        updating = false; UpdateNext();
    }
    private void NameChanged(object sender, TextChangedEventArgs e) { if (!updating && NextButton != null) UpdateNext(); }
    private void UpdateNext()
    {
        NextButton.IsEnabled = Step switch
        {
            0 => faction.Length > 0,
            1 => UsesDp ? PointsDetachments.CanApply : !string.IsNullOrWhiteSpace(DetachmentName.Text),
            _ => (!UsesDp || PointsDetachments.CanApply) && !string.IsNullOrWhiteSpace(NameInput.Text) && int.TryParse(LimitInput.Text, out var limit) && limit is > 0 and <= 1000000
        };
        Feedback.Text = Step == 2 && !NextButton.IsEnabled ? "Enter a name and a points limit from 1 to 1,000,000." : "";
    }
    private void ShowStep()
    {
        if (Footer.Parent is Panel previousParent) previousParent.Children.Remove(Footer);
        if (Step == 2) { NameScreen.Children.Add(Footer); Footer.Margin = new Thickness(0, 20, 0, 0); }
        else { SetupRoot.Children.Add(Footer); Grid.SetRow(Footer, 2); Footer.Margin = new Thickness(0, 10, 0, 24); }
        ArmyScreen.Visibility = Step == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetachmentScreen.Visibility = Step == 1 && !UsesDp ? Visibility.Visible : Visibility.Collapsed;
        PointsDetachments.Visibility = Step == 1 && UsesDp ? Visibility.Visible : Visibility.Collapsed;
        NameScreen.Visibility = Step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Resources["Accent"] = FactionPalette.Brush(faction);
        Heading.Text = Step == 0 ? "New List - Select Faction" : $"New {faction} List - {(Step == 1 ? "Detachments" : "Name")}";
        Subheading.Text = "CHOOSE DETACHMENT";
        ArmySummary.Text = UsesDp && detachmentDraft != null ? string.Join("\n", DetachmentRules.Selected(detachmentDraft)) : DetachmentName.Text.Trim();
        LimitInput.IsReadOnly = UsesDp;
        DetachmentSummaryHint.Text = UsesDp && detachmentDraft != null
            ? DetachmentRules.SelectionSummary(detachmentDraft, data) + ". Go back to change game size or detachments."
            : "Detachment costs and rules require your own reference data.";
        BackButton.Visibility = Visibility.Visible;
        NextButton.Content = new[] { "Continue", "Continue", "Create" }[Step];
        var count = data?.Units.Count(u => u.Faction == faction) ?? 0;
        FactionHint.Text = faction.Length == 0 ? "Search or select a faction." : count > 0 ? $"{faction} · {count} units available" : $"{faction} · import points when you are ready to add units.";
        FactionHint.Visibility = Step == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNext();
    }
    private void Next(object sender, RoutedEventArgs e)
    {
        if (!NextButton.IsEnabled) return;
        if (Step == 2)
        {
            try
            {
                var created = ArmyList.New(faction) with { Name = NameInput.Text.Trim(), Limit = int.Parse(LimitInput.Text), Detachment = DetachmentName.Text.Trim(), Game = data?.Game ?? "Warhammer 40,000" };
                if (UsesDp && detachmentDraft != null) created = DetachmentRules.WithSelection(created, DetachmentRules.Selected(detachmentDraft)) with { Disposition = detachmentDraft.Disposition };
                Completed?.Invoke(created);
            }
            catch (Exception ex) { Feedback.Text = "Could not save: " + ex.Message; }
            return;
        }
        Step++;
        if (Step == 1) LoadDetachments();
        ShowStep();
        if (Step == 2) NameInput.Focus();
    }
    private void Back(object sender, RoutedEventArgs e) { if (Step > 0) { Step--; ShowStep(); } else CancelRequested?.Invoke(); }
    private void Cancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            Next(this, e);
            e.Handled = true;
        }
    }
    private record FactionTile(string Name, Brush Color, string Group);
}

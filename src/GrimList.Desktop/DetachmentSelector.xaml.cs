using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class DetachmentSelector : UserControl
{
    private Dataset? data;
    private bool updating;
    public ArmyList Draft { get; private set; } = ArmyList.New("");
    public bool CanApply => DetachmentRules.Issues(Draft, data).Count == 0 && DetachmentRules.Selected(Draft).Count > 0;
    public event Action? Changed;
    public DetachmentSelector() { InitializeComponent(); }
    public void Begin(ArmyList army, Dataset? dataset)
    {
        data = dataset; Draft = army; updating = true;
        GameSizePicker.ItemsSource = new[] { 1000, 2000, 3000 }.Select(points => new GameSize(points,
            points == 1000 ? "1,000 points · 2 DP or single 3 DP" : $"{points:N0} points · {DetachmentRules.BudgetLabel(points)}")).ToArray();
        GameSizePicker.SelectedValue = army.Limit;
        updating = false; Refresh();
    }
    private void Refresh()
    {
        Draft = DetachmentRules.NormalizeDisposition(Draft, data);
        var preview = (OptionsList.SelectedItem as Option)?.Name ?? DetachmentRules.Selected(Draft).FirstOrDefault();
        updating = true;
        var spent = DetachmentRules.Spent(Draft, data); var budget = DetachmentRules.Budget(Draft.Limit);
        BudgetText.Text = DetachmentRules.SelectionSummary(Draft, data);
        var issues = DetachmentRules.Issues(Draft, data);
        BudgetText.Foreground = (Brush)FindResource(issues.Count > 0 && DetachmentRules.Selected(Draft).Count > 0 ? "Warning" : "Ink");
        BudgetHint.Text = issues.Count > 0 ? string.Join("\n", issues)
            : DetachmentRules.UsesSingleDetachmentAllowance(Draft, data) ? "Single-detachment allowance used; remove it before selecting another detachment."
            : $"{budget - spent} DP remaining";
        SelectedCards.ItemsSource = DetachmentRules.Selected(Draft);
        var dispositions = DetachmentRules.Dispositions(Draft, data);
        DispositionPicker.ItemsSource = dispositions;
        DispositionPicker.SelectedItem = Draft.Disposition;
        DispositionPicker.IsEnabled = dispositions.Count > 1;
        var options = DetachmentRules.Options(Draft, data).Select(d => new Option(d.Name,
            DetachmentRules.Cost(d) is int cost ? $"{cost} DP" : "Cost unknown",
            string.Join(" · ", new[] { DetachmentRules.SelectionMetadata(d, data), DetachmentRules.Contains(Draft, d.Name, data) ? "Selected" : "" }.Where(s => s.Length > 0)),
            DetachmentRules.Contains(Draft, d.Name, data) ? "Remove" : "Add", DetachmentRules.Contains(Draft, d.Name, data) || DetachmentRules.CanAdd(Draft, d.Name, data))).ToList();
        OptionsList.ItemsSource = options;
        OptionsList.SelectedValue = options.FirstOrDefault(o => o.Name == preview)?.Name ?? options.FirstOrDefault()?.Name;
        updating = false; RefreshPreview(); Changed?.Invoke();
    }
    private void GameSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || GameSizePicker.SelectedValue is not int limit) return;
        Draft = Draft with { Limit = limit }; Refresh();
    }
    private void PreviewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        RefreshPreview(); PreviewScroll.ScrollToTop();
    }
    private void RefreshPreview()
    {
        var name = (OptionsList.SelectedItem as Option)?.Name ?? "";
        var definition = DetachmentRules.Find(Draft, name, data);
        var selected = DetachmentRules.Contains(Draft, name, data);
        var cost = DetachmentRules.Cost(definition); var spent = DetachmentRules.Spent(Draft, data); var budget = DetachmentRules.Budget(Draft.Limit);
        PreviewName.Text = name.Length > 0 ? name : "Select a detachment";
        PreviewMeta.Text = definition == null ? "" : string.Join(" · ", new[] { definition.Cost, definition.DispositionLabel }.Where(s => s.Length > 0));
        ToggleButton.Content = selected ? "Remove detachment" : "Add detachment";
        ToggleButton.IsEnabled = definition != null && (selected || DetachmentRules.CanAdd(Draft, name, data));
        SelectionHint.Text = selected ? "Selected for this army." : !cost.HasValue ? "Import a DP cost before adding this detachment."
            : !budget.HasValue ? "Choose a game size to set your DP budget."
            : !spent.HasValue ? "Remove selections with unknown DP costs before adding a detachment."
            : !DetachmentRules.CanAdd(Draft, name, data) ? $"Needs {cost} DP; {Math.Max(0, budget.Value - spent.Value)} DP available." +
                (Draft.Limit == 1000 ? " A 3-DP detachment must be selected alone." : "")
            : "Review the rules below, then add this detachment to your army.";
        if (!selected && DetachmentRules.CombinationIssues(DetachmentRules.WithSelection(Draft, [.. DetachmentRules.Selected(Draft), name]), data) is { Count: > 0 } conflicts)
            SelectionHint.Text = string.Join("\n", conflicts);
        var cards = (data?.ReferenceCards ?? []).Where(c => c.Faction == Draft.Faction && UnitIdentity.Normalize(c.Detachment) == UnitIdentity.Normalize(name)).ToList();
        RuleCards.ItemsSource = cards.Where(c => c.Section == "Detachments").ToList();
        var stratagems = ArmyReferences.ForArmy(DetachmentRules.WithSelection(Draft, [name]), data)
            .Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
        StratagemCards.ItemsSource = stratagems;
        EnhancementCards.ItemsSource = cards.Where(c => c.Section == "Enhancements").ToList();
        NoReferences.Visibility = cards.Count == 0 && stratagems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Toggle(object sender, RoutedEventArgs e)
    {
        if (!ToggleButton.IsEnabled || OptionsList.SelectedItem is not Option option) return;
        ToggleName(option.Name);
    }
    private void QuickToggle(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) ToggleName(name);
        e.Handled = true;
    }
    private void ChooseDisposition(object sender, SelectionChangedEventArgs e)
    {
        if (updating || DispositionPicker.SelectedItem is not string disposition) return;
        Draft = Draft with { Disposition = disposition }; Refresh();
    }
    private void ToggleName(string name)
    {
        if (!DetachmentRules.Contains(Draft, name, data) && !DetachmentRules.CanAdd(Draft, name, data)) return;
        OptionsList.SelectedValue = name;
        var names = DetachmentRules.Selected(Draft);
        if (DetachmentRules.Contains(Draft, name, data)) names.RemoveAll(n => UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, Draft.Faction, n)) == UnitIdentity.Normalize(name));
        else names.Add(name);
        Draft = DetachmentRules.WithSelection(Draft, names); Refresh();
    }
    private void Remove(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        Draft = DetachmentRules.WithSelection(Draft, DetachmentRules.Selected(Draft).Where(n => n != name)); Refresh();
    }
    private record GameSize(int Points, string Label);
    private record Option(string Name, string CostLabel, string Detail, string Action, bool Enabled);
}

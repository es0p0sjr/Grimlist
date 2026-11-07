using System.Windows;
using System.Windows.Controls;
using GrimList.Core;
using GrimList.Application;

namespace GrimList.Desktop;
public partial class HomeView : UserControl
{
    private List<SavedArmySummary> armies = [];
    private Dataset? data;
    public event Action? NewRequested;
    public event Action<ArmyList>? OpenRequested;
    public event Action<ArmyList>? DuplicateRequested;
    public event Action<ArmyList>? DeleteRequested;
    public HomeView() { InitializeComponent(); }
    public void Refresh(Dataset? dataset, List<SavedArmySummary> saved)
    {
        data = dataset; armies = saved;
        DatasetText.Text = "DATASET: " + (data?.Game ?? "No points imported");
        VersionText.Text = data == null ? "Import points from the menu to add units." : $"VERSION: {data.Source.Version} ({(data.Source.Date.Length > 0 ? data.Source.Date : "date unknown")})";
        RenderTiles();
    }
    private void RenderTiles()
    {
        while (TilePanel.Children.Count > 2) TilePanel.Children.RemoveAt(2);
        var matching = armies.Where(s => (s.Army.Name + " " + s.Army.Faction + " " + string.Join(" ", DetachmentRules.Selected(s.Army))).Contains(HomeSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var saved in matching)
        {
            var army = saved.Army;
            var result = saved.Result;
            var tile = new Grid { Width = 174, Height = 112, Margin = new Thickness(0, 0, 10, 10) };
            var button = new Button { Background = FactionPalette.Brush(army.Faction), BorderThickness = new Thickness(0), Padding = new Thickness(7), Tag = army, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var layout = new DockPanel();
            var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            summary.Children.Add(new TextBlock { Text = army.Faction, FontSize = 12 });
            summary.Children.Add(new TextBlock { Text = string.Join(" · ", DetachmentRules.Selected(army)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            summary.Children.Add(new TextBlock { Text = $"{result.Total}/{army.Limit} points{(result.Complete ? "" : " · incomplete")}", FontSize = 12 });
            DockPanel.SetDock(summary, Dock.Bottom); layout.Children.Add(summary);
            layout.Children.Add(new TextBlock { Text = army.Name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 25, 0), FontSize = 15, MaxHeight = 40 });
            button.Content = layout; button.Click += (_, _) => OpenRequested?.Invoke(army); tile.Children.Add(button);
            var more = new Button { Content = "•••", FontSize = 14, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Army actions" };
            more.Click += (_, _) =>
            {
                var menu = new ContextMenu { PlacementTarget = more };
                var duplicate = new MenuItem { Header = "Duplicate army" }; duplicate.Click += (_, _) => DuplicateRequested?.Invoke(army);
                var delete = new MenuItem { Header = "Delete army" }; delete.Click += (_, _) => DeleteRequested?.Invoke(army);
                menu.Items.Add(duplicate); menu.Items.Add(delete); menu.IsOpen = true;
            };
            tile.Children.Add(more); TilePanel.Children.Add(tile);
        }
        EmptyText.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = armies.Count == 0 ? "Create your first army with +." : "No matching armies.";
    }
    private void NewArmy(object sender, RoutedEventArgs e) => NewRequested?.Invoke();
    private void SearchArmies(object sender, RoutedEventArgs e) => FocusSearch();
    public void FocusSearch() { SearchPanel.Visibility = Visibility.Visible; HomeSearch.Focus(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (TilePanel != null) RenderTiles(); }
}

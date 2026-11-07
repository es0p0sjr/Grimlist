using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private ContextMenu ActionMenu(UIElement target) => new()
    {
        Style = (Style)FindResource("ActionMenu"), PlacementTarget = target
    };

    private MenuItem MenuAction(ContextMenu menu, string title, string description, string glyph, Action action)
    {
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (description.Length > 0)
            label.Children.Add(new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 224 });
        var item = new MenuItem
        {
            Style = (Style)FindResource("ActionMenuItem"),
            Header = label,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 }
        };
        AutomationProperties.SetName(item, title);
        item.Click += (_, _) => Run(action);
        menu.Items.Add(item);
        return item;
    }

    private void OpenUnitMenu(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var keyboard = e.CursorLeft < 0;
        if (keyboard && Roster.SelectedItem == null) return;
        var container = keyboard ? Roster.ItemContainerGenerator.ContainerFromItem(Roster.SelectedItem) as ListBoxItem
            : ItemsControl.ContainerFromElement(Roster, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not RosterRow row) return;
        Roster.SelectedValue = row.Id;
        var menu = ActionMenu(container);
        menu.Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint;
        var unit = army.Entries.FirstOrDefault(a => a.Id == row.Id) is { } entry ? data?.FindUnit(entry.UnitKey) : null;
        var problem = playMode ? "Switch to the builder to duplicate units."
            : unit == null ? "Datasheet unavailable."
            : MusterRules.AddProblem(army, data, unit) ?? UnitAvailability.Problem(army, unit, data);
        var duplicate = MenuAction(menu, "Duplicate unit", problem ?? "Copy this unit and its equipment", "\uE8C8", () => DuplicateEntry(row.Id));
        duplicate.IsEnabled = problem == null;
        duplicate.ToolTip = problem ?? "The copy is added as a separate unit.";
        ToolTipService.SetShowOnDisabled(duplicate, true);
        Roster.ContextMenu = menu;
        menu.IsOpen = true;
    }
}

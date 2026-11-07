using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{


    private FrameworkElement SectionChoices(UnitDefinition unit, ArmyEntry? entry, UnitLoadout loadout, LoadoutDefinition definition, LoadoutSection section, int size)
    {
        var roles = definition.Roles.Where(r => section.RoleIds.Contains(r.Id)).ToList();
        var models = loadout.Models.Where(m => section.RoleIds.Contains(m.RoleId)).ToList();
        var panel = new StackPanel { Margin = new(8) };
        if (roles.Count > 1)
        {
            var standard = roles.Single(r => r.Id == section.DefaultRoleId);
            var defaults = Loadouts.Defaults(standard.Groups, size);
            var standardNames = standard.Groups.SelectMany(g => g.Options.Where(o => defaults.Any(e => e.GroupId == g.Id && e.OptionId == o.Id))).Select(o => o.Name).ToList();
            panel.Children.Add(Label($"{Loadouts.RoleCount(loadout, standard.Id)} × {string.Join(" + ", standardNames)}", true));
            panel.Children.Add(Label("Replace standard equipment with:"));
            var pools = (definition.ModelGroups ?? []).Where(g => g.RoleIds.Count < section.RoleIds.Count
                && g.RoleIds.All(section.RoleIds.Contains) && !g.RoleIds.Contains(standard.Id)).ToList();
            var choices = roles.Where(r => r.Id != standard.Id).GroupBy(role => pools
                .Where(g => g.RoleIds.Contains(role.Id)).OrderBy(g => g.RoleIds.Count).FirstOrDefault())
                .OrderBy(g => g.Key == null ? 0 : 1);
            foreach (var choiceGroup in choices)
            {
                var destination = panel;
                if (choiceGroup.Key is { } pool)
                {
                    destination = new StackPanel();
                    destination.Children.Add(Label(pool.Name, true));
                    var count = models.Count(m => pool.RoleIds.Contains(m.RoleId));
                    var maximum = Math.Min(models.Count, pool.Maximum.At(size, loadout.Models));
                    var minimum = pool.Minimum.At(size, loadout.Models);
                    destination.Children.Add(new TextBlock
                    {
                        Text = $"{count} / {maximum} selected · Shared limit" + (minimum > 0 ? $" · Minimum {minimum}" : ""),
                        TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 0, 0, 10)
                    });
                    var border = new Border
                    {
                        Child = destination, Background = (Brush)FindResource("AccentDark"),
                        BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new(2, 0, 0, 0),
                        Padding = new(10, 10, 10, 0), Margin = new(0, 4, 0, 10), CornerRadius = new(3)
                    };
                    System.Windows.Automation.AutomationProperties.SetName(border, pool.Name);
                    panel.Children.Add(border);
                }
                foreach (var role in choiceGroup)
                {
                    var equipment = Loadouts.Defaults(role.Groups, size);
                    var names = role.Groups.SelectMany(g => g.Options.Where(o => equipment.Any(e => e.GroupId == g.Id && e.OptionId == o.Id))).Select(o => o.Name).ToList();
                    var additions = names.Except(standardNames).ToList();
                    var title = additions.Count > 0 ? string.Join(" + ", additions) : role.Name;
                    if (unit.ModelCosts?.FirstOrDefault(c => c.OptionId == role.Id)?.Points is > 0 and var modelPoints)
                        title += $" (+{modelPoints} pts per model)";
                    var maximum = Math.Min(models.Count, role.Maximum.At(size, loadout.Models));
                    var rule = $"Maximum {maximum} in this unit";
                    foreach (var otherPool in pools.Where(g => g != choiceGroup.Key && g.RoleIds.Contains(role.Id)))
                        rule += $" · {otherPool.Name}: {otherPool.Maximum.At(size, loadout.Models)} combined";
                    destination.Children.Add(OptionCounter(unit, entry, title, Loadouts.RoleCount(loadout, role.Id), rule,
                        new(section.Id, role.Id, "", "", -1), new(section.Id, role.Id, "", "", 1), string.Join(" + ", names)));
                }
            }
        }
        foreach (var role in roles)
        {
            var indices = loadout.Models.Select((m, i) => (Model: m, Index: i)).Where(s => s.Model.RoleId == role.Id).Select(s => s.Index).ToList();
            if (indices.Count == 0) continue;
            var groups = role.Groups.Where(g => roles.Count == 1 || g.Options.Count > 1 || g.Minimum.At(size, loadout.Models) != g.Maximum.At(size, loadout.Models)).ToList();
            foreach (var group in groups)
            {
                if (role.Minimum.At(size, loadout.Models) == 1 && role.Maximum.At(size, loadout.Models) == 1)
                {
                    panel.Children.Add(EquipmentChoices(unit, entry, loadout, group, indices[0], size));
                    continue;
                }
                panel.Children.Add(Label(group.Name, true));
                var exclusive = group.Maximum.At(size, loadout.Models) == 1;
                var standardId = Loadouts.DefaultOption(group, size);
                foreach (var option in group.Options)
                {
                    var count = Loadouts.OptionCount(loadout, role.Id, group.Id, option.Id);
                    if (exclusive && option.Id == standardId || option.Minimum.At(size, loadout.Models) == option.Maximum.At(size, loadout.Models))
                    {
                        panel.Children.Add(Label($"{count} × {WargearPricing.Label(unit, option)}"));
                        continue;
                    }
                    var maximum = Math.Min(indices.Count * option.Maximum.At(size, loadout.Models), option.UnitMaximum?.At(size, loadout.Models) ?? int.MaxValue);
                    panel.Children.Add(OptionCounter(unit, entry, WargearPricing.Label(unit, option), count, $"Maximum {maximum} in this unit",
                        new(section.Id, role.Id, group.Id, option.Id, -1), new(section.Id, role.Id, group.Id, option.Id, 1)));
                }
            }
        }
        var selected = Loadouts.Equipment(unit, loadout with { Models = models, Equipment = [] }).GroupBy(e => e.Option.Name)
            .Select(g => $"{g.Sum(e => e.Count)} × {g.Key}");
        var header = new StackPanel();
        var name = System.Text.RegularExpressions.Regex.Replace(section.Name, @"^\d+\s*[-–]\s*\d+\s+", "");
        header.Children.Add(Label(roles.Count == 1 && roles[0].Maximum.At(size, loadout.Models) == 1 ? name : $"{models.Count} × {name}", true));
        header.Children.Add(new TextBlock { Text = string.Join(" · ", selected), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        var card = new Expander { Header = header, Content = panel, IsExpanded = !closedLoadoutCards.Contains(section.Id), Margin = new(0, 0, 0, 8) };
        card.Expanded += (_, _) => closedLoadoutCards.Remove(section.Id);
        card.Collapsed += (_, _) => closedLoadoutCards.Add(section.Id);
        return card;
    }

    private FrameworkElement OptionCounter(UnitDefinition unit, ArmyEntry? entry, string title, int count, string rule, LoadoutCountAction minus, LoadoutCountAction plus, string? description = null)
    {
        var panel = new StackPanel { Margin = new(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = rule, Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(2, 0, 2, 4) });
        var row = new Grid { Background = (Brush)FindResource("Input"), ToolTip = description };
        row.ColumnDefinitions.Add(new() { Width = new(34) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(34) });
        Button Control(string text, LoadoutCountAction action, string verb)
        {
            var button = new Button { Content = text, Tag = action, MinHeight = 34, Padding = new(0), FontSize = 20, Background = (Brush)FindResource("Accent"), IsEnabled = CanAdjust(unit, entry, action) };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{verb} {title}");
            button.ToolTip = button.IsEnabled ? $"{verb} {title}" : action.Delta > 0 ? "Limit reached or no models with standard equipment remain." : "No removable selections.";
            button.Click += AdjustOptionCount; return button;
        }
        var decrease = Control("−", minus, "Remove"); var increase = Control("+", plus, "Add");
        var label = Label($"{count} × {title}", true); label.Margin = new(8, 5, 8, 5); label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1); Grid.SetColumn(increase, 2);
        row.Children.Add(decrease); row.Children.Add(label); row.Children.Add(increase); panel.Children.Add(row);
        return panel;
    }

    private static bool CanAdjust(UnitDefinition unit, ArmyEntry? entry, LoadoutCountAction action) => ArmySession.CanAdjust(unit, entry, action);
    private void AdjustOptionCount(object sender, RoutedEventArgs e)
    {
        if (playMode || sender is not Button { Tag: LoadoutCountAction action }) return;
        var entry = army.Entries.FirstOrDefault(e => e.Id == detailEntryId); var unit = entry == null ? null : data?.FindUnit(entry.UnitKey);
        if (entry == null || unit == null) return;
        Run(() => Edit(() => session.AdjustEquipment(entry.Id, action)));
    }
}

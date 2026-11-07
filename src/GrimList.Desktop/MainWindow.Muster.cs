using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private void RefreshMuster()
    {
        MusterPanel.Children.Clear();
        foreach (var rule in MusterEffects.Rules(army, data))
        {
            var choices = new StackPanel();
            choices.Children.Add(new TextBlock { Text = rule.Detachment + " · " + MusterEffects.Summary(army, rule), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) });
            var selected = MusterEffects.Selected(army, rule);
            var candidates = MusterEffects.Candidates(army, data, rule);
            if (candidates.Count == 0) choices.Children.Add(new TextBlock { Text = "Add eligible units to nominate them here.", TextWrapping = TextWrapping.Wrap });
            foreach (var candidate in candidates)
            {
                var chosen = selected.Contains(candidate);
                var check = new CheckBox { Content = MusterEffects.Label(army, candidate), IsChecked = chosen,
                    IsEnabled = !playMode && (chosen || selected.Count < rule.Rule.Maximum), Margin = new(0, 4, 0, 4) };
                check.Click += (_, _) => Run(() => Edit(() => session.SelectMusterNomination(candidate, check.IsChecked == true)));
                choices.Children.Add(check);
            }
            MusterPanel.Children.Add(new Expander { Header = new TextBlock { Text = "Muster nominations · " + rule.Rule.Name
                    + $"\n{selected.Count} selected · required {rule.Rule.Minimum} · max {rule.Rule.Maximum}", TextWrapping = TextWrapping.Wrap },
                IsExpanded = selected.Count < rule.Rule.Minimum, Content = new ScrollViewer { Content = choices, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        }
        MusterPanel.Visibility = MusterPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

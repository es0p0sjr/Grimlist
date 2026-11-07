using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private UnitDefinition? referenceUnit;
    private List<RuleDefinition> feelNoPainRules = [];
    private void ShowPrimaryMission(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PrimaryMatchup matchup })
            new PrimaryMissionWindow(matchup) { Owner = this }.ShowDialog();
    }
    private void ShowWeaponRules(WeaponProfile weapon)
        => new RuleReferenceWindow(weapon.Name, WeaponRuleReference.ForWeapon(weapon, referenceUnit, data)) { Owner = this }.ShowDialog();
    private void WeaponProfileTapped(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is DataGrid table
            && ItemsControl.ContainerFromElement(table, e.OriginalSource as DependencyObject) is DataGridRow { Item: WeaponProfile weapon })
        { e.Handled = true; ShowWeaponRules(weapon); }
    }
    private void WeaponProfileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is DataGrid { SelectedItem: WeaponProfile weapon })
        { e.Handled = true; ShowWeaponRules(weapon); }
    }
    private void ShowFeelNoPain(object sender, RoutedEventArgs e)
        => new RuleReferenceWindow("Feel No Pain", feelNoPainRules) { Owner = this }.ShowDialog();
}

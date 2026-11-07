using System.Windows;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    private readonly HashSet<string> afflictedTargets = [];
    private string? effectContextId;
    private void TargetConditionChanged(object sender, RoutedEventArgs e)
    {
        if (rendering || renderingDetails || effectContextId == null) return;
        if (TargetAfflicted.IsChecked == true) afflictedTargets.Add(effectContextId); else afflictedTargets.Remove(effectContextId);
        RefreshDetails();
    }
    private List<WeaponProfile> RefreshEffects(UnitDefinition? unit, ArmyEntry? entry, List<WeaponProfile> weapons)
    {
        var effects = AttachedEffects.ForUnit(army, data, entry);
        effectContextId = entry?.AttachedToId is { Length: > 0 } parent ? parent : entry?.Id;
        TargetAfflicted.IsChecked = effectContextId != null && afflictedTargets.Contains(effectContextId);
        TargetAfflicted.Visibility = effects.Any(e => e.Adjustment?.RequiresAfflicted == true) ? Visibility.Visible : Visibility.Collapsed;
        var active = TargetAfflicted.IsChecked == true;
        var abilities = (playMode && unit != null ? Loadouts.Abilities(unit, entry?.Loadout) : unit?.Profile?.Abilities ?? [])
            .Where(a => !effects.Any(e => e.Ability.Name == a.Name && e.Ability.Text == a.Text)).ToList();
        abilities.InsertRange(0, effects.Select(e => e.Ability with
        {
            Section = $"{e.Source} · " + (e.Adjustment?.RequiresAfflicted == true
                ? active ? "Applied against Afflicted target" : "Against Afflicted targets" : e.Adjustment != null ? "Applied to weapons" : "Attached ability")
        }));
        abilities = abilities.Where(a => !WeaponRuleReference.IsWeaponDefinition(a, (unit?.Profile?.Weapons ?? []).Concat(weapons))).ToList();
        AbilityCards.ItemsSource = AbilityGroups.Create(abilities);
        AbilitiesSection.Visibility = abilities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AbilitiesRow.Height = abilities.Count > 0 ? new(1, GridUnitType.Star) : new(0);
        LoadoutColumn.Width = playMode && abilities.Count == 0 ? new(0) : loadoutWidth;
        LoadoutGap.Width = new(playMode && abilities.Count == 0 ? 0 : 12);
        SidebarGap.Height = new(!playMode && abilities.Count > 0 ? 8 : 0);
        SidebarDivider.Visibility = !playMode && abilities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return AttachedEffects.Weapons(weapons, effects.Where(e => e.Adjustment?.RequiresAfflicted == true), active).Select(w => w.Profile).ToList();
    }
}

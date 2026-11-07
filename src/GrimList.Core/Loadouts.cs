using System.Text.RegularExpressions;

namespace GrimList.Core;

public record LoadoutQuery(string Scope, string ChildId, bool IncludeChildSelections, List<string> RoleIds);
public record LoadoutAdjustment(string Operation, int Value, string Comparison = "", int Threshold = 0, int PerModels = 0, LoadoutQuery? Query = null);
public record LoadoutBound(int Value, List<LoadoutAdjustment>? Adjustments = null)
{
    public int At(int models, IEnumerable<ModelLoadout>? selected = null)
    {
        var result = Value;
        foreach (var rule in Adjustments ?? [])
        {
            // Named selections are counted from the actual loadout, never inferred from total size.
            if (rule.Query != null && selected == null) continue;
            var count = rule.Query == null ? models : selected!.Count(m => rule.Query.RoleIds.Contains(m.RoleId));
            var applies = rule.Comparison switch
            {
                "" => true, "atLeast" => count >= rule.Threshold, "greaterThan" => count > rule.Threshold,
                "atMost" => count <= rule.Threshold, "lessThan" => count < rule.Threshold,
                "equalTo" => count == rule.Threshold, "notEqualTo" => count != rule.Threshold, _ => false
            };
            if (!applies) continue;
            var amount = rule.Value * (rule.PerModels > 0 ? models / rule.PerModels : 1);
            result = rule.Operation switch { "set" => amount, "increment" => result + amount, "decrement" => result - amount, "cap" => Math.Min(result, amount), _ => result };
        }
        return Math.Clamp(result, 0, 1000);
    }
}
public record EquipmentOption(string Id, string Name, List<string> Weapons, List<string> Abilities,
    LoadoutBound Minimum, LoadoutBound Maximum, LoadoutBound? UnitMaximum = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<string>? WeaponSourceIds = null);
public record EquipmentGroup(string Id, string Name, List<EquipmentOption> Options, LoadoutBound Minimum,
    LoadoutBound Maximum, string DefaultOptionId = "");
public record LoadoutRole(string Id, string Name, LoadoutBound Minimum, LoadoutBound Maximum, List<EquipmentGroup> Groups, bool Default = false);
public record LoadoutModelGroup(string Name, List<string> RoleIds, LoadoutBound Minimum, LoadoutBound Maximum);
public record LoadoutDefinition(List<LoadoutRole> Roles, List<EquipmentGroup> UnitGroups, List<string> Warnings, List<LoadoutModelGroup>? ModelGroups = null,
    SelectionComposition? Composition = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Incomplete = false);
public record EquipmentSelection(string GroupId, string OptionId, int Count = 1);
public record ModelLoadout(string RoleId, List<EquipmentSelection> Equipment);
public record UnitLoadout(List<ModelLoadout> Models, List<EquipmentSelection> Equipment, bool Manual = false);
public record EquippedWeapon(WeaponProfile Profile, int Count);

public static partial class Loadouts
{
    public const string ReviewWarning = "Equipment replacement limits are incomplete; review this loadout against its source.";
    // Legacy profiles have no default equipment or replacement limits. Keep their choices explicit.
    public static LoadoutDefinition Definition(UnitDefinition unit, UnitLoadout? saved = null)
    {
        if (saved?.Manual != true && unit.Profile?.Loadout is { } definition)
        {
            var choiceIds = (unit.Profile.Semantics?.Choices ?? []).Select(g => g.Id).ToHashSet();
            return choiceIds.Count == 0 ? definition : definition with { UnitGroups = definition.UnitGroups.Where(g => !choiceIds.Contains(g.Id)).ToList(),
                Roles = definition.Roles.Select(r => r with { Groups = r.Groups.Where(g => !choiceIds.Contains(g.Id)).ToList() }).ToList() };
        }
        var options = ManualOptions(unit.Profile?.Weapons ?? []);
        return new([], options.Count == 0 ? [] : [new("manual", "Equipped weapons", options, new(0), new(1000))],
            ["Automatic loadout options have not been imported. Set weapon quantities here, or import datasheets to load defaults and replacement limits."]);
    }

    public static List<EquipmentOption> ManualOptions(IEnumerable<WeaponProfile> weapons) => weapons.Where(w => w.GrantedBy == null)
        .GroupBy(w => WeaponFamily(w.Name)).Select(g => new EquipmentOption("manual:" + g.Key, g.Key,
            g.Select(w => w.Name).ToList(), [], new(0), new(1000), WeaponSourceIds: g.Select(w => w.SourceId)
                .Where(id => !string.IsNullOrEmpty(id)).Cast<string>().Distinct().ToList())).ToList();

    public static string WeaponFamily(string name)
    {
        var clean = name.Trim().TrimStart('➤', '►', '▶').Trim();
        return Regex.Replace(clean, @"\s+[–—-]\s+(standard|supercharge|supercharged|strike|sweep|focused|dispersed)$", "", RegexOptions.IgnoreCase);
    }

    public static UnitLoadout Create(UnitDefinition unit, int models)
        => Resize(unit, new([], [], unit.Profile?.Loadout == null), models);

    public static UnitLoadout Resize(UnitDefinition unit, UnitLoadout current, int models)
    {
        var definition = Definition(unit, current);
        if (current.Manual) return current; // Explicit manual counts are never silently reinterpreted.
        if (definition.Composition != null) return SelectionCompositions.Select(unit, current, models);
        var slots = current.Models.Take(models).ToList();
        bool Available(LoadoutRole role) => slots.Count(s => s.RoleId == role.Id) < role.Maximum.At(models, slots)
            && (definition.ModelGroups ?? []).Where(g => g.RoleIds.Contains(role.Id)).All(g => slots.Count(s => g.RoleIds.Contains(s.RoleId)) < g.Maximum.At(models, slots));
        foreach (var role in definition.Roles)
        {
            var missing = Math.Min(models, role.Minimum.At(models, slots)) - slots.Count(s => s.RoleId == role.Id);
            while (missing-- > 0)
            {
                var replacement = new ModelLoadout(role.Id, Defaults(role.Groups, models));
                if (slots.Count < models) slots.Add(replacement);
                else
                {
                    var index = slots.FindLastIndex(s => slots.Count(x => x.RoleId == s.RoleId) > (definition.Roles.FirstOrDefault(r => r.Id == s.RoleId)?.Minimum.At(models, slots) ?? 0));
                    if (index >= 0) slots[index] = replacement;
                }
            }
        }
        foreach (var group in definition.ModelGroups ?? [])
            while (slots.Count < models && slots.Count(s => group.RoleIds.Contains(s.RoleId)) < group.Minimum.At(models, slots))
            {
                var role = definition.Roles.Where(r => group.RoleIds.Contains(r.Id)).OrderByDescending(r => r.Default).FirstOrDefault(Available);
                if (role == null) break;
                slots.Add(new(role.Id, Defaults(role.Groups, models)));
            }
        while (slots.Count < models && definition.Roles.Count > 0)
        {
            var role = definition.Roles.OrderByDescending(r => r.Default).FirstOrDefault(Available);
            if (role == null) break;
            slots.Add(new(role.Id, Defaults(role.Groups, models)));
        }
        // Resizing can lower weapon-role limits; return surplus specialists to standard equipment.
        foreach (var section in Sections(unit))
            foreach (var role in definition.Roles.Where(r => section.RoleIds.Contains(r.Id) && r.Id != section.DefaultRoleId))
                while (slots.Count(s => s.RoleId == role.Id) > role.Maximum.At(models, slots))
                {
                    var standard = definition.Roles.Single(r => r.Id == section.DefaultRoleId);
                    if (slots.Count(s => s.RoleId == standard.Id) >= standard.Maximum.At(models, slots)
                        || (definition.ModelGroups ?? []).Where(g => g.RoleIds.Contains(standard.Id))
                            .Any(g => slots.Count(s => g.RoleIds.Contains(s.RoleId)) - (g.RoleIds.Contains(role.Id) ? 1 : 0) >= g.Maximum.At(models, slots))) break;
                    var index = slots.FindLastIndex(s => s.RoleId == role.Id);
                    slots[index] = new(standard.Id, Defaults(standard.Groups, models));
                }
        foreach (var group in definition.ModelGroups ?? [])
            while (slots.Count(s => group.RoleIds.Contains(s.RoleId)) > group.Maximum.At(models, slots))
            {
                var section = Sections(unit).FirstOrDefault(s => group.RoleIds.All(s.RoleIds.Contains) && !group.RoleIds.Contains(s.DefaultRoleId));
                if (section == null) break;
                var standard = definition.Roles.Single(r => r.Id == section.DefaultRoleId);
                if (slots.Count(s => s.RoleId == standard.Id) >= standard.Maximum.At(models, slots)) break;
                var index = slots.FindLastIndex(s => group.RoleIds.Contains(s.RoleId));
                slots[index] = new(standard.Id, Defaults(standard.Groups, models));
            }
        // A smaller squad may no longer allow an optional support model. Replace
        // surplus dependent roles with an available role, preserving other slots.
        foreach (var role in definition.Roles.Where(r => r.Maximum.Adjustments?.Any(a => a.Query != null) == true))
            while (slots.Count(s => s.RoleId == role.Id) > role.Maximum.At(models, slots))
            {
                var index = slots.FindLastIndex(s => s.RoleId == role.Id);
                var previous = slots[index]; slots.RemoveAt(index);
                var replacement = definition.Roles.Where(r => r.Id != role.Id).OrderByDescending(r => r.Default).FirstOrDefault(Available);
                if (replacement == null) { slots.Insert(index, previous); break; }
                slots.Insert(index, new(replacement.Id, Defaults(replacement.Groups, models)));
            }
        return new(slots, current.Equipment.Count > 0 ? current.Equipment.ToList() : Defaults(definition.UnitGroups, models));
    }

    public static List<EquipmentSelection> Defaults(List<EquipmentGroup> groups, int models)
    {
        List<EquipmentSelection> result = [];
        foreach (var group in groups)
        {
            foreach (var option in group.Options)
                if (option.Minimum.At(models) > 0) result.Add(new(group.Id, option.Id, option.Minimum.At(models)));
            var remaining = group.Minimum.At(models) - result.Where(s => s.GroupId == group.Id).Sum(s => s.Count);
            foreach (var option in group.Options.OrderByDescending(o => o.Id == group.DefaultOptionId))
            {
                if (remaining <= 0) break;
                var selected = result.FirstOrDefault(s => s.GroupId == group.Id && s.OptionId == option.Id);
                var count = Math.Min(remaining, option.Maximum.At(models) - (selected?.Count ?? 0));
                if (count <= 0) continue;
                result.RemoveAll(s => s.GroupId == group.Id && s.OptionId == option.Id);
                result.Add(new(group.Id, option.Id, (selected?.Count ?? 0) + count)); remaining -= count;
            }
        }
        return result;
    }

    public static UnitLoadout Choose(UnitDefinition unit, ArmyEntry entry, int modelIndex, string groupId, string optionId, int count)
    {
        if (count < 0) throw new InvalidDataException("Equipment quantities cannot be negative.");
        var loadout = entry.Loadout ?? Create(unit, entry.Models);
        var definition = Definition(unit, loadout);
        var groups = modelIndex < 0 ? definition.UnitGroups : definition.Roles.Single(r => r.Id == loadout.Models[modelIndex].RoleId).Groups;
        var group = groups.Single(g => g.Id == groupId);
        if (!group.Options.Any(o => o.Id == optionId)) throw new InvalidDataException("That equipment option is no longer available.");
        var option = group.Options.Single(o => o.Id == optionId);
        if (count < option.Minimum.At(entry.Models) || count > option.Maximum.At(entry.Models)) throw new InvalidDataException($"{option.Name}: quantity exceeds its available limits.");
        var selections = (modelIndex < 0 ? loadout.Equipment : loadout.Models[modelIndex].Equipment).ToList();
        if (group.Maximum.At(entry.Models) == 1 && count > 0) selections.RemoveAll(s => s.GroupId == groupId);
        else selections.RemoveAll(s => s.GroupId == groupId && s.OptionId == optionId);
        if (count > 0) selections.Add(new(groupId, optionId, count));
        var next = modelIndex < 0 ? loadout with { Equipment = selections }
            : loadout with { Models = loadout.Models.Select((m, i) => i == modelIndex ? m with { Equipment = selections } : m).ToList() };
        var total = selections.Where(s => s.GroupId == groupId).Sum(s => s.Count);
        if (total < group.Minimum.At(entry.Models) || total > group.Maximum.At(entry.Models)) throw new InvalidDataException($"{group.Name}: select {group.Minimum.At(entry.Models)}–{group.Maximum.At(entry.Models)} options.");
        if (option.UnitMaximum != null)
        {
            var beforeCount = Equipment(unit, loadout).Where(e => e.Option.Id == optionId).Sum(e => e.Count);
            var afterCount = Equipment(unit, next).Where(e => e.Option.Id == optionId).Sum(e => e.Count);
            if (afterCount > option.UnitMaximum.At(entry.Models) && afterCount >= beforeCount) throw new InvalidDataException($"{option.Name}: at most {option.UnitMaximum.At(entry.Models)} in this unit.");
        }
        var issues = Issues(unit, entry with { Loadout = next });
        // Permit repairs to previously invalid loadouts, while rejecting newly introduced violations.
        var previous = Issues(unit, entry with { Loadout = loadout });
        var added = issues.Except(previous).ToList();
        if (added.Count > 0) throw new InvalidDataException(added[0]);
        return next;
    }

    public static List<string> Issues(UnitDefinition unit, ArmyEntry entry)
    {
        var loadout = entry.Loadout;
        if (loadout == null) return ["Choose a loadout before playing this unit."];
        var definition = Definition(unit, loadout); List<string> issues = [];
        if (!loadout.Manual && unit.Profile?.Loadout == null) return ["Loadout options changed. Reset this unit's loadout to use the current datasheet."];
        if (loadout.Manual && unit.ModelCosts?.Any(c => c.Points != 0) == true)
            return ["Model pricing requires per-model choices. Reset this unit's loadout to use the current datasheet."];
        if (loadout.Manual && unit.WargearCosts != null || definition.Incomplete) issues.Add(ReviewWarning);
        if (!loadout.Manual && definition.Roles.Count > 0 && loadout.Models.Count != entry.Models) issues.Add("Model loadout count does not match the unit size.");
        if (definition.Composition != null) issues.AddRange(SelectionCompositions.Errors(definition.Composition, loadout));
        foreach (var role in definition.Roles)
        {
            var count = loadout.Models.Count(m => m.RoleId == role.Id);
            if (count < role.Minimum.At(entry.Models, loadout.Models) || count > role.Maximum.At(entry.Models, loadout.Models)) issues.Add($"{role.Name}: model count is outside the available composition.");
        }
        foreach (var group in definition.ModelGroups ?? [])
        {
            var count = loadout.Models.Count(m => group.RoleIds.Contains(m.RoleId));
            if (count < group.Minimum.At(entry.Models, loadout.Models) || count > group.Maximum.At(entry.Models, loadout.Models)) issues.Add($"{group.Name}: model count is outside the available composition.");
        }
        var totals = new Dictionary<string, int>();
        void Check(List<EquipmentGroup> groups, List<EquipmentSelection> selections)
        {
            foreach (var selection in selections)
                if (selection.Count <= 0 || !groups.Any(g => g.Id == selection.GroupId && g.Options.Any(o => o.Id == selection.OptionId))) issues.Add("Saved equipment is no longer available. Reset the loadout or choose current options.");
            if (selections.GroupBy(s => (s.GroupId, s.OptionId)).Any(g => g.Count() > 1)) issues.Add("Duplicate equipment selections must be corrected.");
            foreach (var group in groups)
            {
                var count = selections.Where(s => s.GroupId == group.Id).Sum(s => s.Count);
                if (count < group.Minimum.At(entry.Models, loadout.Models) || count > group.Maximum.At(entry.Models, loadout.Models)) issues.Add($"{group.Name}: select {group.Minimum.At(entry.Models, loadout.Models)}–{group.Maximum.At(entry.Models, loadout.Models)} options.");
                foreach (var option in group.Options)
                {
                    var n = selections.Where(s => s.GroupId == group.Id && s.OptionId == option.Id).Sum(s => s.Count);
                    if (n < option.Minimum.At(entry.Models) || n > option.Maximum.At(entry.Models)) issues.Add($"{option.Name}: quantity exceeds its available limits.");
                    totals[option.Id] = totals.GetValueOrDefault(option.Id) + n;
                }
            }
        }
        Check(definition.UnitGroups, loadout.Equipment);
        foreach (var model in loadout.Models)
        {
            var role = definition.Roles.FirstOrDefault(r => r.Id == model.RoleId);
            if (role == null) issues.Add("A saved model is no longer available. Reset the loadout.");
            else Check(role.Groups, model.Equipment);
        }
        foreach (var option in definition.UnitGroups.Concat(definition.Roles.SelectMany(r => r.Groups)).SelectMany(g => g.Options))
            if (option.UnitMaximum != null && totals.GetValueOrDefault(option.Id) > option.UnitMaximum.At(entry.Models)) issues.Add($"{option.Name}: at most {option.UnitMaximum.At(entry.Models)} in this unit.");
        return issues.Distinct().ToList();
    }

    public static List<(EquipmentOption Option, int Count)> Equipment(UnitDefinition unit, UnitLoadout? loadout)
    {
        if (loadout == null) return [];
        var definition = Definition(unit, loadout); List<(EquipmentOption, int)> result = [];
        void Add(List<EquipmentGroup> groups, List<EquipmentSelection> selections)
        {
            foreach (var selection in selections.Where(s => s.Count > 0))
            {
                var option = groups.FirstOrDefault(g => g.Id == selection.GroupId)?.Options.FirstOrDefault(o => o.Id == selection.OptionId);
                if (option != null) result.Add((option, selection.Count));
            }
        }
        Add(definition.UnitGroups, loadout.Equipment);
        foreach (var model in loadout.Models)
            Add(definition.Roles.FirstOrDefault(r => r.Id == model.RoleId)?.Groups ?? [], model.Equipment);
        return result;
    }

    public static List<EquippedWeapon> Weapons(UnitDefinition unit, UnitLoadout? loadout)
    {
        var equipment = Equipment(unit, loadout);
        return (unit.Profile?.Weapons ?? []).Select(w => new EquippedWeapon(w, w.GrantedBy != null ? 1
            : equipment.Where(e => e.Option.Weapons.Contains(w.Name)).Sum(e => e.Count))).Where(w => w.Count > 0).ToList();
    }

    public static List<AbilityProfile> Abilities(UnitDefinition unit, UnitLoadout? loadout)
    {
        var selected = Equipment(unit, loadout).SelectMany(e => e.Option.Abilities).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywords = Weapons(unit, loadout).SelectMany(w => w.Profile.Keywords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        return (unit.Profile?.Abilities ?? []).Where(a => a.Section != "Equipment rules" || selected.Contains(a.Name)
            || keywords.Any(k => k.Equals(a.Name, StringComparison.OrdinalIgnoreCase) || k.StartsWith(a.Name + " ", StringComparison.OrdinalIgnoreCase))).ToList();
    }
}

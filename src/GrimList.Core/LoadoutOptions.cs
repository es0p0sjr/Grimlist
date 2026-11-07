namespace GrimList.Core;

public record LoadoutSection(string Id, string Name, List<string> RoleIds, string DefaultRoleId);

public static partial class Loadouts
{
    public static List<LoadoutSection> Sections(UnitDefinition unit)
    {
        var definition = Definition(unit); var assigned = new HashSet<string>(); List<LoadoutSection> sections = [];
        var pools = (definition.ModelGroups ?? []).Where(g => g.RoleIds.Count > 1).OrderByDescending(g => g.RoleIds.Count).ToList();
        foreach (var role in definition.Roles)
        {
            if (assigned.Contains(role.Id)) continue;
            var pool = pools.FirstOrDefault(g => g.RoleIds.Contains(role.Id) && !g.RoleIds.Any(assigned.Contains));
            var ids = pool?.RoleIds ?? [role.Id];
            var members = definition.Roles.Where(r => ids.Contains(r.Id)).ToList();
            var standard = members.OrderByDescending(r => r.Default).ThenByDescending(r => r.Maximum.Value).First();
            assigned.UnionWith(ids);
            sections.Add(new("roles:" + string.Join("/", ids), pool?.Name ?? role.Name, ids, standard.Id));
        }
        return sections;
    }

    public static int RoleCount(UnitLoadout loadout, string roleId) => loadout.Models.Count(m => m.RoleId == roleId);
    public static int OptionCount(UnitLoadout loadout, string roleId, string groupId, string optionId)
        => loadout.Models.Where(m => m.RoleId == roleId).SelectMany(m => m.Equipment).Where(e => e.GroupId == groupId && e.OptionId == optionId).Sum(e => e.Count);

    // A counter changes a complete equipment bundle. Standard models supply replacements;
    // other selected upgrades are never silently consumed to make room.
    public static UnitLoadout SetRoleCount(UnitDefinition unit, ArmyEntry entry, string sectionId, string roleId, int count)
    {
        if (count < 0 || count > entry.Models) throw new InvalidDataException("Equipment count is outside this unit's size.");
        var section = Sections(unit).Single(s => s.Id == sectionId);
        if (!section.RoleIds.Contains(roleId) || roleId == section.DefaultRoleId) throw new InvalidDataException("Standard equipment is calculated from the remaining models.");
        var definition = Definition(unit);
        if (definition.Composition != null)
        {
            var before = entry.Loadout ?? Create(unit, entry.Models);
            var after = SelectionCompositions.Select(unit, before, entry.Models, roleId, count);
            ValidateAggregateChange(unit, entry, before, after);
            return after;
        }
        var target = definition.Roles.Single(r => r.Id == roleId);
        var proposed = (entry.Loadout ?? Create(unit, entry.Models)).Models;
        if (count < target.Minimum.At(entry.Models, proposed) || count > target.Maximum.At(entry.Models, proposed))
            throw new InvalidDataException($"{target.Name}: maximum {target.Maximum.At(entry.Models, proposed)} in this unit.");
        var original = entry.Loadout ?? Create(unit, entry.Models);
        var current = original;
        while (RoleCount(current, roleId) != count)
        {
            var increasing = RoleCount(current, roleId) < count;
            var from = increasing ? section.DefaultRoleId : roleId;
            var to = definition.Roles.Single(r => r.Id == (increasing ? roleId : section.DefaultRoleId));
            var index = current.Models.FindLastIndex(m => m.RoleId == from);
            if (index < 0) throw new InvalidDataException("No models with standard equipment remain. Remove another option first.");
            var equipment = Defaults(to.Groups, entry.Models);
            // Retain compatible choices when switching a model's weapon bundle.
            foreach (var group in to.Groups)
            {
                var previous = current.Models[index].Equipment.Where(e => e.GroupId == group.Id).ToList();
                if (previous.Count == 0 || previous.Any(e => !group.Options.Any(o => o.Id == e.OptionId))) continue;
                equipment.RemoveAll(e => e.GroupId == group.Id); equipment.AddRange(previous);
            }
            current = current with { Models = current.Models.Select((m, i) => i == index ? new ModelLoadout(to.Id, equipment) : m).ToList() };
        }
        ValidateAggregateChange(unit, entry, original, current);
        return current;
    }

    public static string DefaultOption(EquipmentGroup group, int models)
        => Defaults([group], models).FirstOrDefault()?.OptionId ?? "";

    public static UnitLoadout SetOptionCount(UnitDefinition unit, ArmyEntry entry, string roleId, string groupId, string optionId, int count)
    {
        if (count < 0) throw new InvalidDataException("Equipment quantities cannot be negative.");
        var definition = Definition(unit);
        var group = definition.Roles.Single(r => r.Id == roleId).Groups.Single(g => g.Id == groupId);
        var current = entry.Loadout ?? Create(unit, entry.Models);
        var slots = current.Models.Select((m, i) => (Model: m, Index: i)).Where(s => s.Model.RoleId == roleId).Select(s => s.Index).ToList();
        var option = group.Options.Single(o => o.Id == optionId);
        if (count > slots.Count * option.Maximum.At(entry.Models)) throw new InvalidDataException("Equipment count exceeds the available models.");
        var standard = DefaultOption(group, entry.Models);
        if (group.Maximum.At(entry.Models) == 1 && optionId == standard)
            throw new InvalidDataException("Standard equipment is calculated from the remaining models.");
        while (OptionCount(current, roleId, groupId, optionId) != count)
        {
            var increasing = OptionCount(current, roleId, groupId, optionId) < count;
            var exclusive = group.Maximum.At(entry.Models) == 1;
            UnitLoadout? changed = null;
            foreach (var index in slots)
            {
                var selected = current.Models[index].Equipment.Where(e => e.GroupId == groupId).ToList();
                var n = selected.Where(e => e.OptionId == optionId).Sum(e => e.Count);
                if (increasing && exclusive && selected.Any(e => e.OptionId != standard)) continue;
                if (!increasing && n == 0) continue;
                try
                {
                    // Removing a mandatory replacement restores the standard weapon in the same action.
                    changed = !increasing && exclusive && group.Minimum.At(entry.Models) > 0
                        ? Choose(unit, entry with { Loadout = current }, index, groupId, standard, 1)
                        : Choose(unit, entry with { Loadout = current }, index, groupId, optionId, n + (increasing ? 1 : -1));
                    break;
                }
                catch (InvalidDataException) { }
            }
            if (changed == null) throw new InvalidDataException($"{option.Name}: limit reached or no eligible models remain.");
            current = changed;
        }
        return current;
    }

    private static void ValidateAggregateChange(UnitDefinition unit, ArmyEntry entry, UnitLoadout before, UnitLoadout after)
    {
        var definition = Definition(unit);
        foreach (var role in definition.Roles)
        {
            var previous = RoleCount(before, role.Id); var next = RoleCount(after, role.Id);
            if (next > role.Maximum.At(entry.Models, after.Models) && next > previous || next < role.Minimum.At(entry.Models, after.Models) && next < previous)
                throw new InvalidDataException($"{role.Name}: quantity exceeds its available limits.");
        }
        foreach (var group in definition.ModelGroups ?? [])
        {
            var previous = before.Models.Count(m => group.RoleIds.Contains(m.RoleId));
            var next = after.Models.Count(m => group.RoleIds.Contains(m.RoleId));
            if (next > group.Maximum.At(entry.Models, after.Models) && next > previous || next < group.Minimum.At(entry.Models, after.Models) && next < previous)
                throw new InvalidDataException($"{group.Name}: maximum {group.Maximum.At(entry.Models, after.Models)} combined.");
        }
        var oldEquipment = Equipment(unit, before); var newEquipment = Equipment(unit, after);
        foreach (var option in newEquipment.Select(e => e.Option).Where(o => o.UnitMaximum != null))
        {
            var previous = oldEquipment.Where(e => e.Option.Id == option.Id).Sum(e => e.Count);
            var next = newEquipment.Where(e => e.Option.Id == option.Id).Sum(e => e.Count);
            if (next > option.UnitMaximum!.At(entry.Models, after.Models) && next > previous)
                throw new InvalidDataException($"{option.Name}: maximum {option.UnitMaximum.At(entry.Models, after.Models)} in this unit.");
        }
        var added = Issues(unit, entry with { Loadout = after }).Except(Issues(unit, entry with { Loadout = before })).ToList();
        if (added.Count > 0) throw new InvalidDataException(added[0]);
    }
}

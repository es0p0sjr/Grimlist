using System.Globalization;
using System.Text.RegularExpressions;

namespace GrimList.Core;

// Imported semantics contain no evaluated roster state. All projections allocate new collections.
public record RosterChoice(string GroupId, string OptionId);
public record DatasheetChoiceOption(string Id, string Name, List<string> Keywords, List<AbilityProfile> Abilities, EffectCondition? Available = null);
public record DatasheetChoiceGroup(string Id, string Name, int Minimum, List<DatasheetChoiceOption> Options, EffectCondition? Available = null);
public record ScopedAbility(AbilityProfile Ability, EffectCondition Condition, List<DatasheetModifier>? Modifiers = null);
public record AttachmentPermission(string Id, EffectCondition Condition);
public record DatasheetSemantics(List<DatasheetChoiceGroup> Choices, List<DatasheetModifier> Modifiers, int Version = 1,
    List<ScopedAbility>? Abilities = null, List<AttachmentPermission>? Attachments = null);
public record EffectCondition(string Kind, string Value = "", string Comparison = "atLeast", int Count = 1,
    List<EffectCondition>? Children = null, string Label = "");
// Conditions belong to the granting entry; predicates belong to the receiving entry.
// Never flatten an attached recipient into a self ability, even when both share a unit.
public record EffectRecipient(string Scope, EffectCondition? Predicate = null, string SourceSelector = "");
public record DatasheetModifier(string Id, string Source, string Target, string Characteristic, string Operation, string Value,
    string Profile = "", string WeaponType = "", EffectCondition? Condition = null, string Replaces = "",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WeaponPredicate? WeaponPredicate = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EffectRecipient? Recipient = null);
public record AppliedModifierSource(string Id, string Source, string Target, string Characteristic, string BaseValue, string EffectiveValue,
    string Condition = "", bool Conditional = false)
{
    public string Display => $"{Target} — {Characteristic}: {EffectiveValue}\nSource: {Source}"
        + (Conditional ? "\nConditional: " + Condition : Condition.Length > 0 ? "\n" + Condition : "");
}
public record EffectiveUnitState(UnitDefinition BaseUnit, UnitDefinition Unit, List<AppliedModifierSource> AppliedModifierSources)
{
    public List<string> EffectiveKeywords => Unit.Profile?.Keywords ?? [];
    public Dictionary<string, string> EffectiveCharacteristics => Unit.Profile?.Characteristics ?? [];
    public List<WeaponProfile> EffectiveWeaponProfiles => Unit.Profile?.Weapons ?? [];
    public List<AbilityProfile> EffectiveAbilities => Unit.Profile?.Abilities ?? [];
    public string Summary => string.Join("\n\n", AppliedModifierSources.Select(s => s.Display));
}

public static partial class EffectiveDatasheets
{
    public static ArmyEntry NormalizeChoices(ArmyEntry entry, UnitDefinition unit)
    {
        var groups = (unit.Profile?.Semantics?.Choices ?? []).Select(g => g.Id).ToHashSet();
        if (groups.Count == 0 || entry.Loadout == null) return entry;
        var oldChoices = entry.Loadout.Equipment.Concat(entry.Loadout.Models.SelectMany(m => m.Equipment)).Where(s => groups.Contains(s.GroupId) && s.Count > 0)
            .Select(s => new RosterChoice(s.GroupId, s.OptionId)).Distinct().ToList();
        if (oldChoices.Count == 0) return entry;
        return entry with { Choices = entry.Choices ?? oldChoices, Loadout = entry.Loadout with {
            Equipment = entry.Loadout.Equipment.Where(s => !groups.Contains(s.GroupId)).ToList(),
            Models = entry.Loadout.Models.Select(m => m with { Equipment = m.Equipment.Where(s => !groups.Contains(s.GroupId)).ToList() }).ToList() } };
    }
    public static string Key(string value) => UnitIdentity.Normalize(value.StartsWith("Faction:", StringComparison.OrdinalIgnoreCase) ? value[8..] : value);
    public static bool MatchesKeywords(IEnumerable<string> keywords, string predicate)
    {
        var keys = keywords.Select(Key).Where(k => k.Length > 0).Distinct().ToList();
        return Regex.Split(predicate, @"\s+or\s+|\s*/\s*", RegexOptions.IgnoreCase).Any(branch =>
        {
            var value = Key(Regex.Replace(branch, @"\b(?:and|models?|units?)\b", "", RegexOptions.IgnoreCase));
            var seen = new Dictionary<string, bool>();
            bool Match(string rest) => rest.Length == 0 || (seen.TryGetValue(rest, out var match) ? match
                : seen[rest] = keys.Any(k => rest.StartsWith(k, StringComparison.Ordinal) && Match(rest[k.Length..])));
            return value.Length > 0 && Match(value);
        });
    }
    public static bool? Test(EffectCondition? test, ArmyList army, ArmyEntry entry, UnitDefinition unit)
    {
        if (test == null) return true;
        evaluation?.Observe(test);
        var children = test.Children ?? [];
        if (test.Kind == "not") return children.Count == 1 ? !Test(children[0], army, entry, unit) : null;
        if (test.Kind is "and" or "or")
        {
            var values = children.Select(c => Test(c, army, entry, unit)).ToList();
            return test.Kind == "and" ? values.Contains(false) ? false : values.Contains(null) ? null : true
                : values.Contains(true) ? true : values.Contains(null) ? null : false;
        }
        int? actual = test.Kind switch
        {
            "constant" => int.TryParse(test.Value, out var number) ? number : null,
            "keyword" => MatchesKeywords((unit.Profile?.Keywords ?? []).Append(unit.Category), test.Value) ? 1 : 0,
            "selection" => SelectionCount(test.Value, army, entry, unit),
            "detachment" => DetachmentRules.Contains(army, test.Value, evaluation?.Dataset) ? 1 : 0,
            "enhancement" => entry.Enhancement != null && Key(entry.Enhancement.Name) == Key(test.Value)
                && DetachmentRules.Contains(army, entry.Enhancement.Detachment, evaluation?.Dataset) && (evaluation?.ActiveEnhancements.Contains(entry.Id) ?? true) ? 1 : 0,
            "models" => entry.Models,
            "association" or "attached-keyword" or "attached-enhancement" or "attached-models" or "roster-keyword" or "roster-unit" or "catalogue" => evaluation?.Count(test, entry, unit),
            "unit" => (unit.Aliases ?? []).Any(a => a.EndsWith(":" + test.Value, StringComparison.Ordinal)) ? 1 : 0,
            _ => null
        };
        if (actual == null) return null;
        return test.Comparison switch { "atLeast" or "instanceOf" => actual >= test.Count, "atMost" => actual <= test.Count,
            "equalTo" => actual == test.Count, "notEqualTo" or "notInstanceOf" => actual != test.Count,
            "greaterThan" => actual > test.Count, "lessThan" => actual < test.Count, _ => null };
    }
    private static int SelectionCount(string id, ArmyList army, ArmyEntry entry, UnitDefinition unit)
    {
        var explicitCount = (entry.Choices ?? []).Count(c => c.OptionId == id) + (entry.Loadout?.Equipment ?? [])
            .Concat((entry.Loadout?.Models ?? []).SelectMany(m => m.Equipment)).Where(e => e.OptionId == id).Sum(e => e.Count);
        // DetachmentPrices records only source-proven mandatory upgrades (min/max 1
        // when the detachment is active). Their exact source IDs also own conditional
        // abilities, including legacy recovered semantics. They need no saved picker
        // selection. Do not infer a grant from a name, points amount or mere visibility.
        return MandatorySelection(id, army, unit) ? Math.Max(1, explicitCount) : explicitCount;
    }
    private static bool MandatorySelection(string id, ArmyList army, UnitDefinition unit) => id.Length > 0
        && (unit.DetachmentCosts ?? []).Any(c => c.SourceId == id && DetachmentRules.Contains(army, c.Detachment, evaluation?.Dataset));
    private static bool MandatoryAbility(EffectCondition condition, ArmyList army, UnitDefinition unit)
        => condition.Kind == "selection" && condition.Comparison is "atLeast" or "equalTo" && condition.Count == 1
            && MandatorySelection(condition.Value, army, unit)
            || condition.Kind == "and" && (condition.Children ?? []).Any(c => MandatoryAbility(c, army, unit));
    public static List<DatasheetChoiceGroup> Choices(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        var unit = MusterEffects.MusterUnit(army, entry, data);
        return unit == null ? [] : (unit.Profile?.Semantics?.Choices ?? []).Where(g => Test(g.Available, army, entry, unit) == true)
            .Select(g => g with { Options = g.Options.Where(o => Test(o.Available, army, entry, unit) == true).ToList() }).ToList();
    }
    public static List<string> ChoiceIssues(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        if (data?.FindUnit(entry.UnitKey) is { } original) entry = NormalizeChoices(entry, original);
        var groups = Choices(army, entry, data); List<string> issues = [];
        foreach (var group in groups)
        {
            var selected = (entry.Choices ?? []).Where(c => c.GroupId == group.Id).ToList();
            if (selected.Count < group.Minimum) issues.Add($"{entry.Name}: choose {group.Name}.");
            if (selected.Count > 1 || selected.Any(s => !group.Options.Any(o => o.Id == s.OptionId))) issues.Add($"{entry.Name}: invalid {group.Name} selection.");
        }
        if ((entry.Choices ?? []).Any(c => groups.All(g => g.Id != c.GroupId))) issues.Add($"{entry.Name}: a roster choice is no longer available.");
        return issues;
    }
    private static EffectiveUnitState? ProjectCore(ArmyList army, ArmyEntry entry, Dataset? data, bool includeEnhancement = true)
    {
        var original = data?.FindUnit(entry.UnitKey);
        var muster = MusterEffects.MusterUnit(army, entry, data);
        if (original == null || muster == null) return null;
        if (muster.Profile == null) return new(original, muster, []);
        entry = NormalizeChoices(entry, original);
        if (entry.Loadout == null && muster.Profile.Loadout != null)
        {
            var defaults = Loadouts.Create(muster, entry.Models);
            var choiceGroups = (muster.Profile.Semantics?.Choices ?? []).Select(g => g.Id).ToHashSet();
            entry = entry with { Loadout = defaults with {
                Equipment = defaults.Equipment.Where(s => !choiceGroups.Contains(s.GroupId)).ToList(),
                Models = defaults.Models.Select(m => m with { Equipment = m.Equipment.Where(s => !choiceGroups.Contains(s.GroupId)).ToList() }).ToList() } };
        }
        var p = muster.Profile ?? new();
        var keys = (p.Keywords ?? []).ToList();
        var stats = new Dictionary<string, string>(p.Characteristics ?? []);
        var models = (p.Models ?? []).Select(m => m with { Characteristics = new(m.Characteristics) }).ToList();
        var weapons = (p.Weapons ?? []).ToList();
        var abilities = Loadouts.Abilities(muster, entry.Loadout).ToList();
        var scopedAbilities = p.Semantics?.Abilities ?? [];
        abilities.RemoveAll(a => scopedAbilities.Any(s => s.Ability.Name == a.Name && s.Ability.Text == a.Text));
        List<AppliedModifierSource> applied = [];
        var optionalAbilities = (p.Semantics?.Choices ?? []).SelectMany(g => g.Options).SelectMany(o => o.Abilities).Select(a => a.Name).ToHashSet();
        abilities.RemoveAll(a => optionalAbilities.Contains(a.Name));
        UnitDefinition Current() => muster with { Category = UnitCategories.FromKeywords(keys), Profile = p with {
            Characteristics = stats, Models = models, Weapons = weapons, Keywords = keys, Abilities = abilities,
            Warnings = p.Warnings?.Select(w => w.Replace("Base datasheet shown; conditional profile or visibility changes are not applied.",
                "Unresolved source conditions remain contextual; consult the rule text.")).ToList(),
            Notes = p.Notes.Replace("Base datasheet shown; conditional profile or visibility changes are not applied.",
                "Unresolved source conditions remain contextual; consult the rule text.") } };
        List<RosterChoice> validChoices = [];
        foreach (var group in Choices(army, entry, data))
        {
            var selections = (entry.Choices ?? []).Where(c => c.GroupId == group.Id).ToList();
            if (selections.Count != 1) continue;
            var option = group.Options.FirstOrDefault(o => o.Id == selections[0].OptionId);
            if (option == null) continue;
            validChoices.Add(selections[0]);
            foreach (var key in option.Keywords.Where(k => !keys.Any(x => Key(x) == Key(k))))
            {
                keys.Add(key);
                applied.Add(new(option.Id + "/" + key, option.Name + " — " + group.Name, "Keywords", key, "—", key));
            }
            abilities.AddRange(option.Abilities);
        }
        // Invalid/stale selections are diagnostics, never a source of active buffs.
        entry = entry with { Choices = validChoices };
        var seen = new HashSet<(string Source, string Id)>();
        var removedAbilities = new HashSet<string>();
        List<(DatasheetModifier Modifier, bool Conditional, string Condition)> weaponKeywords = [];
        void Apply(DatasheetModifier modifier)
        {
            // Routed grants are applied only after resolving source and recipient in
            // the legal attachment graph, never directly on the owning datasheet.
            if (modifier.Recipient != null) return;
            if (evaluation?.Failed == true && modifier.Condition != null) return;
            if (!seen.Add((modifier.Source, modifier.Id))) return;
            var active = Test(modifier.Condition, army, entry, evaluation?.ConditionUnit(entry, Current()) ?? Current());
            if (active == false) return;
            if (modifier.Target == "weapon" && modifier.WeaponPredicate is { Kind: "unknown" } selector
                && Stat(modifier.Characteristic) is "M" or "T" or "Sv" or "W" or "LD" or "OC" or "InSv")
            {
                applied.Add(new(modifier.Id, modifier.Source, "Model", modifier.Characteristic, "Unresolved", "Not applied",
                    "Unsupported model recipient selector: " + selector.Value, true));
                return;
            }
            var conditional = active == null;
            var condition = modifier.Condition?.Label ?? "";
            if (conditional && condition.Length == 0) condition = "Source condition cannot be determined from roster state";
            if (modifier.Target == "weapon" && Stat(modifier.Characteristic) == "Keywords")
            {
                weaponKeywords.Add((modifier, conditional, condition));
                return;
            }
            string Change(string before, string target, string field)
            {
                var after = Modify(before, modifier.Operation, modifier.Value);
                if (after != before) applied.Add(new(modifier.Id, modifier.Source, target, field, before, after, condition, conditional));
                return conditional ? before : after;
            }
            if (modifier.Target == "model")
            {
                void Adjust(Dictionary<string, string> values, string target)
                {
                    var name = values.Keys.FirstOrDefault(k => Stat(k) == Stat(modifier.Characteristic));
                    if (name != null) values[name] = Change(values[name], target, name);
                    else if (modifier.Operation == "set" && Stat(modifier.Characteristic) == "InSv") values["InSv"] = Change("—", target, "InSv");
                }
                if (modifier.Profile.Length == 0 || models.Count <= 1 && (models.Any(m => m.Name == modifier.Profile) || models.Count == 0 && modifier.Profile == muster.Name)) Adjust(stats, "Model");
                foreach (var model in models.Where(m => modifier.Profile.Length == 0 || m.Name == modifier.Profile)) Adjust(model.Characteristics, model.Name);
            }
            else if (modifier.Target == "weapon")
            {
                for (var i = 0; i < weapons.Count; i++)
                {
                    var w = weapons[i];
                    var match = WeaponRules.Matches(modifier, w);
                    if (match == null)
                    {
                        var before = Stat(modifier.Characteristic) switch {
                            "A" => w.Attacks, "S" => w.Strength, "AP" => w.Ap, "D" => w.Damage, "Range" => w.Range,
                            "BS" when w.Type == "Ranged" => w.Skill, "WS" when w.Type == "Melee" => w.Skill, _ => null };
                        if (before != null) applied.Add(new(modifier.Id, modifier.Source, w.Name, Stat(modifier.Characteristic), before,
                            modifier.Operation + " " + modifier.Value,
                            string.Join("; ", new[] { condition, "Unresolved weapon selector: " + DataJson.Write(modifier.WeaponPredicate) }.Where(s => s.Length > 0)), true));
                    }
                    if (match != true) continue;
                    weapons[i] = Stat(modifier.Characteristic) switch {
                        "A" => w with { Attacks = Change(w.Attacks, w.Name, "A") }, "S" => w with { Strength = Change(w.Strength, w.Name, "S") },
                        "AP" => w with { Ap = Change(w.Ap, w.Name, "AP") }, "D" => w with { Damage = Change(w.Damage, w.Name, "D") },
                        "Range" => w with { Range = Change(w.Range, w.Name, "Range") },
                        "BS" when w.Type == "Ranged" => w with { Skill = Change(w.Skill, w.Name, "BS") },
                        "WS" when w.Type == "Melee" => w with { Skill = Change(w.Skill, w.Name, "WS") },
                        "Keywords" => w with { Keywords = Change(w.Keywords, w.Name, "Keywords") }, _ => w };
                }
            }
            else if (modifier.Target is "keyword" or "ability")
            {
                var exists = modifier.Target == "keyword" ? keys.Any(k => Key(k) == Key(modifier.Value)) : abilities.Any(a => Key(a.Name) == Key(modifier.Value));
                var remove = modifier.Operation == "remove";
                if (modifier.Target == "ability" && remove && !conditional) removedAbilities.Add(Key(modifier.Value));
                if (exists == remove)
                {
                    applied.Add(new(modifier.Id, modifier.Source, modifier.Target, modifier.Value, exists ? modifier.Value : "—", remove ? "—" : modifier.Value, condition, conditional));
                    if (!conditional)
                        if (modifier.Target == "keyword") { keys.RemoveAll(k => Key(k) == Key(modifier.Value)); if (!remove) keys.Add(modifier.Value); }
                        else { abilities.RemoveAll(a => Key(a.Name) == Key(modifier.Value)); if (!remove) abilities.Add(new(modifier.Value, modifier.Characteristic.Length > 0 ? modifier.Characteristic : "Granted by " + modifier.Source, "Effective abilities")); }
                }
            }
        }
        var enhancement = includeEnhancement ? Enhancements.Find(army, data, entry.Enhancement) : null;
        if (enhancement != null && !Enhancements.Eligible(army, ProjectCore(army, entry, data, false)?.Unit, enhancement, data)) enhancement = null;
        if (enhancement != null)
            foreach (var weapon in EnhancementWeapons.Profiles(enhancement))
            {
                weapons.Add(weapon with { GrantedBy = enhancement.Name });
                applied.Add(new(weapon.SourceId ?? enhancement.Name, enhancement.Name, weapon.Name, "Equipped", "—", "1"));
            }
        foreach (var modifier in p.Semantics?.Modifiers ?? []) Apply(ModelStatModifiers.Normalize(modifier));
        foreach (var modifier in RosterWeaponModifiers(army, entry, data, Current())) Apply(modifier);
        if (enhancement != null)
        {
            var modifiers = CompiledRuleText.ForCard(enhancement);
            foreach (var modifier in modifiers) Apply(modifier);
        }
        foreach (var scoped in scopedAbilities)
            if (evaluation?.Failed != true && Test(scoped.Condition, army, entry, evaluation?.ConditionUnit(entry, Current()) ?? Current()) == true
                && !abilities.Contains(scoped.Ability))
            {
                var ability = scoped.Ability;
                foreach (var change in scoped.Modifiers ?? [])
                    if (Test(change.Condition, army, entry, evaluation?.ConditionUnit(entry, Current()) ?? Current()) == true)
                    {
                        string Changed(string value) => change.Operation == "set" ? change.Value : value + change.Value;
                        ability = change.Characteristic == "Name" ? ability with { Name = Changed(ability.Name) } : ability with { Text = Changed(ability.Text) };
                    }
                // Mandatory detachment upgrades are not editable equipment. Preserve
                // their active abilities through the play/export loadout filter too.
                if (ability.Section == "Equipment rules" && MandatoryAbility(scoped.Condition, army, muster))
                    ability = ability with { Section = "Detachment abilities" };
                abilities.Add(ability);
            }
        foreach (var modifier in evaluation?.AttachedModifiers(entry) ?? []) Apply(modifier);
        abilities.RemoveAll(a => removedAbilities.Contains(Key(a.Name)));
        // Self is in range; recipient predicates and explicit exclusions still apply.
        foreach (var ability in abilities.ToList().DistinctBy(a => Key(a.Name)))
        {
            foreach (var modifier in DatasheetRuleText.SelfAura(ability, keys)) Apply(modifier);
            foreach (var modifier in CompiledRuleText.Weapons(ability)) Apply(modifier);
        }
        weapons = WeaponRules.ApplyKeywords(weapons, weaponKeywords, applied);
        return new(original, Current(), applied.DistinctBy(a => (a.Source, a.Id, a.Target, a.Characteristic, a.BaseValue, a.EffectiveValue, a.Conditional)).ToList());
    }
    public static string Stat(string name) => Key(name) switch { "MOVE" or "MOVEMENT" or "M" => "M", "TOUGHNESS" or "T" => "T", "SAVE" or "SV" => "Sv",
        "WOUNDS" or "W" => "W", "LEADERSHIP" or "LD" => "LD", "OBJECTIVECONTROL" or "OC" => "OC",
        "ATTACKS" or "A" => "A", "STRENGTH" or "S" => "S", "ARMOURPENETRATION" or "AP" => "AP", "DAMAGE" or "D" => "D",
        "BALLISTICSKILL" or "BS" => "BS", "WEAPONSKILL" or "WS" => "WS", "RANGE" => "Range", "KEYWORDS" => "Keywords",
        "INSV" or "INVULNERABLESAVE" => "InSv", _ => name };
    internal static string Modify(string original, string operation, string value)
    {
        if (operation == "set") return value;
        if (operation is "append" or "add") return string.Join(", ", new[] { original, value }.Where(s => s.Length > 0).Distinct());
        var number = Regex.Match(original.Trim(), @"^(?<n>-?\d+)(?<suffix>[+""″]?)$");
        if (!number.Success || !int.TryParse(value.Trim('"', '″', '+'), out var amount))
        {
            // Preserve dice expressions; never turn D6+1 into a deterministic number.
            if (Regex.IsMatch(original, @"^\d*D\d+(?:[+-]\d+)?$", RegexOptions.IgnoreCase) && int.TryParse(value, out amount) && operation is "increment" or "decrement")
                return original + (operation == "increment" ? "+" : "-") + amount;
            return original;
        }
        var before = int.Parse(number.Groups["n"].Value, CultureInfo.InvariantCulture);
        var after = operation switch { "increment" => before + amount, "decrement" => before - amount, "multiply" => before * amount, _ => before };
        return after.ToString(CultureInfo.InvariantCulture) + number.Groups["suffix"].Value;
    }
}

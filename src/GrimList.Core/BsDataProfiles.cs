using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static List<string> Keywords(JsonObject node, Dictionary<string, JsonObject> registry)
        => Children(node, "categoryLinks").Where(c => !Flag(c, "hidden"))
            .Select(c => Text(c, "name") is { Length: > 0 } name ? name
                : registry.TryGetValue(Text(c, "targetId"), out var category) ? Text(category, "name") : "")
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static UnitProfile? ReadDatasheet(JsonObject root, Dictionary<string, JsonObject> registry, List<string> keywords, out List<string> warnings)
    {
        var problems = new HashSet<string>();
        var models = new List<ModelProfile>(); var weapons = new List<WeaponProfile>(); var abilities = new List<AbilityProfile>();
        var profileKeys = new HashSet<string>(); var path = new SelectionPath();
        void AddProfile(JsonObject definition, string section, List<string> categories)
        {
            var node = BaseValues(definition, problems);
            if (Flag(node, "hidden")) return;
            var values = Children(node, "characteristics").GroupBy(c => Text(c, "name"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Clean(Text(g.Last(), "$text")), StringComparer.OrdinalIgnoreCase);
            var type = Text(node, "typeName"); var name = Clean(Text(node, "name"));
            var key = type + "|" + name + "|" + DataJson.Write(values);
            if (!profileKeys.Add(key)) return;
            string Value(params string[] names) => names.Select(n => values.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "—";
            if (type.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            {
                if (values.Count > 0) models.Add(new(name, values));
            }
            else if (type is "Ranged Weapons" or "Melee Weapons")
                weapons.Add(new(name, type == "Ranged Weapons" ? "Ranged" : "Melee", Value("Range"), Value("A", "Attacks"),
                    Value("BS", "WS", "Skill"), Value("S", "Strength"), Value("AP"), Value("D", "Damage"), values.GetValueOrDefault("Keywords") ?? "", Text(definition, "id"), categories));
            else
            {
                var text = values.Count == 1 ? values.Values.Single() : string.Join("\n", values.Select(v => v.Key + ": " + v.Value));
                if (text.Length > 0) abilities.Add(new(name, text, type == "Abilities" ? section : type));
            }
        }
        void AddRule(JsonObject definition, string section)
        {
            var node = BaseValues(definition, problems);
            if (Flag(node, "hidden")) return;
            var name = Clean(Text(node, "name")); var description = Clean(Text(node, "description"));
            if (description.Length == 0) { problems.Add($"Rule text unavailable: {name}."); return; }
            abilities.Add(new(name, description, section));
        }
        void Visit(JsonObject original, int depth = 0, List<string>? inheritedCategories = null)
        {
            if (depth > 50) { problems.Add("Datasheet reference depth exceeded; some entries were omitted."); return; }
            var node = BaseValues(original, problems);
            if (Flag(node, "hidden")) return;
            if (!path.Enter(node)) { problems.Add("A cyclic datasheet reference was omitted."); return; }
            var section = Text(node, "type") == "upgrade" ? "Equipment rules" : "Abilities";
            var categories = (inheritedCategories ?? []).Concat(Children(node, "categoryLinks").Where(c => !Flag(c, "hidden"))
                .Select(c => Text(c, "targetId"))).Where(id => id.Length > 0).Distinct().Order().ToList();
            foreach (var profile in Children(node, "profiles")) AddProfile(profile, section, categories);
            foreach (var rule in Children(node, "rules")) AddRule(rule, section);
            foreach (var link in Children(node, "infoLinks"))
            {
                if (Flag(link, "hidden")) continue;
                if (!registry.ContainsKey(Text(link, "targetId"))) { problems.Add($"Missing shared datasheet entry: {Text(link, "name")}. Import the repository ZIP."); continue; }
                var target = Resolve(link, registry);
                switch (Text(link, "type"))
                {
                    case "profile": AddProfile(target, section, categories); break;
                    case "rule": AddRule(target, section); break;
                    case "infoGroup": Visit(target, depth + 1, categories); break;
                    default: problems.Add($"Unsupported datasheet link: {Text(link, "name")}."); break;
                }
            }
            foreach (var child in Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups"))
                .Concat(Children(node, "entryLinks")).Concat(Children(node, "infoGroups")))
            {
                if (SeparateSelection(child)) continue;
                try { Visit(Resolve(child, registry), depth + 1, categories); }
                catch (InvalidDataException) { problems.Add($"Missing shared equipment entry: {Text(child, "name")}. Import the repository ZIP."); }
            }
            path.Exit(node);
        }
        Visit(root);
        if (models.Count == 0) problems.Add("No unit characteristics found in this catalogue entry.");
        warnings = problems.ToList();
        if (models.Count + weapons.Count + abilities.Count == 0)
        {
            warnings.Add("No datasheet extracted; an existing datasheet will be retained."); return null;
        }
        LoadoutDefinition? loadout = null;
        try
        {
            loadout = ReadLoadout(root, registry);
            if (Descendants(root, registry, new SelectionPath()).Any(n => Text(n, "type") == "model"
                && !loadout.Roles.Any(r => r.Id == Text(n, "id"))))
            {
                try { loadout = RecursiveLoadout(root, registry); }
                catch (InvalidDataException ex) { warnings.Add("Recursive model choices need source review: " + ex.Message); }
            }
            var mappedOptions = loadout.UnitGroups.Concat(loadout.Roles.SelectMany(r => r.Groups)).SelectMany(g => g.Options).ToList();
            var unmapped = weapons.Where(w => !mappedOptions.Any(o => w.SourceId is { Length: > 0 } && o.WeaponSourceIds is { Count: > 0 }
                ? o.WeaponSourceIds.Contains(w.SourceId) : o.Weapons.Contains(w.Name))).ToList();
            if (unmapped.Count > 0)
            {
                warnings.Add(Loadouts.ReviewWarning);
                loadout = loadout with { Incomplete = true, Warnings = loadout.Warnings.Append(Loadouts.ReviewWarning).Distinct().ToList(),
                    UnitGroups = loadout.UnitGroups.Append(new EquipmentGroup("manual", "Additional weapons (manual review)",
                        Loadouts.ManualOptions(unmapped), new(0), new(1000))).ToList() };
            }
        }
        catch (InvalidDataException ex) { warnings.Add("Automatic loadout unavailable: " + ex.Message + " Select equipment quantities manually."); }
        var detachmentKeywords = ReadDetachmentKeywords(root, registry, warnings);
        return LeaderRules.ImportRules(new UnitProfile(models.Count == 1 ? models[0].Characteristics : null, weapons.Distinct().OrderBy(w => w.Type == "Ranged" ? 0 : 1).ThenBy(w => w.Name).ToList(),
            "Build mode shows all available weapon profiles. Choose equipment in the loadout panel; Play mode shows the saved selections. Check any loadout warnings for unsupported source rules.",
            keywords, abilities.Distinct().ToList(), models, warnings, MandatoryWarlord(root, registry), loadout, Associations: ReadAssociations(root, registry), DetachmentKeywords: detachmentKeywords,
            Semantics: ReadDatasheetSemantics(root, registry), WarlordRestrictions: ReadWarlordRestrictions(root, registry)));
    }

    private static List<WarlordRestriction>? ReadWarlordRestrictions(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        List<WarlordRestriction> rules = [];
        foreach (var child in Children(root, "entryLinks").Concat(Children(root, "selectionEntries")))
        {
            JsonObject node;
            try { node = Resolve(child, registry); } catch (InvalidDataException) { node = child; }
            if (!Text(node, "name").Equals("Warlord", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var constraint in Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent" && Text(c, "type") == "max"))
            {
                if (!int.TryParse(Text(constraint, "value"), out var maximum)) continue;
                var modifiers = Modifiers(node).Concat(Modifiers(root)).Where(m => Text(m, "field") == Text(constraint, "id")).ToList();
                if (maximum != 0 && !modifiers.Any(m => Text(m, "value") == "0")) continue;
                EffectCondition condition = new("constant", maximum == 0 ? "1" : "0");
                foreach (var modifier in modifiers)
                {
                    if (Text(modifier, "type") != "set" || !int.TryParse(Text(modifier, "value"), out var value)) { condition = new("unknown"); break; }
                    var test = EffectTest(modifier, registry, Text(root, "id"));
                    condition = value == 0 ? new("or", Children: [condition, test]) : new("and", Children: [condition, new("not", Children: [test])]);
                }
                rules.Add(new("Warlord selection limit", "The source forbids selecting Warlord (maximum 0).", modifiers.Count == 0 ? null : condition));
            }
        }
        return rules.Count == 0 ? null : rules;
    }

    private static bool MandatoryWarlord(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        foreach (var child in Children(root, "entryLinks").Concat(Children(root, "selectionEntries")))
        {
            JsonObject node;
            try { node = Resolve(child, registry); } catch (InvalidDataException) { node = child; }
            if (!Text(node, "name").Equals("Warlord", StringComparison.OrdinalIgnoreCase) || Flag(node, "hidden")) continue;
            foreach (var constraint in Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent" && Text(c, "type") == "min"))
            {
                // Conditional minima need a rules engine; only unconditional requirements force selection.
                if (decimal.TryParse(Text(constraint, "value"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    && value >= 1 && !Modifiers(node).Concat(Modifiers(root)).Any(m => Text(m, "field") == Text(constraint, "id"))) return true;
            }
        }
        return false;
    }

    // Only unconditional display overrides are safe without an army/loadout rules engine.
    private static JsonObject BaseValues(JsonObject original, HashSet<string> warnings)
    {
        var node = (JsonObject)original.DeepClone();
        var fields = Children(node, "characteristics").Where(c => Text(c, "typeId").Length > 0)
            .GroupBy(c => Text(c, "typeId")).ToDictionary(g => g.Key, g => g.Last());
        foreach (var modifier in Modifiers(original))
        {
            var field = Text(modifier, "field");
            if (field is not ("name" or "description" or "hidden") && !fields.ContainsKey(field)) continue;
            if (Flag(modifier, "unsupportedParentCondition") || Children(modifier, "conditions").Count + Children(modifier, "conditionGroups").Count
                + Children(modifier, "localConditionGroups").Count + Children(modifier, "repeats").Count > 0 || Text(modifier, "affects").Length > 0)
            {
                warnings.Add("Base datasheet shown; conditional profile or visibility changes are not applied."); continue;
            }
            var value = Text(modifier, "value"); var type = Text(modifier, "type");
            if (type is not ("set" or "append")) { warnings.Add("A datasheet modifier could not be applied; check the source."); continue; }
            if (fields.TryGetValue(field, out var characteristic)) characteristic["$text"] = type == "set" ? value : Text(characteristic, "$text") + value;
            else node[field] = type == "set" ? value : Text(node, field) + value;
        }
        return node;
    }
    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"<br\s*/?>|</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Replace("^^", "").Replace("**", "").Replace("__", "").Trim();
    }
}

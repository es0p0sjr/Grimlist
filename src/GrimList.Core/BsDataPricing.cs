using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static HashSet<int> CompositionSizes(JsonObject node, Dictionary<string, JsonObject> registry, bool root = false, bool bundle = false, Dictionary<string, int>? maximums = null)
    {
        if (root && Text(node, "type") == "model") return [1];
        var bounds = Bounds(node, registry, new SelectionPath(), root, bundle, maximums);
        if (bounds.Min < 0 || bounds.Max > 100 || bounds.Max < bounds.Min) throw new InvalidDataException("Unsupported unit composition.");
        var children = Text(node, "type") == "model" ? [] : DirectChildren(node, registry).ToList();
        // Ordinary model groups use their explicit total bounds. Only structural
        // bundle choices need a discrete set (10/20 must not invent a 15-model size).
        bool HasBundles(JsonObject entry) => Descendants(entry, registry, new SelectionPath()).Any(n => Text(n, "type") == "upgrade"
            && Descendants(n, registry, new SelectionPath()).Any(m => Text(m, "type") == "model"));
        if (children.Count == 0 || !HasBundles(node) && !bundle) return Enumerable.Range(bounds.Min, bounds.Max - bounds.Min + 1).ToHashSet();
        if (Text(node, "type") == "" && children.All(c => Text(c, "type") == "upgrade"))
            return children.SelectMany(c => CompositionSizes(c, registry, bundle: true, maximums: maximums)).ToHashSet();
        HashSet<int> totals = [0];
        foreach (var child in children)
        {
            var sizes = CompositionSizes(child, registry, maximums: maximums);
            totals = totals.SelectMany(total => sizes.Select(size => total + size)).Where(n => n <= 100).ToHashSet();
        }
        return totals.Where(n => n >= bounds.Min && n <= bounds.Max).ToHashSet();
    }

    // The app builds Army Rosters. Other force modes must not override their points
    // or model caps. An OR group is inactive only when every branch is inactive.
    private static bool InactiveGameMode(JsonObject node, Dictionary<string, JsonObject> registry)
    {
        var branches = Children(node, "conditions").Select(c => Text(c, "scope") == "force" && Text(c, "type") == "instanceOf"
            && Text(c, "value") == "1" && registry.TryGetValue(Text(c, "childId"), out var force)
            && Text(force, "name") is "Boarding Actions" or "Crusade Force")
            .Concat(Children(node, "conditionGroups").Select(g => InactiveGameMode(g, registry)))
            .Concat(Children(node, "localConditionGroups").Select(_ => false)).ToList();
        return Text(node, "type") == "or" ? branches.Count > 0 && branches.All(b => b) : branches.Any(b => b);
    }

    // Some fixed squads encode "nine troopers, minus each specialist" instead of
    // a group total. Sum those linear constraints; accept only exact cancellation
    // of every variable role, so optional models can never inflate the squad.
    private static int? ReplacementSize(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        var entries = new List<JsonObject>();
        bool Gather(JsonObject node, int depth)
        {
            if (depth > 30) return false;
            if (Text(node, "type") == "model") { entries.Add(node); return true; }
            if (Text(node, "type") == "upgrade") return !Descendants(node, registry, new SelectionPath()).Any(n => Text(n, "type") == "model");
            return DirectChildren(node, registry).All(c => Gather(c, depth + 1));
        }
        if (!Gather(root, 0) || entries.Count < 2 || entries.Select(n => Text(n, "id")).Distinct().Count() != entries.Count) return null;
        var coefficients = entries.ToDictionary(n => Text(n, "id"), _ => 0);
        var constant = 0; var replaced = false;
        foreach (var entry in entries)
        {
            var limits = Children(entry, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
            if (limits.Count(c => Text(c, "type") == "min") > 1 || limits.Count(c => Text(c, "type") == "max") > 1) return null;
            var min = limits.SingleOrDefault(c => Text(c, "type") == "min");
            var max = limits.SingleOrDefault(c => Text(c, "type") == "max");
            var mods = Modifiers(entry).Where(m => limits.Any(c => Text(c, "id") == Text(m, "field")) && !InactiveGameMode(m, registry)).ToList();
            if (min == null || max == null || Text(min, "value") != Text(max, "value"))
            {
                coefficients[Text(entry, "id")]++; continue;
            }
            constant += Number(min, "value");
            var lower = mods.Where(m => Text(m, "field") == Text(min, "id")).ToList();
            var upper = mods.Where(m => Text(m, "field") == Text(max, "id")).ToList();
            string Shape(JsonObject m) { var clone = (JsonObject)m.DeepClone(); clone.Remove("field"); return clone.ToJsonString(); }
            if (!lower.Select(Shape).Order().SequenceEqual(upper.Select(Shape).Order())) return null;
            foreach (var modifier in lower)
            {
                if (Text(modifier, "type") != "decrement" || Text(modifier, "value") != "1" || Flag(modifier, "unsupportedParentCondition")
                    || Text(modifier, "affects").Length > 0 || new[] { "conditions", "conditionGroups", "localConditionGroups" }.Any(k => Children(modifier, k).Count > 0)
                    || Children(modifier, "repeats").Count == 0) return null;
                foreach (var repeat in Children(modifier, "repeats"))
                {
                    if (Text(repeat, "field") != "selections" || Text(repeat, "value") != "1" || Text(repeat, "repeats") is not ("" or "1")
                        || Text(repeat, "scope") != Text(root, "id") && Text(repeat, "scope") != "parent"
                        || !registry.TryGetValue(Text(repeat, "childId"), out var target)) return null;
                    var ids = (Text(target, "type") == "model" ? new[] { target } : Descendants(target, registry, new SelectionPath()).Where(n => Text(n, "type") == "model"))
                        .Select(n => Text(n, "id")).ToList();
                    if (ids.Count == 0 || ids.Any(id => !coefficients.ContainsKey(id))) return null;
                    foreach (var id in ids) coefficients[id]--;
                    replaced = true;
                }
            }
        }
        return replaced && coefficients.Values.All(c => c == 0) && constant > 0 ? constant : null;
    }

    // Named groups/roles can be counted from total size only if all other roles
    // have fixed counts. Never substitute total models for an arbitrary specialist.
    private static Dictionary<string, Func<int, int>> ModelCounts(JsonObject root, Dictionary<string, JsonObject> registry)
    {
        var nodes = Descendants(root, registry, new SelectionPath()).ToList();
        var models = nodes.Where(n => Text(n, "type") == "model").ToList();
        Dictionary<string, Func<int, int>> result = [];
        // Category predicates count matching descendants, not attached leaders.
        // A known category absent from this selection subtree has count zero.
        var presentCategories = nodes.SelectMany(n => Children(n, "categoryLinks")).Select(c => Text(c, "targetId")).ToHashSet();
        foreach (var category in registry.Values.SelectMany(n => Children(n, "categoryLinks")).Select(c => Text(c, "targetId")).Distinct())
            if (!presentCategories.Contains(category)) result[category] = _ => 0;
        foreach (var node in nodes)
        {
            var ids = (Text(node, "type") == "model" ? new[] { node } : Descendants(node, registry, new SelectionPath()).Where(n => Text(n, "type") == "model"))
                .Select(n => Text(n, "id")).ToHashSet();
            if (ids.Count == 0) continue;
            var outside = models.Where(n => !ids.Contains(Text(n, "id"))).ToList();
            try
            {
                var bounds = outside.Select(n => Bounds(n, registry, new SelectionPath())).ToList();
                if (bounds.Any(b => b.Min != b.Max)) continue;
                var offset = bounds.Sum(b => b.Min);
                result[Text(node, "id")] = n => n - offset;
            }
            catch (InvalidDataException) { /* The count depends on loadout composition. */ }
        }
        return result;
    }

    private static List<(JsonObject Node, int Quantity)> EquipmentBundle(JsonObject node, Dictionary<string, JsonObject> registry, SelectionPath path)
    {
        if (!path.Enter(node)) throw new InvalidDataException("Cyclic equipment bundle.");
        List<(JsonObject Node, int Quantity)> result = [(node, 1)];
        foreach (var child in DirectChildren(node, registry))
        {
            var bundle = EquipmentBundle(child, registry, path);
            var fields = bundle.SelectMany(n => Children(n.Node, "costs")).Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase)).Select(c => Text(c, "typeId")).ToHashSet();
            var paid = bundle.Any(n => Children(n.Node, "costs").Any(c => fields.Contains(Text(c, "typeId")) && Number(c, "value") != 0)
                || Modifiers(n.Node).Any(m => fields.Contains(Text(m, "field"))));
            if (!paid) continue;
            var limits = Children(child, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
            var minimum = limits.Where(c => Text(c, "type") == "min").ToList();
            var maximum = limits.Where(c => Text(c, "type") == "max").ToList();
            if (minimum.Count != 1 || maximum.Count != 1 || Text(minimum[0], "value") != Text(maximum[0], "value")
                || Modifiers(child).Any(m => limits.Any(c => Text(c, "id") == Text(m, "field"))))
                throw new InvalidDataException("Variable equipment bundle needs source review.");
            var quantity = Number(minimum[0], "value");
            result.AddRange(bundle.Select(n => (n.Node, checked(n.Quantity * quantity))));
        }
        path.Exit(node); return result;
    }

    private static List<EquipmentCost> EquipmentPrices(JsonObject node, Dictionary<string, JsonObject> registry, LoadoutDefinition? loadout)
    {
        if (loadout == null) return [];
        var nodes = Descendants(node, registry, new SelectionPath()).ToList();
        var fields = nodes.Prepend(node).SelectMany(n => Children(n, "costs")).Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase))
            .Select(c => Text(c, "typeId")).ToHashSet();
        var options = loadout.UnitGroups.Concat(loadout.Roles.SelectMany(r => r.Groups)).SelectMany(g => g.Options).DistinctBy(o => o.Id);
        List<EquipmentCost> costs = [];
        foreach (var option in options)
        {
            if (option.Id.StartsWith("manual:")) continue; // Priced independently from source profile paths.
            var matches = nodes.Where(n => Text(n, "id") == option.Id).ToList();
            if (matches.Count == 0)
            {
                costs.Add(new(option.Id, option.Id.StartsWith("innate:") ? 0 : null, "Equipment price reference unavailable."));
                continue;
            }
            try
            {
                var bundles = matches.Select(n => EquipmentBundle(n, registry, new SelectionPath())).ToList();
                var amounts = bundles.Select(b => b.Sum(n => checked(n.Quantity * Children(n.Node, "costs").Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase)).Sum(c => Number(c, "value"))))).Distinct().ToList();
                var conditional = bundles.Any(b => b.Any(n => Modifiers(n.Node).Any(m => fields.Contains(Text(m, "field")))));
                SourceCost Source(JsonObject selection, int quantity = 1)
                {
                    List<SourceCost> children = [];
                    foreach (var child in DirectChildren(selection, registry))
                    {
                        var descendants = EquipmentBundle(child, registry, new SelectionPath());
                        if (!descendants.Any(i => Children(i.Node, "costs").Any(c => fields.Contains(Text(c, "typeId")) && Number(c, "value") != 0))) continue;
                        var count = Children(child, "constraints").Where(c => Text(c, "type") == "min" && Text(c, "field") == "selections" && Text(c, "scope") == "parent")
                            .Select(c => Number(c, "value")).Single();
                        children.Add(Source(child, count));
                    }
                    return new(Text(selection, "sourceLinkId") is { Length: > 0 } link ? link : Text(selection, "id"),
                        Text(selection, "sourceTargetId"), Text(selection, "name"), quantity,
                        Children(selection, "costs").Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase)).Sum(c => Number(c, "value")), children);
                }
                var source = Source(matches[0]);
                costs.Add(conditional || amounts.Count != 1 ? new(option.Id, null, "Conditional equipment cost needs source review.", source) : new(option.Id, amounts[0], Source: source));
            }
            catch (InvalidDataException ex) { costs.Add(new(option.Id, null, ex.Message)); }
        }
        return costs;
    }

    // Re-read pricing against existing loadout IDs without replacing datasheets or rules.
    // Missing shared definitions leave that unit's current prices untouched.
    public static ImportPreview Reprice(Dataset current, BsDataDocument document, SourceInfo source)
    {
        var registry = Walk(document.Root).Where(n => Text(n, "id").Length > 0).GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => g.First());
        var prefix = $"bsdata:{source.Edition}:{document.Id}:";
        List<UnitDefinition> units = []; List<string> warnings = [];
        foreach (var unit in current.Units)
        {
            var alias = unit.Aliases?.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
            if (alias == null) continue;
            var id = alias[prefix.Length..];
            var candidate = Children(document.Root, "selectionEntries").Concat(Children(document.Root, "entryLinks"))
                .FirstOrDefault(n => Text(n, "targetId") == id || Text(n, "id") == id);
            if (candidate == null) continue;
            try
            {
                var node = Resolve(candidate, registry);
                var costs = EquipmentPrices(node, registry, unit.Profile?.Loadout);
                var modelCosts = ModelPrices(node, registry, unit.Profile?.Loadout);
                var prices = ReadModelPrices(node, registry, costs, modelCosts, unit.Profile, document.Id, false, out var formula);
                var allied = !AlliedRules.IsMarineFaction(unit.Faction) && Modifiers(node).Any(ContainsCatalogueCondition)
                    ? ReadModelPrices(node, registry, costs, modelCosts, unit.Profile, document.Id, true, out _) : null;
                costs.AddRange(ManualEquipmentPrices(node, registry, unit.Profile));
                units.Add(unit with { Options = prices, AlliedOptions = allied, AlliedPointsWarning = "", WargearCosts = costs, PointsWarning = "", PointsSource = source,
                    ModelCosts = modelCosts, PriceFormula = formula, DetachmentCosts = DetachmentPrices(node, registry), CopyCategory = CopyCategory(node) });
            }
            catch (InvalidDataException ex) { warnings.Add(unit.Name + ": " + ex.Message); }
        }
        if (units.Count == 0) throw new InvalidDataException("No supported unit pricing found in this catalogue.");
        var dataset = new Dataset(source, units, current.Game); dataset.Validate();
        return new(dataset, warnings);
    }
}

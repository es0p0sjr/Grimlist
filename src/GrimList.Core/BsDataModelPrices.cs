using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static List<EquipmentCost> ModelPrices(JsonObject root, Dictionary<string, JsonObject> registry, LoadoutDefinition? loadout)
    {
        var models = Descendants(root, registry, new SelectionPath()).Where(n => Text(n, "type") == "model").ToList();
        if (models.Count < 2 || loadout == null) return [];
        List<EquipmentCost> result = [];
        foreach (var group in models.GroupBy(n => Text(n, "id")))
        {
            if (!loadout.Roles.Any(r => r.Id == group.Key)) continue;
            var costs = group.Select(n => Children(n, "costs").Where(c => Text(c, "name") == "pts").Sum(c => Number(c, "value"))).Distinct().ToList();
            var fields = group.SelectMany(n => Children(n, "costs")).Where(c => Text(c, "name") == "pts").Select(c => Text(c, "typeId")).ToHashSet();
            if (costs.Count == 1 && costs[0] > 0 && !group.Any(n => Modifiers(n).Any(m => fields.Contains(Text(m, "field")))))
                result.Add(new(group.Key, costs[0]));
        }
        return result;
    }

    private static List<PointOption> ReadModelPrices(JsonObject root, Dictionary<string, JsonObject> registry, List<EquipmentCost>? equipmentCosts,
        List<EquipmentCost> modelCosts, UnitProfile? profile, string catalogueId, bool allied, out UnitPriceFormula? formula)
    {
        formula = null;
        // A condition can name an entry-link instance instead of its target.
        // Resolve identities through the catalogue graph, without guessing IDs.
        root = (JsonObject)root.DeepClone();
        void ResolveScopes(JsonObject expression)
        {
            if (expression["scope"] != null) expression["scope"] = SelectionIdentity(Text(expression, "scope"), registry);
            foreach (var key in new[] { "modifiers", "modifierGroups", "conditions", "conditionGroups", "localConditionGroups", "repeats" })
                foreach (var child in Children(expression, key)) ResolveScopes(child);
        }
        ResolveScopes(root);
        void CheckScopes(JsonObject expression, string rule)
        {
            foreach (var condition in Children(expression, "conditions"))
            {
                var scope = Text(condition, "scope");
                if (System.Text.RegularExpressions.Regex.IsMatch(scope, @"\A[0-9a-f]+(?:-[0-9a-f]+){3}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && !registry.ContainsKey(scope))
                    throw ImportDiagnostics.BrokenReference(registry.TryGetValue(catalogueId, out var catalogue) ? Text(catalogue, "name") : catalogueId,
                        Text(root, "name"), scope, rule, "price scope");
            }
            foreach (var key in new[] { "conditionGroups", "localConditionGroups", "repeats" })
                foreach (var group in Children(expression, key)) CheckScopes(group, rule);
        }
        var pointFields = Children(root, "costs").Where(c => Text(c, "name") == "pts").Select(c => Text(c, "typeId")).ToHashSet();
        var ruleIndex = 0;
        foreach (var modifier in Modifiers(root))
        {
            var rule = Text(modifier, "id") is { Length: > 0 } id ? id : Text(root, "id") + "/modifier/" + ruleIndex;
            ruleIndex++;
            if (pointFields.Contains(Text(modifier, "field")) && !InactiveGameMode(modifier, registry)) CheckScopes(modifier, rule);
        }
        if (profile?.Loadout?.Composition != null)
            return SelectionPrices(root, registry, equipmentCosts, modelCosts, profile, catalogueId, allied, out formula);
        try { return Prices(root, registry, equipmentCosts, catalogueId, allied, modelCosts, profile?.Loadout); }
        catch (InvalidDataException ex) when (ex.Message == "Unrecognized price condition." && profile?.Loadout != null) { }
        var nodes = Descendants(root, registry, new SelectionPath()).ToList();
        var roleIds = profile!.Loadout!.Roles.Select(r => r.Id).ToHashSet();
        var selectors = nodes.GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => g.SelectMany(n => Text(n, "type") == "model" ? new[] { n }
            : Descendants(n, registry, new SelectionPath()).Where(m => Text(m, "type") == "model")).Select(n => Text(n, "id")).Distinct().ToList());
        var unitId = Text(root, "id"); var copyBreaks = new SortedSet<int> { 1 };
        PriceTest Parse(JsonObject node)
        {
            List<PriceTest> tests = [];
            foreach (var c in Children(node, "conditions"))
            {
                var child = Text(c, "childId"); var scope = Text(c, "scope"); var type = Text(c, "type");
                if (Text(c, "field") != "selections") throw new InvalidDataException("Unsupported composition price field.");
                if (scope == "primary-catalogue" && type is "instanceOf" or "notInstanceOf" && Text(c, "value") == "1")
                {
                    if (allied && child != catalogueId) throw new InvalidDataException("Allied price depends on a specific host catalogue.");
                    tests.Add(new(child == catalogueId ? "native" : "constant", type == "instanceOf" ? "equalTo" : "notEqualTo", 1)); continue;
                }
                if (scope != unitId && scope is not ("self" or "unit-self" or "unit") || type is not ("atLeast" or "greaterThan" or "atMost" or "lessThan" or "equalTo" or "notEqualTo"))
                    throw new InvalidDataException("Unsupported composition price scope.");
                if (child == "model") tests.Add(new("models", type, Number(c, "value")));
                else if (selectors.TryGetValue(child, out var roles) && roles.Count > 0 && roles.All(roleIds.Contains)) tests.Add(new("roles", type, Number(c, "value"), roles));
                else throw new InvalidDataException("Composition price selection is not a model role.");
            }
            foreach (var local in Children(node, "localConditionGroups"))
            {
                // Validate the existing copy-count grammar before storing its threshold.
                var wrapper = new JsonObject { ["localConditionGroups"] = new JsonArray(local.DeepClone()) };
                Predicate(wrapper, unitId, copyBreaks, catalogueId, allied, copyIds: Children(root, "categoryLinks").Select(c => Text(c, "targetId")).ToHashSet());
                tests.Add(new("copy", "atLeast", Number(local, "value") + 1));
            }
            tests.AddRange(Children(node, "conditionGroups").Select(Parse));
            return new(Text(node, "type") == "or" ? "or" : "and", Children: tests);
        }
        var cost = Children(root, "costs").Single(c => Text(c, "name") == "pts"); var field = Text(cost, "typeId");
        List<PriceRule> rules = [];
        foreach (var modifier in Modifiers(root).Where(m => Text(m, "field") == field && !InactiveGameMode(m, registry)))
        {
            if (Text(modifier, "type") is not ("set" or "increment" or "decrement" or "multiply") || Flag(modifier, "unsupportedParentCondition")
                || Text(modifier, "affects").Length > 0 || Children(modifier, "repeats").Count > 0) throw new InvalidDataException("Unsupported composition price operation.");
            rules.Add(new(Text(modifier, "type"), Number(modifier, "value"), Parse(modifier)));
        }
        // Reuse the normal paid-descendant checks with only root price predicates removed.
        var clone = (JsonObject)root.DeepClone(); clone.Remove("modifiers"); clone.Remove("modifierGroups");
        Prices(clone, registry, equipmentCosts, catalogueId, allied, modelCosts, profile?.Loadout);
        formula = new(Number(cost, "value"), rules);
        var unit = new UnitDefinition("", "", [], Profile: profile, PriceFormula: formula);
        List<PointOption> result = [];
        foreach (var size in CompositionSizes(root, registry, root: true, maximums: CompositionMaximums(root, registry, profile?.Loadout)).Order())
        {
            var entry = new ArmyEntry("", "", "", size);
            int? previous = null;
            foreach (var copy in copyBreaks)
            {
                var price = FormulaPricing.Cost(unit, entry, copy, allied) ?? throw new InvalidDataException("Default composition cannot be priced.");
                if (price != previous) result.Add(new(size, price, copy));
                previous = price;
            }
        }
        return result;
    }
}

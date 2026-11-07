using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, JsonObject>, HashSet<string>> equipmentPointFields = new();
    // Read source paths independently of the editor's ability to express selection limits.
    // A profile can occur under several differently priced selections; every occurrence
    // must agree before a manual weapon quantity can have a numeric price.
    private static List<EquipmentCost> ManualEquipmentPrices(JsonObject root, Dictionary<string, JsonObject> registry, UnitProfile? profile)
    {
        var options = Loadouts.ManualOptions(profile?.Weapons ?? []);
        if (options.Count == 0) return [];
        var fields = equipmentPointFields.GetValue(registry, entries => entries.Values.Where(n => Text(n, "name").Equals("pts", StringComparison.OrdinalIgnoreCase))
            .Select(n => Text(n, "id")).Concat(entries.Values.SelectMany(n => Children(n, "costs"))
                .Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase)).Select(c => Text(c, "typeId")))
            .Where(id => id.Length > 0).ToHashSet());
        var matches = new Dictionary<string, List<EquipmentCost>>();
        var complete = true;
        List<JsonObject> Profiles(JsonObject node, HashSet<string> path, int depth = 0)
        {
            if (depth > 40) throw new InvalidDataException("Weapon profile depth exceeded.");
            var id = Text(node, "id");
            if (id.Length > 0 && !path.Add(id)) throw new InvalidDataException("Cyclic weapon profile reference.");
            List<JsonObject> result = [];
            if (Text(node, "typeName") is "Ranged Weapons" or "Melee Weapons") result.Add(node);
            foreach (var child in Children(node, "profiles").Concat(Children(node, "infoGroups"))
                .Concat(Children(node, "infoLinks").Where(n => Text(n, "type") is "profile" or "infoGroup")))
                result.AddRange(Profiles(Resolve(child, registry), path, depth + 1));
            if (id.Length > 0) path.Remove(id);
            return result;
        }
        int Amount(JsonObject n) => Children(n, "costs").Where(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase)).Sum(c => Number(c, "value"));
        var path = new SelectionPath();
        void Visit(JsonObject node, List<JsonObject> ancestors, int depth = 0)
        {
            if (depth > 40 || !path.Enter(node)) throw new InvalidDataException("Incomplete equipment source path.");
            var lineage = ancestors.Append(node).ToList();
            var profiles = Profiles(node, []);
            var amount = lineage.Where(n => Text(n, "type") is not ("unit" or "model")).Sum(Amount);
            // Unit/copy and model prices have their own calculators. Only modifiers
            // targeting equipment (or explicitly affecting descendants) taint this price.
            var conditional = lineage.Any(n => Modifiers(n).Any(m => fields.Contains(Text(m, "field"))
                && (Text(n, "type") is not ("unit" or "model") || Text(m, "affects").Length > 0)));
            var children = DirectChildren(node, registry).ToList();
            // A paid multi-profile bundle cannot be charged once for each of its weapons.
            var simplePaid = Text(node, "type") == "upgrade" && amount == Amount(node) && children.Count == 0
                && profiles.Select(n => Loadouts.WeaponFamily(Clean(Text(n, "name")))).Distinct().Count() == 1;
            int? price = conditional || amount > 0 && !simplePaid ? null : amount;
            var source = new SourceCost(Text(node, "sourceLinkId") is { Length: > 0 } link ? link : Text(node, "id"),
                Text(node, "sourceTargetId"), Text(node, "name"), 1, Amount(node));
            foreach (var weapon in profiles)
            {
                var id = Text(weapon, "id");
                if (!matches.TryGetValue(id, out var values)) matches[id] = values = [];
                values.Add(new(id, price, price == null ? "Conditional or bundled equipment price needs its source selection." : "", source));
            }
            foreach (var child in children) Visit(child, lineage, depth + 1);
            path.Exit(node);
        }
        try { Visit(root, []); }
        catch (InvalidDataException) { complete = false; }
        return options.Select(option =>
        {
            var ids = option.WeaponSourceIds ?? [];
            var evidence = ids.SelectMany(id => matches.GetValueOrDefault(id) ?? []).ToList();
            var known = complete && ids.Count > 0 && ids.All(matches.ContainsKey) && evidence.Count > 0
                && evidence.All(c => c.Points != null) && evidence.Select(c => c.Points).Distinct().Count() == 1;
            return new EquipmentCost(option.Id, known ? evidence[0].Points : null,
                known ? "" : "Manual equipment price is unresolved: missing, conditional or ambiguous source selection.",
                known ? evidence[0].Source : null);
        }).ToList();
    }
}

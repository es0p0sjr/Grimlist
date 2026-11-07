using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    private static List<PointOption> SelectionPrices(JsonObject root, Dictionary<string, JsonObject> registry,
        List<EquipmentCost>? equipmentCosts, List<EquipmentCost> modelCosts, UnitProfile profile, string catalogueId, bool allied,
        out UnitPriceFormula? formula)
    {
        var cost = Children(root, "costs").SingleOrDefault(c => Text(c, "name") == "pts")
            ?? throw new InvalidDataException("Missing unit base price for recursive composition.");
        var field = Text(cost, "typeId");
        var copyBreaks = new SortedSet<int> { 1 };
        var copyIds = Children(root, "categoryLinks").Select(c => Text(c, "targetId")).Append(Text(root, "id")).ToHashSet();
        var rules = NumericModifiers(root, field, "root", registry, copyIds, copyBreaks);
        var covered = (modelCosts.Select(c => c.OptionId)).Concat(DetachmentPrices(root, registry).Select(c => c.SourceId)).ToHashSet();
        var nodes = Descendants(root, registry, new SelectionPath()).ToList();
        foreach (var n in nodes.Where(n => equipmentCosts?.Any(c => c.OptionId == Text(n, "id") && c.Points != null) == true))
            foreach (var item in EquipmentBundle(n, registry, new SelectionPath())) covered.Add(Text(item.Node, "id"));
        foreach (var n in nodes.Where(n => !covered.Contains(Text(n, "id"))))
            if (Children(n, "costs").Any(c => Text(c, "name") == "pts" && Number(c, "value") > 0)
                || Modifiers(n).Any(m => Text(m, "field") == field && !InactiveGameMode(m, registry) && !ImpossibleChildCopy(m, n, root, registry)))
                throw new InvalidDataException("Selected-tree cost is not represented: " + Text(n, "id") + " / " + Text(n, "name"));
        formula = new(Number(cost, "value"), [], rules, catalogueId);
        var unit = new UnitDefinition("", "", [], Profile: profile, PriceFormula: formula);
        List<PointOption> result = [];
        foreach (var size in SelectionCompositions.States(profile.Loadout!.Composition!).Select(s => s.Models).Distinct().Order())
        {
            var entry = new ArmyEntry("preview", "", "", size, Loadout: Loadouts.Create(unit, size));
            int? previous = null;
            foreach (var copy in copyBreaks)
            {
                var price = FormulaPricing.Cost(unit, entry, copy, allied) ?? throw new InvalidDataException("Default recursive composition cannot be priced.");
                if (previous != price) result.Add(new(size, price, copy));
                previous = price;
            }
        }
        return result;
    }
}

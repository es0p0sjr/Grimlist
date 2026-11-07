namespace GrimList.Core;

public record PriceTest(string Kind, string Comparison = "", int Value = 0, List<string>? RoleIds = null, List<PriceTest>? Children = null);
public record PriceRule(string Operation, int Value, PriceTest Test);
public record UnitPriceFormula(int Base, List<PriceRule> Rules, List<NumericModifier>? SelectionRules = null, string CatalogueId = "");

public static class FormulaPricing
{
    public static int? Cost(UnitDefinition unit, ArmyEntry entry, int copy, bool allied)
    {
        var formula = unit.PriceFormula;
        var loadout = entry.Loadout ?? Loadouts.Create(unit, entry.Models);
        if (formula == null || loadout.Manual || loadout.Models.Count != entry.Models
            || loadout.Models.Any(m => !Loadouts.Definition(unit).Roles.Any(r => r.Id == m.RoleId))
            || Loadouts.Issues(unit, entry with { Loadout = loadout }).Count > 0) return null;
        bool Test(PriceTest expression) => RuleEvaluation.Test(expression, t => t.Kind, t => t.Children ?? [], test =>
        {
            var value = test.Kind switch
            {
                "models" => entry.Models, "roles" => loadout.Models.Count(m => test.RoleIds!.Contains(m.RoleId)),
                "copy" => copy, "native" => allied ? 0 : 1, "constant" => 0,
                _ => throw new InvalidDataException("Unknown price test.")
            };
            return RuleEvaluation.Compare(value, test.Comparison, test.Value);
        });
        var price = formula.Base;
        if (formula.SelectionRules != null)
        {
            var composition = unit.Profile?.Loadout?.Composition;
            var selected = composition == null ? null : SelectionCompositions.State(composition, loadout);
            if (composition == null || selected == null) return null;
            price = RuleEvaluation.Effective(price, formula.SelectionRules, new SelectionContext(composition, selected, loadout, copy, allied, formula.CatalogueId).Count);
        }
        foreach (var rule in formula.Rules.Where(r => Test(r.Test))) price = RuleEvaluation.Apply(price, rule.Operation, rule.Value);
        return price < 0 ? null : price;
    }
}

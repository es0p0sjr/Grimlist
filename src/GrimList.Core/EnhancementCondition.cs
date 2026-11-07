namespace GrimList.Core;

// Source eligibility is independent of the display text. Unknown source conditions
// remain unknown; they must never grant permission to equip an enhancement.
public record EnhancementCondition(string Kind, string Value = "", string Comparison = "equalTo", int Count = 1,
    List<EnhancementCondition>? Children = null)
{
    internal bool? Evaluate(ArmyList army, UnitDefinition unit, IReadOnlyCollection<string> keywords, Dataset? data = null)
    {
        var children = Children ?? [];
        bool? Evaluate(EnhancementCondition c) => c.Evaluate(army, unit, keywords, data);
        if (Kind == "not") return children.Count == 1 ? !Evaluate(children[0]) : null;
        if (Kind is "and" or "or")
        {
            var values = children.Select(Evaluate).ToList();
            if (Kind == "and") return values.Contains(false) ? false : values.Contains(null) ? null : true;
            return values.Contains(true) ? true : values.Contains(null) ? null : false;
        }
        int? value = Kind switch
        {
            "constant" => int.TryParse(Value, out var number) ? number : null,
            "keyword" => keywords.Contains(UnitIdentity.Normalize(Value)) ? 1 : 0,
            "unit" => (unit.Aliases ?? []).Any(a => a.EndsWith(":" + Value, StringComparison.Ordinal)) ? 1 : 0,
            "detachment" => DetachmentRules.Contains(army, Value, data) ? 1 : 0,
            _ => null
        };
        if (value == null) return null;
        return Comparison switch
        {
            "equalTo" or "instanceOf" => value == Count,
            "notEqualTo" or "notInstanceOf" => value != Count,
            "lessThan" => value < Count,
            "greaterThan" => value > Count,
            "atLeast" => value >= Count,
            "atMost" => value <= Count,
            _ => null
        };
    }
}

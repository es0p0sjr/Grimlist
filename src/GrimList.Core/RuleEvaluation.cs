namespace GrimList.Core;

// Shared numeric modifier and boolean-tree semantics for prices and association counts.
public static class RuleEvaluation
{
    public static bool Compare(int actual, string comparison, int expected) => comparison switch
    {
        "atLeast" => actual >= expected, "atMost" => actual <= expected,
        "greaterThan" => actual > expected, "lessThan" => actual < expected,
        "equalTo" => actual == expected, "notEqualTo" => actual != expected,
        _ => throw new InvalidDataException("Unsupported comparison: " + comparison)
    };
    public static int Apply(int current, string operation, int value) => operation switch
    {
        "set" => value, "increment" => checked(current + value), "decrement" => checked(current - value), "multiply" => checked(current * value),
        _ => throw new InvalidDataException("Unsupported modifier: " + operation)
    };
    public static bool Test<T>(T node, Func<T, string> kind, Func<T, IEnumerable<T>> children, Func<T, bool> leaf) => kind(node) switch
    {
        "and" => children(node).All(c => Test(c, kind, children, leaf)),
        "or" => children(node).Any(c => Test(c, kind, children, leaf)),
        _ => leaf(node)
    };

    public static bool Test(SelectionTest test, Func<SelectionQuery, int> query) => Test(test, t => t.Kind, t => t.Children ?? [],
        t => Compare(query(t.Query ?? throw new InvalidDataException("Missing rule query.")), t.Comparison, t.Value));

    // Repeats on one modifier are additive; they count applications, not a change
    // to its operand. This distinction matters for SET and MULTIPLY.
    public static int Effective(int value, IEnumerable<NumericModifier> modifiers, Func<SelectionQuery, int> query)
    {
        foreach (var modifier in modifiers.OrderBy(m => m.Order))
        {
            if (!Test(modifier.Condition, query)) continue;
            var times = modifier.Repeats.Count == 0 ? 1 : modifier.Repeats.Sum(r =>
            {
                if (r.Every <= 0 || r.Times < 0) throw new InvalidDataException("Invalid numeric repeat.");
                var count = query(r.Query);
                return checked((r.RoundUp ? (count + r.Every - 1) / r.Every : count / r.Every) * r.Times);
            });
            if (times is < 0 or > 10000) throw new InvalidDataException("Numeric repeat exceeds supported evaluation limit.");
            for (var i = 0; i < times; i++) value = Apply(value, modifier.Operation, modifier.Value);
        }
        return value;
    }

    public static void Validate(IEnumerable<NumericModifier> modifiers)
    {
        bool Query(SelectionQuery q) => q.Scope.Length > 0 && q.ChildId.Length > 0
            && q.Field is "selections" or "associations" or "copies" or "copy-before";
        bool Valid(SelectionTest t, int depth = 0) => depth < 30 && (t.Kind is "and" or "or"
            ? (t.Children ?? []).All(c => Valid(c, depth + 1))
            : t.Kind == "query" && t.Query != null && Query(t.Query) && t.Value >= 0
                && t.Comparison is "atLeast" or "atMost" or "equalTo" or "notEqualTo" or "lessThan" or "greaterThan");
        foreach (var m in modifiers)
            if (m.Value < 0 || m.Order < 0 || m.Operation is not ("set" or "increment" or "decrement" or "multiply")
                || !Valid(m.Condition) || m.Repeats.Any(r => r.Every <= 0 || r.Times < 0 || !Query(r.Query)))
                throw new InvalidDataException("Invalid imported numeric rule: " + m.SourceId);
    }
}

public record SelectionQuery(string Scope, string ChildId, string Field = "selections", bool IncludeChildSelections = false,
    bool IncludeChildForces = false, string Origin = "", bool Shared = true);
public record SelectionTest(string Kind = "and", SelectionQuery? Query = null, string Comparison = "", int Value = 0,
    List<SelectionTest>? Children = null);
public record NumericRepeat(SelectionQuery Query, int Every, int Times = 1, bool RoundUp = false);
public record NumericModifier(string Operation, int Value, SelectionTest Condition, List<NumericRepeat> Repeats, int Order = 0, string SourceId = "");

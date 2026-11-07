namespace GrimList.Core;

public static class RosterOrder
{
    // Callers retain category/attachment grouping. Equal names put larger units
    // first; exact ties keep their saved order through LINQ's stable sorting.
    public static IComparer<ArmyEntry> ByNameThenSize { get; } = Comparer<ArmyEntry>.Create((left, right) =>
    {
        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : right.Models.CompareTo(left.Models);
    });
}

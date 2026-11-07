using GrimList.Core;

namespace GrimList.Application;

// Exact comparisons of immutable presentation inputs. No hashes, serialization,
// army evaluation or rule interpretation; callers retain the previous snapshot.
public static class PresentationEquality
{
    public static bool Sequence<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right, Func<T, T, bool>? same = null)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!(same?.Invoke(left[i], right[i]) ?? EqualityComparer<T>.Default.Equals(left[i], right[i]))) return false;
        return true;
    }
    private static bool Characteristics(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Count != right.Count) return false;
        // Rendering preserves source field order, and defensive-save lookup uses
        // the first matching alias. Compare ordered entries with exact labels.
        using var other = right.GetEnumerator();
        foreach (var field in left)
        {
            if (!other.MoveNext() || field.Key != other.Current.Key || field.Value != other.Current.Value) return false;
        }
        return true;
    }
    public static bool Stats(UnitProfile? left, UnitProfile? right)
        => ReferenceEquals(left, right) || Characteristics(left?.Characteristics, right?.Characteristics)
            && Sequence(left?.Models, right?.Models, static (a, b) => a.Name == b.Name && Characteristics(a.Characteristics, b.Characteristics));
    public static bool Weapon(WeaponProfile left, WeaponProfile right)
        => ReferenceEquals(left, right) || left.Name == right.Name && left.Type == right.Type && left.Range == right.Range
            && left.Attacks == right.Attacks && left.Skill == right.Skill && left.Strength == right.Strength && left.Ap == right.Ap
            && left.Damage == right.Damage && left.Keywords == right.Keywords && left.SourceId == right.SourceId
            && left.GrantedBy == right.GrantedBy && Sequence(left.SourceCategories, right.SourceCategories);
    public static bool EffectiveWeapon(EffectiveWeapon left, EffectiveWeapon right)
        => ReferenceEquals(left, right) || Weapon(left.Original, right.Original) && Weapon(left.Profile, right.Profile)
            && Sequence(left.Changes, right.Changes);
}

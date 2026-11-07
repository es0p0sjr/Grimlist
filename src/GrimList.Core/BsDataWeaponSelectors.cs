namespace GrimList.Core;

// Known unfiltered BSData profile traversals. 'group' is a traversal segment,
// not a weapon category; arbitrary filters must remain unresolved predicates.
internal static class BsDataWeaponSelectors
{
    public static string Type(string selector) => selector switch
    {
        "self.entries.recursive.profiles.Melee Weapons" or "self.entries.profiles.Melee Weapons"
            or "self.entries.group.recursive.profiles.Melee Weapons" or "profiles.Melee Weapons" => "Melee",
        "self.entries.recursive.profiles.Ranged Weapons" or "self.entries.profiles.Ranged Weapons"
            or "self.entries.group.recursive.profiles.Ranged Weapons" or "profiles.Ranged Weapons" => "Ranged",
        "self.entries.recursive.profiles" or "self.entries.profiles" or "self.entries.group.recursive.profiles" => "All",
        "profiles.Unit" or "self.profiles.Unit" or "self.entries.profiles.Unit" or "self.entries.recursive.profiles.Unit"
            or ModelStatModifiers.UnitGroupSelector => "Model",
        _ => ""
    };
}

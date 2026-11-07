namespace GrimList.Core;

public record PrimaryMatchup(string OpponentDisposition, string YourPrimary, string OpponentPrimary, int SourcePage);
public record DispositionPrimaryView(string Disposition, string Hint, IReadOnlyList<PrimaryMatchup> Matchups);

// Chapter Approved 2026-27. Verified against GW Event Companion v1.2,
// layout A headings on the pages below (2026-10-14). Shared, offline UI data.
public static class DispositionPrimaries
{
    public const string SourceLabel = "Chapter Approved 2026-27 · GW Event Companion v1.2";
    public const string SourceUrl = "https://assets.warhammer-community.com/eng_wh40k_event_companion-pl87i44rzn-a7ieny8i9x.pdf";
    private static readonly string[] Dispositions = ["Take and Hold", "Purge the Foe", "Disruption", "Reconnaissance", "Priority Assets"];
    private sealed record Pair(int First, int Second, string FirstPrimary, string SecondPrimary, int Page);
    private static readonly Pair[] Pairs =
    [
        new(0, 0, "Battlefield Dominance", "Battlefield Dominance", 9),
        new(0, 1, "Immovable Object", "Unstoppable Force", 12),
        new(0, 2, "Determined Acquisition", "Death Trap", 15),
        new(0, 3, "Purge and Secure", "Reconnaissance Sweep", 18),
        new(0, 4, "Inescapable Dominion", "Secure Asset", 21),
        new(1, 1, "Meatgrinder", "Meatgrinder", 24),
        new(1, 2, "Punishment", "Delaying Action", 27),
        new(1, 3, "Consecrate", "Triangulation", 30),
        new(1, 4, "Destroyer's Wrath", "Vital Link", 33),
        new(2, 2, "Outmanoeuvre", "Outmanoeuvre", 36),
        new(2, 3, "Smoke and Mirrors", "Surveil the Foe", 39),
        new(2, 4, "Locate and Deny", "Extract Relic", 42),
        new(3, 3, "Gather Intel", "Gather Intel", 45),
        new(3, 4, "Search and Scour", "Vanguard Operation", 48),
        new(4, 4, "Sabotage", "Sabotage", 51)
    ];

    public static bool IsKnownPrimary(string name) => Pairs.Any(p => UnitIdentity.Normalize(p.FirstPrimary) == UnitIdentity.Normalize(name)
        || UnitIdentity.Normalize(p.SecondPrimary) == UnitIdentity.Normalize(name));

    public static PrimaryMatchup Matchup(string yours, string opponent)
    {
        var a = Array.IndexOf(Dispositions, yours); var b = Array.IndexOf(Dispositions, opponent);
        if (a < 0 || b < 0) throw new InvalidDataException("Unknown force disposition.");
        var pair = Pairs.Single(p => p.First == Math.Min(a, b) && p.Second == Math.Max(a, b));
        return new(opponent, a == pair.First ? pair.FirstPrimary : pair.SecondPrimary,
            a == pair.First ? pair.SecondPrimary : pair.FirstPrimary, pair.Page);
    }

    public static DispositionPrimaryView ForArmy(ArmyList army, Dataset? data)
    {
        if (army.Game != "Warhammer 40,000" || data?.Game != army.Game || data.Source.Edition != "11")
            return new("", "Primary matchups are available for Warhammer 40,000 11th edition.", []);
        var available = DetachmentRules.Dispositions(army, data);
        if (available.Count == 0)
            return new("", "Add a detachment to unlock a disposition and its primary missions.", []);
        if (string.IsNullOrWhiteSpace(army.Disposition))
            return new("", "Choose an army disposition to see its five primary matchups.", []);
        if (!available.Any(d => UnitIdentity.Normalize(d) == UnitIdentity.Normalize(army.Disposition)))
            return new("", "Choose a disposition available to the army's selected detachments.", []);
        var selected = Array.FindIndex(Dispositions, d => UnitIdentity.Normalize(d) == UnitIdentity.Normalize(army.Disposition));
        if (selected < 0)
            return new(army.Disposition, "Primary matchups are not available for this disposition.", []);
        var rows = Enumerable.Range(0, Dispositions.Length).Select(opponent =>
        {
            var pair = Pairs.Single(p => p.First == Math.Min(selected, opponent) && p.Second == Math.Max(selected, opponent));
            return new PrimaryMatchup(Dispositions[opponent], selected == pair.First ? pair.FirstPrimary : pair.SecondPrimary,
                selected == pair.First ? pair.SecondPrimary : pair.FirstPrimary, pair.Page);
        }).ToArray();
        return new(Dispositions[selected], "Your opponent's disposition determines your primary mission. All five matchups are shown below.", rows);
    }
}

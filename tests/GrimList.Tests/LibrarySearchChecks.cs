using GrimList.Application;
using GrimList.Core;

internal static class LibrarySearchChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run(Action<string, Action> test, string root)
    {
        test("Unit search matches names and keywords with case, whitespace and partial matching", () =>
        {
            var unit = new UnitDefinition("Fixture", "Test squad", [new(1, 50)],
                Profile: new(Keywords: ["Infantry", "Great Devourer"], Notes: "Unrelated narrative"));
            foreach (var query in new[] { "", "  ", " SQUAD ", "infan", " great DEVOURER " })
                Check(UnitSearch.Matches(unit, query), "Did not match: " + query);
            Check(!UnitSearch.Matches(unit, "narrative"), "Rules prose matched as a keyword");
            Check(!UnitSearch.Matches(unit with { Profile = null }, "infantry"), "Missing profile matched");
            Check(UnitSearch.Matches(unit with { Profile = new() }, "squad"), "Missing keywords broke name search");
        });
        test("Keyword search preserves library eligibility, filters and cached availability", () =>
        {
            var unit = new UnitDefinition("Death Guard", "Test squad", [new(1, 50)], Profile: new(Keywords: ["Infantry"]));
            var data = new Dataset(new("Search fixture", "1", "", ""),
                [unit, unit with { Name = "Old squad", Profile = new(Keywords: ["Infantry", "Legends"]) },
                 unit with { Name = "Crucible squad", Profile = new(Keywords: ["Infantry", "Crucible"]) },
                 unit with { Name = "Expensive squad", Options = [new(1, 3000)] },
                 unit with { Name = "Restricted squad", RequiredDetachments = ["Required detachment"] },
                 unit with { Name = "Foreign squad", Faction = "Tyranids" }]);
            using var store = WindowsServices.OpenStore(Path.Combine(root, "keyword-search")); store.Import(data);
            var session = new ArmySession(store); session.Create(unit.Faction, "Search", 2000);
            var rows = session.BrowseLibrary(new());
            var builds = (session.EligibilityBuildCount, session.LibraryBuildCount);
            var found = session.BrowseLibrary(new(" INFANTRY "));
            Check(found.Count == 4 && found.All(r => r.Unit.Faction == unit.Faction && r.Unit.Name != "Restricted squad"),
                "Keyword search bypassed faction/detachment eligibility");
            Check(session.BrowseLibrary(new("quad")).Count == 4, "Name search regressed");
            Check(session.BrowseLibrary(new("missing")).Count == 0, "Unmatched query returned units");
            Check(ReferenceEquals(rows, session.BrowseLibrary(new("  "))), "Clearing search did not restore cached rows");
            Check(builds == (session.EligibilityBuildCount, session.LibraryBuildCount), "Typing rebuilt availability");
            var filtered = session.BrowseLibrary(new("infantry", FitsRemaining: true, HideLegends: true, HideCrucible: true));
            Check(filtered.Count == 1 && filtered[0].Unit.Name == unit.Name, "Keyword search bypassed library filters");
        });
        test("Bundled CSM Damned and Tyranid Harvester units are discoverable by keyword", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            foreach (var (faction, query, examples) in new[]
            {
                ("Chaos Space Marines", "Damned", new[] { "Accursed Cultists", "Cultist Mob", "Dark Commune" }),
                ("Tyranids", "Harvester", new[] { "Haruspex", "Psychophage", "Pyrovores", "Ripper Swarms" })
            })
            {
                var available = AlliedRules.Library(ArmyList.New(faction), data);
                var matching = available.Where(u => UnitSearch.Matches(u, query)).ToList();
                foreach (var name in examples)
                    Check(matching.Any(u => u.Name == name), faction + ": keyword search missed " + name);
                var tagged = available.Where(u => u.Profile?.Keywords?.Contains(query) == true).ToList();
                Check(tagged.Count > 0 && tagged.All(u => matching.Contains(u)), faction + ": missing keyword results");
            }
        });
    }
}

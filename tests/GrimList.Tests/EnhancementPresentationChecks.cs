using GrimList.Application;
using GrimList.Core;

internal static class EnhancementPresentationChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        var unit = new UnitDefinition("Fixture", "Bearer", [new(1, 100)], Profile: new(Keywords: ["Character"]));
        var cards = new List<ReferenceCard> { new("Fixture", "Enhancements", "First", "Original text", "Host", "10 pts", Eligibility: new("constant", "1")),
            new("Fixture", "Enhancements", "Second", "Second text", "Host", "20 pts", Eligibility: new("constant", "1")) };
        var data = new Dataset(new("Fixture", "1", "", ""), [unit], Detachments: [new("Fixture", "Host")], ReferenceCards: cards);
        var army = ArmyList.New("Fixture") with { Detachment = "Host", Entries = [new("first", unit.Key, unit.Name, 1), new("second", unit.Key, unit.Name, 1)] };
        test("Prepared enhancement options match command acceptance and retain exact rejection messages", () =>
        {
            foreach (var snapshot in new[] { army, Enhancements.Select(army, data, "first", new("Host", "First")),
                army with { Entries = [army.Entries[0], army.Entries[1] with { AttachedToId = "first", Enhancement = new("Host", "First") }] } })
            {
                var before = DataJson.Write(snapshot);
                var prepared = EnhancementPresentation.Prepare(snapshot, data, "first", true);
                Check(prepared.Options.Count == 2, "Fixture options missing");
                foreach (var option in prepared.Options)
                {
                    string? rejection = null;
                    if (!option.Selected) try { Enhancements.Select(snapshot, data, "first", new(option.Card.Detachment, option.Card.Name)); }
                        catch (InvalidDataException error) { rejection = error.Message; }
                    Check(option.Rejection == rejection, "Prepared/command mismatch");
                }
                Check(DataJson.Write(snapshot) == before, "Preparation mutated army");
            }
        });
        test("Enhancement preparation caches only identical immutable inputs and separates summaries", () =>
        {
            var summary = EnhancementPresentation.Prepare(army, data, "first", false);
            Check(summary.Options.Count == 0, "Summary eagerly prepared choices");
            var results = new EnhancementPresentation[24];
            Parallel.For(0, results.Length, i => results[i] = EnhancementPresentation.Prepare(army, data, "first", true));
            Check(results.All(r => ReferenceEquals(r, results[0])), "Concurrent/repeated preparation diverged");
            var selected = Enhancements.Select(army, data, "first", new("Host", "First"));
            Check(EnhancementPresentation.Prepare(selected, data, "first", true).Options.Single(o => o.Card.Name == "First").Selected, "Selection used stale result");
            Check(!EnhancementPresentation.Prepare(army, data, "first", true).Options.Any(o => o.Selected), "Undo snapshot changed");
            var changed = data with { ReferenceCards = [cards[0] with { Text = "New import" }, cards[1]] };
            Check(EnhancementPresentation.Prepare(army, changed, "first", true).Options[0].Card.Text == "New import", "Same-name source update reused old text");
            Check(EnhancementPresentation.Prepare(army with { Limit = 1000 }, data, "first", true).Budget == 2, "Army limit reused stale budget");
        });
    }
}

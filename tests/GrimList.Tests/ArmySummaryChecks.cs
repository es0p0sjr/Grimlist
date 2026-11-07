using GrimList.Application;
using GrimList.Core;

internal static class ArmySummaryChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("Saved summaries reuse unchanged results and invalidate on roster, source and external updates", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "summary-cache"));
            var data = LoadoutChecks.Fixture(); store.Import(data);
            var session = new ArmySession(store); var unit = session.Dataset!.Units.First();
            session.Create(unit.Faction, "Summary test", 2000); session.AddUnit(unit.Key, unit.Options[0].Models);
            var copy = session.Army.Duplicate(); store.Save(copy);
            void Accurate()
            {
                foreach (var summary in session.LoadArmySummaries())
                {
                    var expected = ArmyCalculator.Calculate(summary.Army, session.Dataset);
                    if (summary.Result != new ArmySummary(expected.Total, expected.Complete, expected.Validated)) throw new Exception("Stale summary");
                }
            }
            Accurate(); var count = session.SummaryCalculationCount;
            Accurate(); if (session.SummaryCalculationCount != count) throw new Exception("Unchanged Home recalculates armies");
            store.Save(copy with { Limit = 1 }); Accurate();
            if (session.SummaryCalculationCount != count + 1) throw new Exception("External edit did not invalidate exactly one summary");
            session.ResetLoadout(session.SelectedEntry!.Id); Accurate();
            session.ReloadLibrary(); Accurate();
            if (session.SummaryCalculationCount != count + 2) throw new Exception("Library replacement did not invalidate summaries");
            store.Delete(copy.Id); Accurate();
            if (session.LoadArmySummaries().Count != 1) throw new Exception("Deleted summary retained");
        });
    }
}

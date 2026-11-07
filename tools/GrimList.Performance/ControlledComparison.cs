#if PERFORMANCE_CHECKS
using System.Security.Cryptography;
using System.Text;
using GrimList.Application;
using GrimList.Core;

public static class ControlledComparison
{
    public record Verification(string Operation, int Iteration, string StateHash, int Projected, int Reused, bool Incremental);
    public static async Task<object> Run(ArmySession session, Func<string, Task> present, string mode, int iterations)
    {
        ControlledPerformance.Configure(mode);
        var data = session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        var inputHash = Hash(DataJson.Write(data));
        List<Verification> verified = [];
        var samples = await AcceptanceWorkload.Run(session, present, iterations, onlyEntries: 20, warmups: 3,
            completed: (s, operation, iteration) =>
            {
                // All comparison/serialization is outside the measured interval.
                var army = s.Army; var library = s.DatasetForArmy(army)!;
                var full = EffectiveDatasheets.EvaluateFull(army, library);
                var actual = army.Entries.Select(e => EffectiveDatasheets.Project(army, e, library)).ToArray();
                for (var i = 0; i < army.Entries.Count; i++)
                    if (DataJson.Write(actual[i]) != DataJson.Write(full[army.Entries[i].Id]))
                        throw new InvalidDataException($"Effective-state mismatch: {mode}/{operation}/{iteration}/{army.Entries[i].Id}");
                var work = EffectiveDatasheets.Work(army, library);
                if (mode != "optimized" && work.Incremental) throw new InvalidDataException("Full comparison unexpectedly reused entries");
                var canonical = DataJson.Write(new { States = actual, s.Result });
                // AddUnit generates a fresh ID; normalize IDs solely for result comparison.
                foreach (var entry in army.Entries.Select((e, i) => (e.Id, Index: i)).OrderByDescending(e => e.Id.Length))
                    canonical = canonical.Replace(entry.Id, $"fixture-entry-{entry.Index}", StringComparison.Ordinal);
                verified.Add(new(operation, iteration, Hash(canonical), work.Projected, work.Reused, work.Incremental));
            });
        return new { Mode = mode, Entries = 20, Iterations = iterations, Warmups = 3, InputHash = inputHash,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            CoreModule = typeof(ArmyList).Module.ModuleVersionId,
            ApplicationModule = typeof(ArmySession).Module.ModuleVersionId,
            FullReads = session.FactionLoading?.FullReads, Samples = samples, Verification = verified };
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
#endif

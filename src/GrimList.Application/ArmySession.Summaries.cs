using System.Security.Cryptography;
using System.Text;
using GrimList.Core;

namespace GrimList.Application;

public record ArmySummary(int Total, bool Complete, bool Validated);
public record SavedArmySummary(ArmyList Army, ArmySummary Result);

public sealed partial class ArmySession
{
    private object? summaryLibrary;
    internal sealed record CachedSummary(string Revision, string Library, ArmySummary Value);
    private readonly Dictionary<string, CachedSummary> savedSummaries = [];
    public long SummaryCalculationCount { get; private set; }
    private static string ArmyRevision(ArmyList army) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataJson.Write(army))));
    public List<SavedArmySummary> LoadArmySummaries()
    {
        lock (sync)
        {
            // Full roster fingerprints plus their actual catalogue dependencies.
            // Unrelated faction edits no longer discard every saved army summary.
            var identity = (object?)factionLibrary ?? Dataset;
            var path = factionLibrary == null ? null : Path.Combine(repository.DirectoryPath, "derived-cache", "summaries-2.gz");
            if (!ReferenceEquals(summaryLibrary, identity))
            {
                savedSummaries.Clear(); summaryLibrary = identity;
                if (path != null)
                    try { foreach (var row in FactionLibrary.Read<Dictionary<string, CachedSummary>>(path)) savedSummaries.Add(row.Key, row.Value); }
                    catch (Exception e) when (FactionLibrary.CacheError(e)) { savedSummaries.Clear(); }
            }
            var armies = repository.LoadArmies();
            var dirty = false;
            var live = armies.Select(a => a.Id).ToHashSet();
            foreach (var removed in savedSummaries.Keys.Where(k => !live.Contains(k)).ToArray()) { savedSummaries.Remove(removed); dirty = true; }
            var currentRevision = IsOpen ? ArmyRevision(Army) : "";
            var summaries = armies.Select(army =>
            {
                var revision = ArmyRevision(army);
                var library = factionLibrary?.SummaryRevision(army) ?? "";
                if (!savedSummaries.TryGetValue(army.Id, out var cached) || cached.Revision != revision || cached.Library != library)
                {
                    ArmyResult result;
                    if (IsOpen && army.Id == Army.Id && revision == currentRevision) result = Result;
                    else { result = calculate(army, DatasetForArmy(army)); SummaryCalculationCount++; }
                    cached = new(revision, library, new(result.Total, result.Complete, result.Validated)); dirty = true;
                    savedSummaries[army.Id] = cached;
                }
                return new SavedArmySummary(army, cached.Value);
            }).ToList();
            if (dirty && path != null)
                try { FactionLibrary.Write(path, savedSummaries); } catch (Exception e) when (FactionLibrary.CacheError(e)) { }
            return summaries;
        }
    }
}

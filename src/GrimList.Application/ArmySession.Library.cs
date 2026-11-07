using GrimList.Core;

namespace GrimList.Application;

public sealed partial class ArmySession
{
    // Eligibility depends on source, owning faction/game, detachment/disposition,
    // and limit. It does NOT depend on equipment, attachments, warlord or entry IDs.
    // Row limits/copy prices additionally depend on selected unit counts/sizes.
    // FitsRemaining additionally depends on calculated points. Search only filters
    // the cached rows; it never runs eligibility again.
    private sealed record LibraryScope(Dataset? Dataset, string Game, string Faction, int Limit, string Detachments, string Disposition);
    private LibraryScope? libraryScope;
    private IReadOnlyList<UnitDefinition> eligibleUnits = [];
    private (string Key, int Models)[] libraryStructure = [];
    private readonly Dictionary<(bool Legends, bool Crucible, bool Fits, int Points), IReadOnlyList<LibraryChoice>> libraryChoices = [];
    public long EligibilityBuildCount { get; private set; }
    public long LibraryBuildCount { get; private set; }
    private void ReleasePreviousLibrary()
    {
        // A source replacement is the one event that invalidates every library
        // projection. Release the old snapshot even if the user stays off the roster.
        libraryScope = null; eligibleUnits = []; libraryStructure = []; libraryChoices.Clear();
        summaryLibrary = null; savedSummaries.Clear();
    }

    private void EnsureLibraryState()
    {
        var scope = new LibraryScope(Dataset, Army.Game, Army.Faction, Army.Limit,
            DataJson.Write(DetachmentRules.Selected(Army)), Army.Disposition);
        // Dataset records contain mutable list DTOs, but published snapshots are
        // immutable by contract. Replacement identity is the source-change signal.
        var sourceChanged = !ReferenceEquals(scope.Dataset, libraryScope?.Dataset);
        var scopeChanged = sourceChanged || libraryScope == null || scope with { Dataset = null } != libraryScope with { Dataset = null };
        var structure = Army.Entries.Select(e => (e.UnitKey, e.Models)).OrderBy(e => e.UnitKey, StringComparer.Ordinal).ThenBy(e => e.Models).ToArray();
        if (scopeChanged)
        {
            eligibleUnits = AlliedRules.Library(Army, Dataset).AsReadOnly();
            EligibilityBuildCount++; libraryScope = scope;
        }
        if (scopeChanged || !libraryStructure.SequenceEqual(structure))
        {
            libraryStructure = structure; libraryChoices.Clear();
        }
    }
    public IReadOnlyList<LibraryChoice> BrowseLibrary(LibraryFilter filter)
    {
        lock (sync)
        {
            EnsureLibraryState();
            var key = (filter.HideLegends, filter.HideCrucible, filter.FitsRemaining, filter.FitsRemaining ? Result.Total : 0);
            if (!libraryChoices.TryGetValue(key, out var rows))
            {
                // Retain only the current remaining-points projection per filter.
                foreach (var stale in libraryChoices.Keys.Where(k => k.Fits && k.Points != Result.Total).ToArray()) libraryChoices.Remove(stale);
                rows = BuildLibrary(filter with { Search = "" }, eligibleUnits).AsReadOnly();
                libraryChoices.Add(key, rows); LibraryBuildCount++;
            }
            var query = filter.Search.Trim();
            return query.Length == 0 ? rows : rows.Where(r => UnitSearch.Matches(r.Unit, query)).ToArray();
        }
    }
}

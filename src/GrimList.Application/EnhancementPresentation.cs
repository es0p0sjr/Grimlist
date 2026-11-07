using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GrimList.Core;

namespace GrimList.Application;

public sealed record EnhancementOption(ReferenceCard Card, bool Selected, string? Rejection);
public sealed record EnhancementPresentation(int Choices, int Upgrades, int? Budget, ReferenceCard? SelectedCard,
    IReadOnlyList<string> Issues, IReadOnlyList<EnhancementOption> Options)
{
    private static readonly ConditionalWeakTable<ArmyList, ConditionalWeakTable<Dataset, ConcurrentDictionary<(string, bool), Lazy<EnhancementPresentation>>>> cache = new();
    public static EnhancementPresentation Prepare(ArmyList army, Dataset data, string entryId, bool includeOptions)
    {
        var entries = cache.GetValue(army, static _ => new()).GetValue(data, static _ => new());
        var key = (entryId, includeOptions);
        var pending = entries.GetOrAdd(key, k => new(() => Build(army, data, k.Item1, k.Item2)));
        try { return pending.Value; }
        catch
        {
            // Remove only this failed computation; another caller may already have retried.
            ((ICollection<KeyValuePair<(string, bool), Lazy<EnhancementPresentation>>>)entries).Remove(new(key, pending));
            throw;
        }
    }

    private static EnhancementPresentation Build(ArmyList army, Dataset data, string entryId, bool includeOptions)
    {
        var entry = army.Entries.Single(e => e.Id == entryId);
        var usage = Enhancements.Usage(army, data);
        List<EnhancementOption> options = [];
        if (includeOptions && EffectiveDatasheets.Project(army, entry, data)?.Unit is { } unit)
            foreach (var card in Enhancements.Options(army, data, unit))
            {
                var choice = new EnhancementSelection(card.Detachment, card.Name);
                var selected = entry.Enhancement != null && Enhancements.Key(entry.Enhancement) == Enhancements.Key(choice);
                string? reason = null;
                if (!selected) try { Enhancements.Select(army, data, entryId, choice); }
                    catch (InvalidDataException error) { reason = error.Message; }
                options.Add(new(card, selected, reason));
            }
        return new(usage.Choices, usage.Upgrades, Enhancements.Budget(army.Limit), Enhancements.Find(army, data, entry.Enhancement),
            Enhancements.Issues(army, data, entry).ToList().AsReadOnly(), options.AsReadOnly());
    }
}

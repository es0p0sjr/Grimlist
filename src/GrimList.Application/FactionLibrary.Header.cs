using GrimList.Core;

namespace GrimList.Application;

public sealed partial class FactionLibrary
{
    private static Manifest PackHeader(Manifest manifest)
    {
        var sources = manifest.Sources.ToList();
        var ids = sources.Select((source, index) => (source, index)).ToDictionary(p => p.source, p => p.index);
        int? Source(SourceInfo? source)
        {
            if (source == null) return null;
            if (!ids.TryGetValue(source, out var id)) { id = sources.Count; sources.Add(source); ids.Add(source, id); }
            return id;
        }
        return manifest with { Sources = sources,
            HeaderSources = new((manifest.Header.Detachments ?? []).Select(d => Source(d.Source)).ToList(),
                (manifest.Header.ReferenceCoverage ?? []).Select(c => Source(c.Source)).ToList()),
            Header = manifest.Header with {
                Detachments = manifest.Header.Detachments?.Select(d => d with { Source = null }).ToList(),
                ReferenceCoverage = manifest.Header.ReferenceCoverage?.Select(c => c with { Source = null }).ToList()
            }
        };
    }

    private static Manifest ExpandHeader(Manifest manifest)
    {
        if (manifest.HeaderSources is not { } bindings) return manifest; // Version 3 stored sources inline.
        if (bindings.Detachments.Count != (manifest.Header.Detachments?.Count ?? 0)
            || bindings.Coverage.Count != (manifest.Header.ReferenceCoverage?.Count ?? 0)
            || bindings.Detachments.Concat(bindings.Coverage).Any(id => !ValidSource(id, manifest)))
            throw new InvalidDataException("Invalid pooled header source binding");
        SourceInfo? Source(int? id) => id == null ? null : manifest.Sources[id.Value];
        return manifest with { HeaderSources = null, Header = manifest.Header with {
            Detachments = manifest.Header.Detachments?.Select((d, i) => d with { Source = Source(bindings.Detachments[i]) }).ToList(),
            ReferenceCoverage = manifest.Header.ReferenceCoverage?.Select((c, i) => c with { Source = Source(bindings.Coverage[i]) }).ToList()
        } };
    }
}

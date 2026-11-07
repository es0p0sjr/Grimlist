namespace GrimList.Core;

// Edition-specific operative rules are bundled for offline use, with provenance.
public static class CoreStratagems
{
    private static readonly Lazy<List<ReferenceCard>> Tenth = new(() => Read("10"));
    private static readonly Lazy<List<ReferenceCard>> Eleventh = new(() => Read("11"));

    public static List<ReferenceCard> ForEdition(string edition) => edition switch
    {
        "10" => [.. Tenth.Value],
        "11" => [.. Eleventh.Value],
        _ => []
    };

    private static List<ReferenceCard> Read(string edition)
    {
        using var stream = typeof(CoreStratagems).Assembly.GetManifestResourceStream($"GrimList.Core.Resources.core-stratagems-{edition}.json")
            ?? throw new InvalidDataException($"Missing core stratagem rules for edition {edition}.");
        using var reader = new StreamReader(stream);
        var dataset = DataJson.Read<Dataset>(reader.ReadToEnd());
        dataset.Validate();
        return dataset.ReferenceCards!;
    }
}

using GrimList.Core;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GrimList.Application;

// Bump the appropriate contract when its interpretation changes. Resource evidence
// is fingerprinted automatically at build time; unrelated UI builds stay reusable.
public static class LibraryRecoveryVersion
{
    public const string Normalization = "normalization-1";
    public const string Compilation = "rule-compilation-1";
    public const string Availability = "availability-1";
    public const string Evaluation = "army-evaluation-3";
    private static readonly (string Name, string Hash)[] resources = new[] { typeof(LibraryRecoveryVersion).Assembly, typeof(Dataset).Assembly }
        .SelectMany(a => a.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(x => x.Key.StartsWith("GrimList.CacheResource.", StringComparison.Ordinal))
            .Select(x => (Name: a.GetName().Name + "/" + x.Key.Replace('\\', '/'), Hash: x.Value ?? "")))
        .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    private static string Stamp(string contract, params string[] names)
    {
        var selected = resources.Where(r => names.Any(n => r.Name.Contains(n, StringComparison.Ordinal))).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Missing build-time cache resource fingerprints: " + contract);
        return contract + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", selected.Select(r => r.Name + ":" + r.Hash)))));
    }
    public static readonly string References = Stamp("references-1/" + Normalization,
        "references-11", "reference-baselines", "army-rule-recovery", "GrimList.Core/");
    public static readonly string Enhancements = Stamp("enhancement-eligibility-1/" + Normalization, "enhancement-recovery", "GrimList.Core/");
    public static readonly string Effective = Stamp("effective-1/" + Compilation, "effective-recovery/", "weapon-metadata");
    public static readonly string Catalogue = Stamp("catalogue-1/" + Normalization, "detachment-metadata", "equipment-prices", "GrimList.Core/");
    public static readonly string Current = string.Join("/", "recovery-2", References, Enhancements, Effective, Catalogue);
}

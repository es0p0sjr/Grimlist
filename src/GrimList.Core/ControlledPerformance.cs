#if PERFORMANCE_CHECKS
namespace GrimList.Core;

// Only compiled into explicitly opted-in diagnostic builds, never production.
// Each comparison process selects one mode before opening its fixture workload.
internal static class ControlledPerformance
{
    internal static string Mode { get; private set; } = "optimized";
    internal static bool ReuseEvaluations => Mode == "optimized";
    internal static bool CacheRuleText => Mode != "uncached";
    internal static void Configure(string mode)
    {
        if (mode is not ("optimized" or "full" or "uncached")) throw new ArgumentException("Unknown comparison mode", nameof(mode));
        Mode = mode;
        EffectiveDatasheets.ResetPerformanceCaches();
        CompiledRuleText.ResetPerformanceCaches();
    }
}

public static partial class EffectiveDatasheets
{
    internal static void ResetPerformanceCaches()
    {
        evaluations.Clear();
        recentEvaluations.Clear();
    }
}
#endif

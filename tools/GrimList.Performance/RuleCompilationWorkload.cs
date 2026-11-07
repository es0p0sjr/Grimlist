using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

internal static class RuleCompilationWorkload
{
    internal static void Run(string output)
    {
        var release = BootstrapRelease.ReadBundled();
        var data = release.Dataset;
        var cards = (data.ReferenceCards ?? []).Where(c => c.Section is "Enhancements" or "Detachments" or "Army Rules").ToList();
        var abilities = data.Units.SelectMany(u => u.Profile?.Abilities ?? []).Distinct().ToList();
        long Visit(bool compiled)
        {
            long count = 0;
            foreach (var card in cards)
                foreach (var roster in new[] { false, true })
                    count += compiled ? CompiledRuleText.ForCard(card, roster).Count
                        : WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, roster).Count();
            foreach (var ability in abilities)
                count += compiled ? CompiledRuleText.Attached(ability).Count + CompiledRuleText.Weapons(ability).Count
                    : DatasheetRuleText.Attached(ability).Count + DatasheetRuleText.WeaponAbilities(ability.Name, ability.Text).Count;
            return count;
        }
        // Cold compilation, then alternating complete passes over identical sources.
        var coldStart = GC.GetTotalAllocatedBytes(true); var coldClock = Stopwatch.StartNew();
        var expected = Visit(true); var coldMs = coldClock.Elapsed.TotalMilliseconds;
        var coldBytes = GC.GetTotalAllocatedBytes(true) - coldStart;
        _ = Visit(false);
        List<object> samples = [];
        for (var i = 0; i < 6; i++)
            foreach (var compiled in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var before = CompiledRuleText.CompilationCount;
                var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                var actual = Visit(compiled); var elapsed = clock.Elapsed.TotalMilliseconds;
                var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                if (actual != expected || CompiledRuleText.CompilationCount != before) throw new InvalidOperationException("Rule compilation workload changed results or recompiled warm sources.");
                samples.Add(new { Compiled = compiled, Ms = elapsed, AllocatedBytes = bytes, Modifiers = actual });
            }
        var profiles = data.Units.Where(u => u.Profile != null).Take(100).Select(u => u.Profile!).ToList();
        var copies = profiles.Select(p => DataJson.Read<UnitProfile>(DataJson.Write(p))).ToList();
        List<object> presentation = [];
        for (var i = 0; i < 6; i++)
            foreach (var structural in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                for (var repeat = 0; repeat < 100; repeat++)
                    for (var n = 0; n < profiles.Count; n++)
                    {
                        var a = profiles[n]; var b = copies[n];
                        var same = structural ? PresentationEquality.Stats(a, b)
                            : DataJson.Write(new { a.Characteristics, a.Models }) == DataJson.Write(new { b.Characteristics, b.Models });
                        if (!same) throw new InvalidOperationException("Presentation equality mismatch.");
                    }
                presentation.Add(new { Structural = structural, Ms = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
            }
        File.WriteAllText(output, DataJson.Write(new { Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Configuration = "Release", Scope = "Static interpretation and stat comparison only; excludes persistence, complete evaluation and native UI",
            Source = data.Source, release.Manifest.ReleaseId, release.Manifest.Sha256,
            CompilerVersion = CompiledRuleText.Version, Cards = cards.Count, Abilities = abilities.Count,
            ColdMs = coldMs, ColdAllocatedBytes = coldBytes, Samples = samples, Presentation = presentation }));
        Console.WriteLine($"Rule compilation comparison passed: {cards.Count} cards, {abilities.Count} distinct abilities; results: {output}");
    }
}

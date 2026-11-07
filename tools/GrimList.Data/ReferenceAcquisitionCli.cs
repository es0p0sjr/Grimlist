using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

internal static class ReferenceAcquisitionCli
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools/reference_acquisition/cli.py"))) directory = directory.Parent;
            if (directory == null) throw new InvalidDataException("Run references from the GrimList workspace.");
            var start = new ProcessStartInfo("python") { UseShellExecute = false };
            start.Environment["PYTHONPATH"] = directory.FullName;
            foreach (var argument in new[] { "-B", "-m", "tools.reference_acquisition.cli" }.Concat(args)) start.ArgumentList.Add(argument);
            var startedAt = DateTime.UtcNow;
            using var process = Process.Start(start) ?? throw new InvalidDataException("Python 3.10+ is required by reference acquisition.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 1 || args.Contains("--dry-run") || args.Length == 0 || args[0] is not ("build-pack" or "update")) return process.ExitCode;
            string Option(string name, string fallback = "")
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }
            var edition = Option("--edition", "11");
            var output = Option("--output", "artifacts/references");
            var candidate = Path.Combine(output, $"GrimList-References-{edition}e.candidate.json");
            if (!File.Exists(candidate)) return process.ExitCode;
            if (File.GetLastWriteTimeUtc(candidate) < startedAt)
                throw new InvalidDataException("No fresh candidate was generated; an earlier output will not be published.");
            var pack = new StructuredJsonReferenceAdapter().Read(Path.GetFileName(candidate), await File.ReadAllTextAsync(candidate), edition);
            var current = DataJson.Read<Dataset>(await File.ReadAllTextAsync(Option("--dataset")));
            var preview = DatasetMerge.Prepare(current, pack, ImportLayer.Profiles, allowReferenceConflicts: true);
            if (DataJson.Write(current.Units) != DataJson.Write(preview.Dataset.Units)) throw new InvalidDataException("Reference preview changed unit data.");
            if (preview.ReferenceChanges.Any(c => c.Status is "Unresolved")) throw new InvalidDataException("Shared importer could not resolve generated cards.");
            // Conflicts with a user's existing source remain a visible import decision.
            // This tool exports a pack and preview; it never applies to an active store.
            await File.WriteAllTextAsync(Path.Combine(output, "reference-import-preview.json"), DataJson.Write(new {
                Applied = false, UnitsUnchanged = true, Changes = preview.ReferenceChanges,
                Coverage = ReferenceCoverageAudit.Build(preview.Dataset)
            }));
            var target = Path.Combine(output, $"GrimList-References-{edition}e.json");
            await File.WriteAllTextAsync(target + ".tmp", DataJson.Write(pack));
            File.Move(target + ".tmp", target, true);
            Console.WriteLine($"Validated through shared JSON adapter and merge: {pack.ReferenceCards!.Count} cards. Pack: {target}. No active library changed.");
            return process.ExitCode;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("Reference acquisition failed: " + error.Message);
            return 1;
        }
    }
}

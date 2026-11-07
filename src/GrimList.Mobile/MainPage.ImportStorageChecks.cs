#if PERFORMANCE_CHECKS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private sealed record ImportStorageExpected(string Digest, string Army, int Units, string Revision);
    private static string LibraryDigest(Dataset data)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, data, DataJson.Options); stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    // Real parser, review UI, apply button, SQLite adapter and session reload in
    // the isolated test app. Inputs are pinned fixtures embedded only in this
    // test build; production storage is never touched.
    private async Task RunImportStorageChecks(string directory, bool reopen)
    {
        var expectedPath = Path.Combine(directory, "import-storage-expected.json");
        List<string> checks = [];
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        using var runtime = Java.Lang.Runtime.GetRuntime()!;
        var heapLimit = runtime.MaxMemory();
        Check(heapLimit <= 256L * 1024 * 1024, "Java heap limit is at most 256 MiB");
        if (reopen)
        {
            var expected = DataJson.Read<ImportStorageExpected>(File.ReadAllText(expectedPath));
            var loaded = await Task.Run(() => workspace!.Repository.LoadLibrary());
            Check(LibraryDigest(loaded.Dataset!) == expected.Digest && loaded.Revision == expected.Revision, "Cold restart retains the complete applied library");
            Check(DataJson.Write(workspace!.Repository.LoadArmies().Single(a => a.Id == "import-storage-army")) == expected.Army, "Cold restart retains the saved army");
            await Session.RunAsync(s => s.Open("import-storage-army"));
            screen = Screen.Editor; await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
            File.WriteAllText(Path.Combine(directory, "import-storage-reopen.json"), DataJson.Write(new { Checks = checks, Units = loaded.Dataset!.Units.Count, HeapLimit = heapLimit }));
            return;
        }

        var sourceDirectory = Path.Combine(FileSystem.Current.CacheDirectory, "import-storage-fixture");
        Directory.CreateDirectory(sourceDirectory);
        foreach (var name in new[] { "repository.zip", "source.json", "mfm.json" })
        {
            using var resource = typeof(MainPage).Assembly.GetManifestResourceStream("GrimList.Mobile.ImportStorage." + name)
                ?? throw new InvalidOperationException("Build this check with ImportStorageFixtureDirectory pointing to the pinned fixtures.");
            using var output = File.Create(Path.Combine(sourceDirectory, name)); resource.CopyTo(output);
        }

        long peakJava = 0;
        using var sampling = new Timer(_ =>
        {
            var used = runtime.TotalMemory() - runtime.FreeMemory();
            if (used > Interlocked.Read(ref peakJava)) Interlocked.Exchange(ref peakJava, used);
        }, null, 0, 50);
        var armyData = Session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        var unit = armyData.Units.First(u => u.Faction == "Death Guard" && u.Name == "Plague Marines" && u.Options.Count > 0);
        var army = ArmyList.New("Death Guard") with { Id = "import-storage-army", Name = "Import memory regression",
            Entries = [new("import-storage-entry", unit.Key, unit.Name, unit.Options[0].Models, Loadout: Loadouts.Create(unit, unit.Options[0].Models))] };
        workspace!.Repository.Save(army); await Session.RunAsync(s => s.Open(army.Id));
        var armyBefore = DataJson.Write(workspace.Repository.LoadArmies().Single(a => a.Id == army.Id));
        List<object> timings = [];
        ImportPreview? parsed = null;
        void Progress(int pass, string stage) => File.WriteAllText(Path.Combine(directory, "import-storage-progress.json"), DataJson.Write(new { Pass = pass, Stage = stage }));
        for (var pass = 0; pass < 2; pass++)
        {
            var clock = Stopwatch.StartNew();
            Progress(pass, "Parsing pinned BSData and MFM fixtures");
            if (parsed == null)
            {
                parsed = await BsDataClient.ReadCached(Path.Combine(sourceDirectory, "repository.zip"));
                await using var mfmFile = File.OpenRead(Path.Combine(sourceDirectory, "mfm.json"));
                var points = (await JsonSerializer.DeserializeAsync<Dataset>(mfmFile, DataJson.Options))!;
                parsed = parsed with { Dataset = MfmPoints.Overlay(parsed.Dataset, points).Dataset };
            }
            var parseMs = clock.ElapsedMilliseconds; clock.Restart();
            Progress(pass, "Preparing full-library preview");
            await PreviewImport(parsed.Dataset, ImportLayer.Combined, parsed.Warnings, CancellationToken.None);
            var preview = pendingImport!;
            Check(preview.BaseRevision != null && preview.BaseSnapshot.Length == 0, $"Pass {pass}: preview uses a revision token");
            Check(preview.Dataset.Units.Count >= 2000, $"Pass {pass}: full all-armies dataset");
            await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
            Check(screen == Screen.ImportReview, $"Pass {pass}: review screen rendered");
            var previewMs = clock.ElapsedMilliseconds; clock.Restart();
            Progress(pass, "Applying through the review button");
            var button = this.GetVisualTreeDescendants().OfType<Button>().Single(b => b.AutomationId == "apply-library");
            Check(button.IsEnabled, $"Pass {pass}: update can be applied");
            busy = false; ((IButtonController)button).SendClicked();
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (busy && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check(!busy && pendingImport == null && screen != Screen.ImportReview, $"Pass {pass}: apply button completed");
            busy = true;
            var stored = await Task.Run(() => workspace.Repository.LoadLibrary());
            Check(stored.Revision != preview.BaseRevision, $"Pass {pass}: new library committed");
            Check(stored.Dataset!.Units.Count == preview.Dataset.Units.Count, $"Pass {pass}: all units persisted");
            // ApplyImport has always compiled attachment rules and removed obsolete
            // duplicate reference cards before saving. Compare that canonical input.
            var canonical = ReferenceData.Normalize(LeaderRules.ImportRules(preview.Dataset)) with { Source = stored.Dataset.Source };
            Check(await Task.Run(() => LibraryDigest(stored.Dataset) == LibraryDigest(canonical)), $"Pass {pass}: complete canonical dataset round trip");
            Check(DataJson.Write(workspace.Repository.LoadArmies().Single(a => a.Id == army.Id)) == armyBefore, $"Pass {pass}: saved army unchanged");
            Check(Session.LibraryUnitCount == stored.Dataset.Units.Count, $"Pass {pass}: session reloaded");
            var rejected = false;
            try { await workspace.Library.ApplyAsync(preview); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, $"Pass {pass}: stale preview rejected");
            File.WriteAllText(expectedPath, DataJson.Write(new ImportStorageExpected(LibraryDigest(stored.Dataset), armyBefore, stored.Dataset.Units.Count, stored.Revision!)));
            timings.Add(new { Pass = pass, ParseMs = parseMs, PreviewMs = previewMs, ApplyAndVerifyMs = clock.ElapsedMilliseconds });
            Progress(pass, "Apply and full-data verification passed");
        }
        await PrepareAsync(); RefreshPresentation(); await DisplayFrames();
        File.WriteAllText(Path.Combine(directory, "import-storage.json"), DataJson.Write(new { Checks = checks, Timings = timings, HeapLimit = heapLimit, PeakJavaBytes = peakJava }));
    }
}
#endif

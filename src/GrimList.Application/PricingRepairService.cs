using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public static class PricingRepairService
{
    public static string Apply(IArmyRepository store, PricingRepairPreview preview)
    {
        if (preview.Recovered == 0) throw new InvalidDataException("No recovered prices to apply.");
        if (DataJson.Write(store.LoadDataset()) != preview.BaseSnapshot)
            throw new InvalidDataException("The library changed since the scan. Scan again before applying repairs.");
        var backup = Path.Combine(store.DirectoryPath, "backups", "before-pricing-repair-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "dataset.json"), preview.BaseSnapshot);
        File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(store.LoadArmies()));
        File.WriteAllText(Path.Combine(backup, "repair-report.json"), DataJson.Write(preview.Rows));
        store.ApplyImport(preview.Dataset, preview.BaseSnapshot);
        return backup;
    }
}

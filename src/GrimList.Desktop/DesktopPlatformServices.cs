using System.Windows;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Desktop;

public sealed class DesktopClipboard : IClipboardService
{
    public Task SetTextAsync(string text, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Clipboard.SetText(text); return Task.CompletedTask;
    }
}
public sealed class DesktopPdfExporter : IRosterPdfExporter
{
    public byte[] Create(ArmyList army, Dataset? dataset, bool includeRules, bool allWeapons)
        => RosterPdf.Create(army, dataset, includeRules, allWeapons);
}

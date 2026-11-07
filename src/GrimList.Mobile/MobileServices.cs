using GrimList.Platform;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using IFilePicker = GrimList.Platform.IFilePicker;

namespace GrimList.Mobile;

public sealed class MobilePaths : IPlatformPaths
{
    public string DataDirectory => FileSystem.Current.AppDataDirectory;
    public string CacheDirectory => Path.Combine(DataDirectory, "sources");
}
public sealed class MobileFilePicker : IFilePicker
{
    public async Task<PickedDocument?> PickAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose library JSON, BSData ZIP, or MFM PDF/text" }));
        cancellation.ThrowIfCancellationRequested();
        return result == null ? null : new(result.FileName, await result.OpenReadAsync());
    }
}
public sealed class MobileClipboard : IClipboardService
{
    public Task SetTextAsync(string text, CancellationToken cancellation = default)
    { cancellation.ThrowIfCancellationRequested(); return Clipboard.Default.SetTextAsync(text); }
}
public sealed class MobileFileSharing : IFileSharing
{
    public async Task ShareAsync(string name, string contentType, byte[] content, CancellationToken cancellation = default)
    {
        var folder = Path.Combine(FileSystem.Current.CacheDirectory, "exports"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(name));
        await File.WriteAllBytesAsync(path, content, cancellation);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        { Title = "Share GrimList file", File = new ShareFile(path, contentType) }));
    }
}

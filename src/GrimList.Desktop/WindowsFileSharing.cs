using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GrimList.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GrimList.Desktop;

public sealed class WindowsFileSharing(Window owner, IPlatformPaths paths) : IFileSharing, IDisposable
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr window, ref Guid iid);
        void ShowShareUIForWindow(IntPtr window);
    }
    private DataTransferManager? manager;
    private StorageFile? file;
    public async Task ShareAsync(string name, string contentType, byte[] content, CancellationToken cancellation = default)
    {
        var folder = Path.Combine(paths.CacheDirectory, "bug-reports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(name));
        await File.WriteAllBytesAsync(path, content, cancellation);
        file = await StorageFile.GetFileFromPathAsync(path);
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        if (manager == null)
        {
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
            var pointer = interop.GetForWindow(hwnd, ref iid);
            try { manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
            manager.DataRequested += ProvideReport;
        }
        cancellation.ThrowIfCancellationRequested();
        interop.ShowShareUIForWindow(hwnd);
    }
    private void ProvideReport(DataTransferManager sender, DataRequestedEventArgs args)
    {
        args.Request.Data.Properties.Title = "GrimList bug report";
        args.Request.Data.RequestedOperation = DataPackageOperation.Copy;
        if (file != null) args.Request.Data.SetStorageItems([file]);
    }
    public void Dispose() { if (manager != null) manager.DataRequested -= ProvideReport; manager = null; file = null; }
}

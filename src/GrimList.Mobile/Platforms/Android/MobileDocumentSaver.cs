using Android.App;
using Android.Content;
using Microsoft.Maui.ApplicationModel;

namespace GrimList.Mobile;

public interface IMobileDocumentSaver
{
    Task<bool> SaveAsync(string name, string contentType, byte[] content);
}

/// <summary>Saves through Android's document provider, including non-filesystem content URIs.</summary>
public sealed class MobileDocumentSaver : IMobileDocumentSaver
{
    internal const int RequestCode = 18252;
    private static TaskCompletionSource<Android.Net.Uri?>? pending;
    public async Task<bool> SaveAsync(string name, string contentType, byte[] content)
    {
        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref pending, completion, null) != null) throw new InvalidOperationException("A save dialog is already open.");
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var intent = new Intent(Intent.ActionCreateDocument);
                intent.AddCategory(Intent.CategoryOpenable); intent.SetType(contentType); intent.PutExtra(Intent.ExtraTitle, name);
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity ?? throw new InvalidOperationException("The save dialog requires an active window.");
#pragma warning disable CS0618 // SAF result is forwarded by MainActivity and survives activity recreation in this process.
                activity.StartActivityForResult(intent, RequestCode);
#pragma warning restore CS0618
            });
            var uri = await completion.Task; if (uri == null) return false;
            await Task.Run(async () =>
            {
                using var stream = Android.App.Application.Context.ContentResolver!.OpenOutputStream(uri, "wt")
                    ?? throw new IOException("The selected document could not be opened for writing.");
                await stream.WriteAsync(content); await stream.FlushAsync();
            });
            return true;
        }
        finally { Interlocked.CompareExchange(ref pending, null, completion); }
    }
    internal static void Complete(Result result, Intent? data) => pending?.TrySetResult(result == Result.Ok ? data?.Data : null);
}

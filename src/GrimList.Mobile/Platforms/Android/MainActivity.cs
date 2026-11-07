using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace GrimList.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Android.Content.Intent.ActionView], Categories = [Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable],
    DataScheme = "grimlist", DataHost = "battle")]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState); ReceiveBattleIntent(Intent);
    }
    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent); ReceiveBattleIntent(intent);
    }
    private static void ReceiveBattleIntent(Android.Content.Intent? intent)
    {
        if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is { } link
            && link.StartsWith(GrimList.Application.BattleQr.LinkPrefix, StringComparison.OrdinalIgnoreCase)) BattleQrInbox.Receive(link);
    }
#pragma warning disable CS0672, CS0618 // Compatibility callback for Android's Storage Access Framework.
    protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == MobileDocumentSaver.RequestCode) MobileDocumentSaver.Complete(resultCode, data);
    }
#pragma warning restore CS0672, CS0618
}

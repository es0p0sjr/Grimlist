using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed class App(IServiceProvider services) : Microsoft.Maui.Controls.Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        UserAppTheme = AppTheme.Dark;
        var page = services.GetRequiredService<MainPage>();
        BattleQrInbox.Received += () => MainThread.BeginInvokeOnMainThread(async () => await page.HandleIncomingBattleQr());
        var window = new Window(new NavigationPage(page)
        { BarBackgroundColor = Ui.Background, BarTextColor = Microsoft.Maui.Graphics.Colors.White });
        window.Activated += async (_, _) => await page.HandleIncomingBattleQr();
        return window;
    }
}

using GrimList.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Controls.Hosting;
using IFilePicker = GrimList.Platform.IFilePicker;

namespace GrimList.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if PERFORMANCE_CHECKS
        MainPage.PerformanceClock.Start();
#endif
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<IPlatformPaths, MobilePaths>();
        builder.Services.AddSingleton<ISqliteConnectionFactory, AndroidSqliteConnectionFactory>();
        builder.Services.AddSingleton<IFilePicker, MobileFilePicker>();
        builder.Services.AddSingleton<IFileSharing, MobileFileSharing>();
        builder.Services.AddSingleton<IClipboardService, MobileClipboard>();
        builder.Services.AddSingleton<IMobileDocumentSaver, MobileDocumentSaver>();
        builder.Services.AddSingleton<IPdfTextExtractor, AndroidPdfTextExtractor>();
        builder.Services.AddSingleton<IRosterPdfExporter, AndroidRosterPdf>();
        builder.Services.AddSingleton<MobileWorkspace>();
        builder.Services.AddTransient<MainPage>();
        return builder.Build();
    }
}

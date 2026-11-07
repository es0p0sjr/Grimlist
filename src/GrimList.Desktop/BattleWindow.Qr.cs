using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrimList.Application;

namespace GrimList.Desktop;

public sealed partial class BattleWindow
{
    private void AddQr(BattleExchangePacket packet)
    {
        var matrix = BattleQr.Matrix(BattleQr.Link(packet)); var size = matrix.GetLength(0); const int scale = 5;
        var pixels = (size + 8) * scale; var data = Enumerable.Repeat((byte)255, pixels * pixels).ToArray();
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (matrix[y, x])
            for (var dy = 0; dy < scale; dy++) for (var dx = 0; dx < scale; dx++)
                data[((y + 4) * scale + dy) * pixels + (x + 4) * scale + dx] = 0;
        var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Gray8, null, data, pixels); bitmap.Freeze();
        var image = new Image { Source = bitmap, Width = pixels, Height = pixels, MaxWidth = 560, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        System.Windows.Automation.AutomationProperties.SetAutomationId(image, "exchange-qr");
        body.Children.Add(image);
        body.Children.Add(Text("Scan with your phone camera or QR scanner, then open the GrimList link. No file or internet connection is needed.", 14));
        body.Children.Add(Button("Copy QR link", () => Clipboard.SetText(BattleQr.Link(packet)), "exchange-copy-link"));
    }
}

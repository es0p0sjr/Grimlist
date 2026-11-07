using GrimList.Application;

namespace GrimList.Mobile;

// The system camera/QR scanner opens the custom link. No external service sees
// the payload, and receipt alone never creates a battle or joins an invitation.
internal static class BattleQrInbox
{
    private static string? pending;
    public static bool HasPending => pending != null;
    public static event Action? Received;
    public static void Receive(string link)
    {
        if (link.Length > BattleQr.MaxLinkLength) { pending = "invalid"; }
        else pending = link;
        Received?.Invoke();
    }
    public static string? Take() => Interlocked.Exchange(ref pending, null);
}

public sealed partial class BattlePage
{
    public async Task ReceiveQrAsync()
    {
        if (exchangeBusy) return;
        var link = BattleQrInbox.Take(); if (link == null) return;
        await pendingSave;
        await ExchangeAction(async () => await ReviewExchange(BattleQr.ReadLink(link)));
    }
    private void AddQr(BattleExchangePacket packet)
    {
        var drawable = new QrDrawing(BattleQr.Matrix(BattleQr.Link(packet)));
        body.Add(new GraphicsView { Drawable = drawable, HeightRequest = 340, HorizontalOptions = LayoutOptions.Fill, BackgroundColor = Colors.White, AutomationId = "exchange-qr" });
        body.Add(Ui.Text("Scan with the other phone's camera or QR scanner, then open the GrimList link.", 14));
        body.Add(Ui.Button("Copy QR link", async () => await Clipboard.Default.SetTextAsync(BattleQr.Link(packet)), "exchange-copy-link", Ui.Surface));
    }
    private sealed class QrDrawing(bool[,] matrix) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF area)
        {
            canvas.FillColor = Colors.White; canvas.FillRectangle(area);
            var size = matrix.GetLength(0); var scale = Math.Max(1, MathF.Floor(Math.Min(area.Width, area.Height) / (size + 8)));
            var left = MathF.Floor((area.Width - (size + 8) * scale) / 2); var top = MathF.Floor((area.Height - (size + 8) * scale) / 2);
            canvas.Antialias = false; canvas.FillColor = Colors.Black;
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (matrix[y, x])
                canvas.FillRectangle(left + (x + 4) * scale, top + (y + 4) * scale, scale, scale);
        }
    }
}

using System.Text;

namespace GrimList.Application;

// Byte-mode QR Model 2, medium correction, mask 0. The encoding algorithm is
// adapted from Project Nayuki's MIT-licensed QR Code generator. See
// tools/qr_reference/LICENSE.txt and the independent matrix reference checks.
public static class BattleQr
{
    public const string LinkPrefix = "grimlist://battle/";
    public const int MaxLinkLength = 1800;
    public static string Link(BattleExchangePacket packet)
    {
        var encoded = BattleExchange.Encode(packet)[BattleExchange.Prefix.Length..];
        var link = LinkPrefix + encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (link.Length > MaxLinkLength) throw new InvalidDataException("Shorten the player name before sharing this QR code.");
        return link;
    }
    public static BattleExchangePacket ReadLink(string link)
    {
        if (link.Length > MaxLinkLength || !link.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Scan a GrimList battle QR code.");
        var data = link[LinkPrefix.Length..];
        if (data.Length == 0 || data.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidDataException("This battle QR code is damaged.");
        data = data.Replace('-', '+').Replace('_', '/');
        var packet = BattleExchange.Decode(BattleExchange.Prefix + data.PadRight((data.Length + 3) / 4 * 4, '='));
        if (packet.Version != 3) throw new InvalidDataException("Ask for a new battle QR code from the updated app.");
        return packet;
    }
    public static BattleExchangePacket Read(string text) => text.Trim().StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase)
        ? ReadLink(text.Trim()) : BattleExchange.Decode(text);

    private static readonly int[] Ecc = [-1,10,16,26,18,24,16,18,22,22,26,30,22,22,24,24,28,28,26,26,26,26,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28];
    private static readonly int[] Blocks = [-1,1,1,1,2,2,4,4,4,5,5,5,8,9,9,10,10,11,13,14,16,17,17,18,20,21,23,25,26,28,29,31,33,35,37,38,40,43,45,47,49];
    private static int Raw(int v)
    {
        var n = (16 * v + 128) * v + 64;
        if (v >= 2) { var a = v / 7 + 2; n -= (25 * a - 10) * a - 55; if (v >= 7) n -= 36; }
        return n / 8;
    }
    // Returns [row, column], without the four-module quiet border added by views.
    public static bool[,] Matrix(string text)
    {
        if (text.Length > MaxLinkLength || text.Any(c => c > 127)) throw new InvalidDataException("QR payload must be a bounded ASCII link.");
        var bytes = Encoding.ASCII.GetBytes(text); var v = 1;
        int Capacity() => Raw(v) - Ecc[v] * Blocks[v];
        while (v < 40 && 4 + (v < 10 ? 8 : 16) + bytes.Length * 8 > Capacity() * 8) v++;
        if (4 + (v < 10 ? 8 : 16) + bytes.Length * 8 > Capacity() * 8) throw new InvalidDataException("QR payload too large.");
        var bits = new List<bool>();
        void Append(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(4, 4); Append(bytes.Length, v < 10 ? 8 : 16); foreach (var b in bytes) Append(b, 8);
        Append(0, Math.Min(4, Capacity() * 8 - bits.Count)); while (bits.Count % 8 != 0) bits.Add(false);
        for (var pad = 0; bits.Count < Capacity() * 8; pad++) Append(pad % 2 == 0 ? 0xEC : 0x11, 8);
        var data = new byte[Capacity()];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) data[i / 8] |= (byte)(1 << (7 - i % 8));
        var degree = Ecc[v]; var divisor = new byte[degree]; divisor[^1] = 1; byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++) { divisor[j] = Multiply(divisor[j], root); if (j + 1 < degree) divisor[j] ^= divisor[j + 1]; }
            root = Multiply(root, 2);
        }
        var shortCount = Blocks[v] - Raw(v) % Blocks[v]; var shortLength = Raw(v) / Blocks[v];
        var blocks = new List<byte[]>(); var offset = 0;
        for (var block = 0; block < Blocks[v]; block++)
        {
            var length = shortLength - degree + (block < shortCount ? 0 : 1);
            var part = data.AsSpan(offset, length).ToArray(); offset += length; var remainder = new byte[degree];
            foreach (var b in part)
            {
                var factor = (byte)(b ^ remainder[0]); Array.Copy(remainder, 1, remainder, 0, degree - 1); remainder[^1] = 0;
                for (var j = 0; j < degree; j++) remainder[j] ^= Multiply(divisor[j], factor);
            }
            blocks.Add(part.Concat(block < shortCount ? new byte[1] : []).Concat(remainder).ToArray());
        }
        var codewords = new List<byte>();
        for (var i = 0; i < blocks[0].Length; i++)
            for (var j = 0; j < blocks.Count; j++) if (i != shortLength - degree || j >= shortCount) codewords.Add(blocks[j][i]);
        var size = v * 4 + 17; var modules = new bool[size, size]; var function = new bool[size, size];
        void Set(int x, int y, bool dark) { modules[y, x] = dark; function[y, x] = true; }
        for (var i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        void Finder(int x, int y)
        {
            for (var dy = -4; dy <= 4; dy++) for (var dx = -4; dx <= 4; dx++)
                if (x + dx >= 0 && x + dx < size && y + dy >= 0 && y + dy < size)
                    Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) is not (2 or 4));
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        if (v > 1)
        {
            var count = v / 7 + 2; var step = (v * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var positions = new int[count]; positions[0] = 6;
            for (var i = count - 1; i >= 1; i--) positions[i] = size - 7 - (count - 1 - i) * step;
            for (var i = 0; i < count; i++) for (var j = 0; j < count; j++)
            {
                if (i == 0 && j == 0 || i == 0 && j == count - 1 || i == count - 1 && j == 0) continue;
                for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
                    Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
        // Medium correction's format bits are 00, and this encoder fixes mask 0.
        const int format = 0x5412;
        bool Bit(int value, int index) => ((value >> index) & 1) != 0;
        for (var i = 0; i < 6; i++) Set(8, i, Bit(format, i));
        Set(8, 7, Bit(format, 6)); Set(8, 8, Bit(format, 7)); Set(7, 8, Bit(format, 8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(format, i));
        for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(format, i));
        for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(format, i));
        Set(8, size - 8, true);
        if (v >= 7)
        {
            var rem = v; for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var versionBits = v << 12 | rem;
            for (var i = 0; i < 18; i++) { var a = size - 11 + i % 3; var b = i / 3; Set(a, b, Bit(versionBits, i)); Set(b, a, Bit(versionBits, i)); }
        }
        var bitIndex = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < size; vertical++) for (var j = 0; j < 2; j++)
            {
                var x = right - j; var y = ((right + 1) & 2) == 0 ? size - 1 - vertical : vertical;
                if (!function[y, x] && bitIndex < codewords.Count * 8)
                { modules[y, x] = Bit(codewords[bitIndex / 8], 7 - bitIndex % 8); bitIndex++; }
            }
        }
        if (bitIndex != codewords.Count * 8) throw new InvalidOperationException("QR codeword placement failed.");
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (!function[y, x] && (x + y) % 2 == 0) modules[y, x] = !modules[y, x];
        return modules;
    }
    private static byte Multiply(int x, int y)
    {
        var z = 0; for (var i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; } return (byte)z;
    }
}

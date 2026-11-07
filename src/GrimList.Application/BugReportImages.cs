using System.Buffers.Binary;
using System.Text;

namespace GrimList.Application;

public static class BugReportImages
{
    public static async Task<byte[]> ReadSelectedAsync(Stream stream, CancellationToken cancellation = default)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + read > BugReportService.MaximumImageBytes) throw new InvalidDataException("This image exceeds 10 MB. Choose a smaller image, or send the report without it.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    // Platform decoders convert explicitly selected PNG/JPEG files to PNG. Keep only
    // image chunks; discard EXIF, text, timestamps, profiles and original filenames.
    public static byte[] StripMetadata(byte[] png)
    {
        if (png.Length > BugReportService.MaximumImageBytes || png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Select a valid PNG or JPEG image. Images are optional.");
        using var output = new MemoryStream(); output.Write(png, 0, 8);
        var offset = 8; var header = false; var pixels = false;
        while (offset + 12 <= png.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (size > png.Length - offset - 12) break;
            var length = (int)size; var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (!header && type != "IHDR") break;
            if (type == "IHDR")
            {
                if (header || length != 13) break;
                var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8, 4));
                var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 12, 4));
                if (width == 0 || height == 0 || (ulong)width * height > 40_000_000) throw new InvalidDataException("Choose an image smaller than 40 megapixels.");
                header = true;
            }
            if (type is "IHDR" or "PLTE" or "IDAT" or "IEND" or "tRNS") output.Write(png, offset, length + 12);
            if (type == "IDAT") pixels = true;
            if (type == "IEND" && length == 0 && pixels) return output.ToArray();
            offset += length + 12;
        }
        throw new InvalidDataException("This image is incomplete. Choose another image, or send the report without it.");
    }
}

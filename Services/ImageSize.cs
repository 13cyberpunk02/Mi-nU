using System.Buffers.Binary;

namespace Mi_nU.Services
{
    /// <summary>
    /// A picture's size in pixels, read from its header rather than by decoding it. Covers what the photo picker
    /// produces, JPEG and PNG; anything else gives null and the chat lays it out as a square.
    /// </summary>
    static class ImageSize
    {
        public static (int Width, int Height)? Of(ReadOnlySpan<byte> bytes)
        {
            // PNG: the IHDR chunk right after the signature holds width and height.
            if (bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature))
                return (BinaryPrimitives.ReadInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..]));

            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                return null;

            // JPEG: walk the segments to the frame header (SOF0–SOF15, except the markers that aren't frames).
            var position = 2;
            while (position + 9 < bytes.Length)
            {
                if (bytes[position] != 0xFF)
                    return null;
                var marker = bytes[position + 1];
                if (marker == 0xFF)
                {
                    position++;
                    continue;
                }
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    int height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]);
                    int width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 7)..]);
                    return (width, height);
                }
                position += 2 + length;
            }
            return null;
        }

        static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    }
}

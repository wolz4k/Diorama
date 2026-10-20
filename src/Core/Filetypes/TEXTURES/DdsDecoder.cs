using System;

namespace Diorama.Core.Filetypes.TEXTURES
{
    // A copy of Flux's Knowledge/DdsDecoder.cs (decoding only): the character preview composites a character's
    // decal textures over its tint colours on the CPU, since the game's shader isn't reproduced here.
    /// <summary>A decoded image: RGBA, 8 bits per channel, rows top to bottom.</summary>
    public record DecodedImage(int Width, int Height, byte[] Rgba);

    /// <summary>
    /// Decodes the DDS textures the game ships as .TEX files (DXT1/DXT5, which is all the character textures use,
    /// plus uncompressed 32-bit) far enough to show a preview. Picks the largest mip level that fits
    /// <c>maxSize</c>, so thumbnails decode only a fraction of the data.
    /// </summary>
    public static class DdsDecoder
    {
        public static DecodedImage? Decode(byte[] data, int maxSize = 4096)
        {
            if (data.Length < 128 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ') return null;
            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(data, 28));
            int pfFlags = BitConverter.ToInt32(data, 80);
            string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
            int bitCount = BitConverter.ToInt32(data, 88);
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

            int blockBytes;
            if (fourCC == "DXT1") blockBytes = 8;
            else if (fourCC == "DXT5" || fourCC == "DXT4") blockBytes = 16;
            else if ((pfFlags & 0x40) != 0 && bitCount == 32) blockBytes = 0; // uncompressed RGB(A)
            else return null;

            // Skip mips larger than maxSize.
            int offset = 128, w = width, h = height;
            for (int level = 0; level < mips - 1 && (w > maxSize || h > maxSize); level++)
            {
                offset += LevelSize(w, h, blockBytes);
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            if (offset + LevelSize(w, h, blockBytes) > data.Length) return null;

            var rgba = new byte[w * h * 4];
            if (blockBytes == 0) DecodeUncompressed(data, offset, w, h, data, rgba);
            else DecodeBlocks(data, offset, w, h, blockBytes == 16, rgba);
            return new DecodedImage(w, h, rgba);
        }

        private static int LevelSize(int w, int h, int blockBytes) =>
            blockBytes == 0 ? w * h * 4 : Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;

        private static void DecodeUncompressed(byte[] data, int offset, int w, int h, byte[] header, byte[] rgba)
        {
            uint rMask = BitConverter.ToUInt32(header, 92), gMask = BitConverter.ToUInt32(header, 96), bMask = BitConverter.ToUInt32(header, 100), aMask = BitConverter.ToUInt32(header, 104);
            for (int i = 0; i < w * h; i++)
            {
                uint px = BitConverter.ToUInt32(data, offset + i * 4);
                rgba[i * 4] = Channel(px, rMask);
                rgba[i * 4 + 1] = Channel(px, gMask);
                rgba[i * 4 + 2] = Channel(px, bMask);
                rgba[i * 4 + 3] = aMask == 0 ? (byte)255 : Channel(px, aMask);
            }
        }

        private static byte Channel(uint px, uint mask)
        {
            if (mask == 0) return 0;
            int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
            uint max = mask >> shift;
            return (byte)(((px & mask) >> shift) * 255 / max);
        }

        private static void DecodeBlocks(byte[] data, int offset, int w, int h, bool dxt5, byte[] rgba)
        {
            int blocksX = Math.Max(1, (w + 3) / 4), blocksY = Math.Max(1, (h + 3) / 4);
            Span<byte> colours = stackalloc byte[16];
            Span<byte> alphas = stackalloc byte[8];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int block = offset + (by * blocksX + bx) * (dxt5 ? 16 : 8);
                    ulong alphaBits = 0;
                    if (dxt5)
                    {
                        byte a0 = data[block], a1 = data[block + 1];
                        alphas[0] = a0; alphas[1] = a1;
                        if (a0 > a1)
                            for (int i = 1; i < 7; i++) alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        else
                        {
                            for (int i = 1; i < 5; i++) alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                            alphas[6] = 0; alphas[7] = 255;
                        }
                        for (int i = 0; i < 6; i++) alphaBits |= (ulong)data[block + 2 + i] << (8 * i);
                        block += 8;
                    }

                    ushort c0 = BitConverter.ToUInt16(data, block), c1 = BitConverter.ToUInt16(data, block + 2);
                    Rgb565(c0, colours, 0);
                    Rgb565(c1, colours, 4);
                    bool fourColour = dxt5 || c0 > c1;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        if (fourColour)
                        {
                            colours[8 + ch] = (byte)((2 * colours[ch] + colours[4 + ch]) / 3);
                            colours[12 + ch] = (byte)((colours[ch] + 2 * colours[4 + ch]) / 3);
                        }
                        else
                        {
                            colours[8 + ch] = (byte)((colours[ch] + colours[4 + ch]) / 2);
                            colours[12 + ch] = 0;
                        }
                    }
                    colours[3] = colours[7] = colours[11] = 255;
                    colours[15] = (byte)(fourColour ? 255 : 0);

                    uint indices = BitConverter.ToUInt32(data, block + 4);
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= h) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= w) continue;
                            int i = py * 4 + px;
                            int ci = (int)(indices >> (2 * i)) & 3;
                            int o = (y * w + x) * 4;
                            rgba[o] = colours[ci * 4];
                            rgba[o + 1] = colours[ci * 4 + 1];
                            rgba[o + 2] = colours[ci * 4 + 2];
                            rgba[o + 3] = dxt5 ? alphas[(int)(alphaBits >> (3 * i)) & 7] : colours[ci * 4 + 3];
                        }
                    }
                }
            }
        }

        private static void Rgb565(ushort c, Span<byte> into, int at)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            into[at] = (byte)((r << 3) | (r >> 2));
            into[at + 1] = (byte)((g << 2) | (g >> 4));
            into[at + 2] = (byte)((b << 3) | (b >> 2));
        }
    }
}

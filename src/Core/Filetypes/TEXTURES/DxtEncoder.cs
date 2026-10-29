using System;

namespace Diorama.Core.Filetypes.TEXTURES
{
    /// <summary>
    /// Compresses pictures to DXT1 with 1-bit alpha or DXT5 (most character textures), the same encoder as Flux's. Only
    /// used for pictures a modder brings (the texture an imported OBJ names); the game's own textures are never
    /// re-compressed. Each block's two colours are the ends of the line through its pixels' colours (their principal
    /// axis), which is what keeps faces and edges clean.
    /// </summary>
    public static class DxtEncoder
    {
        /// <summary>A DXT1 block of transparent pixels (three-colour mode, every index 3).</summary>
        public static readonly byte[] TransparentBlock = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>Encodes an image whose size is a multiple of 4 into DXT1 blocks, row after row. Pixels with alpha below 128 become transparent.</summary>
        public static byte[] EncodeDxt1(DecodedImage image)
        {
            if (image.Width % 4 != 0 || image.Height % 4 != 0) throw new ArgumentException("The image's size must be a multiple of 4.");
            int bw = image.Width / 4, bh = image.Height / 4;
            var output = new byte[bw * bh * 8];
            var block = new int[16 * 3];
            var opaque = new bool[16];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int o = ((by * 4 + i / 4) * image.Width + bx * 4 + i % 4) * 4;
                        block[i * 3] = image.Rgba[o];
                        block[i * 3 + 1] = image.Rgba[o + 1];
                        block[i * 3 + 2] = image.Rgba[o + 2];
                        opaque[i] = image.Rgba[o + 3] >= 128;
                    }
                    EncodeBlock(block, opaque, output, (by * bw + bx) * 8);
                }
            return output;
        }

        /// <summary>
        /// Encodes an image whose size is a multiple of 4 into DXT5 blocks (an 8-byte alpha block, then a four-colour
        /// DXT1 block), row after row: smooth alpha, as most of the game's character textures use.
        /// </summary>
        public static byte[] EncodeDxt5(DecodedImage image)
        {
            if (image.Width % 4 != 0 || image.Height % 4 != 0) throw new ArgumentException("The image's size must be a multiple of 4.");
            int bw = image.Width / 4, bh = image.Height / 4;
            var output = new byte[bw * bh * 16];
            var block = new int[16 * 3];
            var alpha = new int[16];
            var opaque = new bool[16];
            Array.Fill(opaque, true);
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int o = ((by * 4 + i / 4) * image.Width + bx * 4 + i % 4) * 4;
                        block[i * 3] = image.Rgba[o];
                        block[i * 3 + 1] = image.Rgba[o + 1];
                        block[i * 3 + 2] = image.Rgba[o + 2];
                        alpha[i] = image.Rgba[o + 3];
                    }
                    int at = (by * bw + bx) * 16;
                    EncodeAlphaBlock(alpha, output, at);
                    EncodeBlock(block, opaque, output, at + 8);
                }
            return output;
        }

        /// <summary>A DXT5 alpha block: the block's highest and lowest alpha, then a 3-bit index per pixel into the eight between.</summary>
        private static void EncodeAlphaBlock(int[] alpha, byte[] output, int at)
        {
            int a0 = 0, a1 = 255;
            foreach (int a in alpha) { a0 = Math.Max(a0, a); a1 = Math.Min(a1, a); }
            output[at] = (byte)a0;
            output[at + 1] = (byte)a1;
            if (a0 == a1)
            {
                // All one value: index 0 everywhere.
                for (int k = 2; k < 8; k++) output[at + k] = 0;
                return;
            }
            // a0 > a1: eight steps, index 0 = a0, 1 = a1, 2..7 = between from a0 towards a1.
            Span<int> levels = stackalloc int[8];
            levels[0] = a0;
            levels[1] = a1;
            for (int k = 1; k <= 6; k++) levels[k + 1] = ((7 - k) * a0 + k * a1) / 7;
            ulong bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestDistance = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = Math.Abs(alpha[i] - levels[k]);
                    if (d < bestDistance) { bestDistance = d; best = k; }
                }
                bits |= (ulong)best << (3 * i);
            }
            for (int k = 0; k < 6; k++) output[at + 2 + k] = (byte)(bits >> (8 * k));
        }

        private static void EncodeBlock(int[] px, bool[] opaque, byte[] output, int at)
        {
            int count = 0;
            double mr = 0, mg = 0, mb = 0;
            for (int i = 0; i < 16; i++)
                if (opaque[i]) { count++; mr += px[i * 3]; mg += px[i * 3 + 1]; mb += px[i * 3 + 2]; }
            if (count == 0)
            {
                TransparentBlock.CopyTo(output, at);
                return;
            }
            mr /= count; mg /= count; mb /= count;

            // Principal axis of the colours (power iteration on their covariance).
            double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!opaque[i]) continue;
                double r = px[i * 3] - mr, g = px[i * 3 + 1] - mg, b = px[i * 3 + 2] - mb;
                crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
            }
            double ar = 1, ag = 1, ab = 1;
            for (int k = 0; k < 8; k++)
            {
                double nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
                double len = Math.Sqrt(nr * nr + ng * ng + nb * nb);
                if (len < 1e-9) break;
                ar = nr / len; ag = ng / len; ab = nb / len;
            }
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 16; i++)
            {
                if (!opaque[i]) continue;
                double t = (px[i * 3] - mr) * ar + (px[i * 3 + 1] - mg) * ag + (px[i * 3 + 2] - mb) * ab;
                min = Math.Min(min, t);
                max = Math.Max(max, t);
            }
            ushort c0 = To565(mr + ar * max, mg + ag * max, mb + ab * max);
            ushort c1 = To565(mr + ar * min, mg + ag * min, mb + ab * min);

            bool transparent = count < 16;
            // Four-colour blocks need c0 > c1, three-colour (with transparency) c0 <= c1.
            if (transparent ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
            if (!transparent && c0 == c1)
            {
                // A flat colour: any index 0 gives it.
                Write(output, at, c0, c1, 0);
                return;
            }

            Span<int> palette = stackalloc int[12];
            From565(c0, palette, 0);
            From565(c1, palette, 3);
            int colours = transparent ? 3 : 4;
            for (int ch = 0; ch < 3; ch++)
            {
                if (transparent) palette[6 + ch] = (palette[ch] + palette[3 + ch]) / 2;
                else
                {
                    palette[6 + ch] = (2 * palette[ch] + palette[3 + ch]) / 3;
                    palette[9 + ch] = (palette[ch] + 2 * palette[3 + ch]) / 3;
                }
            }
            uint indices = 0;
            for (int i = 0; i < 16; i++)
            {
                int best = 3;
                if (opaque[i])
                {
                    int bestDistance = int.MaxValue;
                    for (int c = 0; c < colours; c++)
                    {
                        int dr = px[i * 3] - palette[c * 3], dg = px[i * 3 + 1] - palette[c * 3 + 1], db = px[i * 3 + 2] - palette[c * 3 + 2];
                        int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                        if (d < bestDistance) { bestDistance = d; best = c; }
                    }
                }
                indices |= (uint)best << (2 * i);
            }
            Write(output, at, c0, c1, indices);
        }

        private static void Write(byte[] output, int at, ushort c0, ushort c1, uint indices)
        {
            BitConverter.TryWriteBytes(output.AsSpan(at), c0);
            BitConverter.TryWriteBytes(output.AsSpan(at + 2), c1);
            BitConverter.TryWriteBytes(output.AsSpan(at + 4), indices);
        }

        private static ushort To565(double r, double g, double b)
        {
            int R = Math.Clamp((int)Math.Round(r * 31 / 255), 0, 31), G = Math.Clamp((int)Math.Round(g * 63 / 255), 0, 63), B = Math.Clamp((int)Math.Round(b * 31 / 255), 0, 31);
            return (ushort)(R << 11 | G << 5 | B);
        }

        private static void From565(ushort c, Span<int> into, int at)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            into[at] = (r << 3) | (r >> 2);
            into[at + 1] = (g << 2) | (g >> 4);
            into[at + 2] = (b << 3) | (b >> 2);
        }
    }
}

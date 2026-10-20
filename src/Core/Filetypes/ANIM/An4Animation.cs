using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Diorama.Core.Filetypes.ANIM
{
    /// <summary>
    /// Reads a pose out of an .AN4 animation. The file is "Deflate_v1.0", 16 zero bytes and the unpacked size (u32 LE at
    /// 0x20), then deflate whose block type codes are swapped (2 stored, with a 2-byte length only; 1 fixed; 0 dynamic).
    /// Unpacked, it's big-endian, with one header ("ANIE"…) per sub-animation (the character's own skeleton first, then
    /// attachments'). A header holds per node (joint, in the model's order) NumCurves curves: 3 translations, then 3
    /// Euler rotations (Z·Y·X), then scales when there are 9. Each curve has a key type: 14 is 0, 15 is 1, 6/7/11 are
    /// animated (8-bit, 16-bit or float keys in groups of 4 frames, scaled by a (scale, min) pair), anything else is a
    /// constant, the type × ConstantScale + ConstantBase. Worked out from the game's evaluator (LDC exe 0x14013c400).
    /// </summary>
    public static class An4Animation
    {
        /// <summary>Key bytes per group and (scale, min) pairs used, by key type, as the game's tables give them.</summary>
        static readonly byte[] KeyBytes = { 3, 4, 0, 3, 4, 3, 4, 8, 4, 8, 4, 16, 0, 0 };
        static readonly byte[] ScaleMins = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0 };

        /// <summary>One joint's local transform: translation and rotation (and scale, 1 unless keyed).</summary>
        public record struct JointPose(Vector3 Translation, Quaternion Rotation, Vector3 Scale);

        /// <summary>
        /// The pose at <paramref name="frame"/> of the first sub-animation over <paramref name="joints"/> joints, or null when
        /// there's none (or the file can't be read). Only whole frames are sampled.
        /// </summary>
        public static List<JointPose>? Pose(string path, int joints, int frame = 0)
        {
            byte[] data;
            try { data = Unpack(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
            for (int h = Find(data, 0); h >= 0; h = Find(data, h + 4))
                if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 4)) == joints)
                {
                    try { return Evaluate(data, h, frame); }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
                }
            return null;
        }

        /// <summary>
        /// The pose the animation sways about: each joint's translation, rotation and scale averaged over every frame of the
        /// first sub-animation over <paramref name="joints"/> joints (a cape's idle swings side to side; its first frame
        /// alone catches it mid-swing). Null as <see cref="Pose"/>.
        /// </summary>
        public static List<JointPose>? AveragePose(string path, int joints)
        {
            byte[] data;
            try { data = Unpack(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
            for (int h = Find(data, 0); h >= 0; h = Find(data, h + 4))
                if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 4)) == joints)
                {
                    try
                    {
                        int frames = Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 6)));
                        var sum = Evaluate(data, h, 0);
                        var rotations = sum.Select(p => new Vector4(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W)).ToArray();
                        var translations = sum.Select(p => p.Translation).ToArray();
                        var scales = sum.Select(p => p.Scale).ToArray();
                        for (int f = 1; f < frames; f++)
                        {
                            var pose = Evaluate(data, h, f);
                            for (int j = 0; j < pose.Count; j++)
                            {
                                var q = new Vector4(pose[j].Rotation.X, pose[j].Rotation.Y, pose[j].Rotation.Z, pose[j].Rotation.W);
                                rotations[j] += Vector4.Dot(q, rotations[j]) < 0 ? -q : q;
                                translations[j] += pose[j].Translation;
                                scales[j] += pose[j].Scale;
                            }
                        }
                        return Enumerable.Range(0, sum.Count).Select(j =>
                        {
                            var r = Vector4.Normalize(rotations[j]);
                            return new JointPose(translations[j] / frames, new Quaternion(r.X, r.Y, r.Z, r.W), scales[j] / frames);
                        }).ToList();
                    }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
                }
            return null;
        }

        static int Find(byte[] data, int from)
        {
            for (int i = from; i + 0x50 <= data.Length; i++)
                if (data[i] == 'A' && data[i + 1] == 'N' && data[i + 2] == 'I' && data[i + 3] is (byte)'D' or (byte)'E' or (byte)'F')
                    return i;
            return -1;
        }

        static List<JointPose> Evaluate(byte[] d, int h, int frame)
        {
            ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
            int Off(int field) => h + BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(h + field));
            float F32(int at) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(at));

            int nodes = U16(h + 4), frames = U16(h + 6), groupSize = U16(h + 8), curves = U16(h + 0xc);
            float constBase = F32(h + 0x1c), constScale = F32(h + 0x20);
            int scaleMins = Off(0x24), keyTypes = Off(0x2c), keys = Off(0x30);
            frame = Math.Clamp(frame, 0, Math.Max(frames - 1, 0));
            int group = frame >> 2, sub = frame & 3;

            // The keys are held as big-endian 32-bit words: each word's bytes reversed, then read little-endian.
            byte[] Word(int at)
            {
                var w = new byte[4];
                for (int i = 0; i < 4; i++) w[i] = at + 3 - i < d.Length ? d[at + 3 - i] : (byte)0;
                return w;
            }
            uint W32(int at) => BitConverter.ToUInt32(Word(at), 0);
            ushort W16(int at) => BitConverter.ToUInt16(Word(at & ~3), at & 3);
            float WF(int at) => BitConverter.ToSingle(Word(at), 0);

            var poses = new List<JointPose>(nodes);
            for (int n = 0; n < nodes; n++)
            {
                // A node's data starts past the earlier nodes', every key type under 14 counted by the tables.
                int kp = keys + group * groupSize, sm = scaleMins;
                for (int m = 0; m < n * curves; m++)
                {
                    int t0 = U16(keyTypes + 2 * m) & 0x7fff;
                    if (t0 < 14) { kp += KeyBytes[t0]; sm += 8 * ScaleMins[t0]; }
                }
                var v = new float[Math.Max(curves, 9)];
                v[6] = v[7] = v[8] = 1;
                for (int c = 0; c < curves; c++)
                {
                    int t = U16(keyTypes + 2 * (n * curves + c)) & 0x7fff;
                    float value;
                    switch (t)
                    {
                        case 14: value = 0; break;
                        case 15: value = 1; break;
                        case 6:
                        {
                            uint w = W32(kp), next = W32(kp + groupSize);
                            int a = (int)(w & 0xff), b = (int)(next & 0xff), k = (int)((w >> 8) >> (6 * sub)) & 0x3f;
                            value = (k / 63f * (b - a) + a) * F32(sm) + F32(sm + 4);
                            kp += 4; sm += 8;
                            break;
                        }
                        case 7:
                        {
                            int a = W16(kp), b = W16(kp + groupSize), frac = sub switch { 0 => W16(kp + 2) & 0xfff, 1 => W16(kp + 4) & 0xfff, 2 => W16(kp + 6) & 0xfff, _ => 4095 };
                            value = (a + (b - a) * frac / 4095f) * F32(sm) + F32(sm + 4);
                            kp += 8; sm += 8;
                            break;
                        }
                        case 11:
                            value = WF(kp + 4 * sub);
                            kp += 16;
                            break;
                        default:
                            value = t * constScale + constBase;
                            break;
                    }
                    if (c < v.Length) v[c] = value;
                }
                poses.Add(new JointPose(new Vector3(v[0], v[1], v[2]), EulerZYX(v[3], v[4], v[5]), new Vector3(v[6], v[7], v[8])));
            }
            return poses;
        }

        /// <summary>The game's Euler rotation: X first, then Y, then Z.</summary>
        public static Quaternion EulerZYX(float x, float y, float z)
        {
            float sx = MathF.Sin(x / 2), cx = MathF.Cos(x / 2), sy = MathF.Sin(y / 2), cy = MathF.Cos(y / 2), sz = MathF.Sin(z / 2), cz = MathF.Cos(z / 2);
            return new Quaternion(sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz);
        }

        /// <summary>The unpacked animation: TT's "Deflate_v1.0" wrapper around deflate with swapped block type codes.</summary>
        public static byte[] Unpack(byte[] file)
        {
            if (file.Length < 0x24 || System.Text.Encoding.ASCII.GetString(file, 0, 12) != "Deflate_v1.0")
                throw new InvalidDataException("Not a Deflate_v1.0 file.");
            int size = BitConverter.ToInt32(file, 0x20);
            return Inflate(file.AsSpan(0x24), size);
        }

        static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        ref struct Bits
        {
            readonly ReadOnlySpan<byte> data;
            public int Pos;
            uint buffer;
            int count;
            public Bits(ReadOnlySpan<byte> data) { this.data = data; Pos = 0; buffer = 0; count = 0; }
            public int Get(int n)
            {
                while (count < n) { buffer |= (uint)(Pos < data.Length ? data[Pos] : 0) << count; Pos++; count += 8; }
                int v = (int)(buffer & ((1u << n) - 1));
                buffer >>= n; count -= n;
                return v;
            }
            public void Align() { buffer = 0; count = 0; }
            public ReadOnlySpan<byte> Data => data;
        }

        /// <summary>Canonical Huffman decoding table: counts per length and symbols in code order.</summary>
        sealed class Huffman
        {
            public readonly int[] Counts = new int[16];
            public readonly int[] Symbols;
            public Huffman(ReadOnlySpan<int> lengths)
            {
                Symbols = new int[lengths.Length];
                foreach (int l in lengths) Counts[l]++;
                Counts[0] = 0;
                var offsets = new int[16];
                for (int i = 1; i < 16; i++) offsets[i] = offsets[i - 1] + Counts[i - 1];
                for (int s = 0; s < lengths.Length; s++)
                    if (lengths[s] != 0) Symbols[offsets[lengths[s]]++] = s;
            }

            public int Decode(ref Bits bits)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= bits.Get(1);
                    int count = Counts[len];
                    if (code - count < first) return Symbols[index + (code - first)];
                    index += count; first += count; first <<= 1; code <<= 1;
                }
                throw new InvalidDataException("Bad Huffman code.");
            }
        }

        static byte[] Inflate(ReadOnlySpan<byte> input, int size)
        {
            var output = new byte[size];
            int o = 0;
            var bits = new Bits(input);
            Span<int> fixedLengths = stackalloc int[288];
            for (int i = 0; i < 288; i++) fixedLengths[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
            var fixedLit = new Huffman(fixedLengths);
            Span<int> five = stackalloc int[30];
            five.Fill(5);
            var fixedDist = new Huffman(five);
            while (true)
            {
                int final = bits.Get(1), type = bits.Get(2);
                if (type == 2)
                {
                    // stored: TT's code 2, a 2-byte length with no complement
                    bits.Align();
                    int len = bits.Data[bits.Pos] | bits.Data[bits.Pos + 1] << 8;
                    bits.Pos += 2;
                    bits.Data.Slice(bits.Pos, len).CopyTo(output.AsSpan(o));
                    o += len; bits.Pos += len;
                }
                else
                {
                    Huffman lit, dist;
                    if (type == 1) { lit = fixedLit; dist = fixedDist; }
                    else if (type == 0)
                    {
                        int hlit = bits.Get(5) + 257, hdist = bits.Get(5) + 1, hclen = bits.Get(4) + 4;
                        var cl = new int[19];
                        for (int i = 0; i < hclen; i++) cl[CodeLengthOrder[i]] = bits.Get(3);
                        var clh = new Huffman(cl);
                        var lens = new int[hlit + hdist];
                        for (int i = 0; i < lens.Length;)
                        {
                            int s = clh.Decode(ref bits);
                            if (s < 16) lens[i++] = s;
                            else if (s == 16) { int r = 3 + bits.Get(2), prev = lens[i - 1]; while (r-- > 0) lens[i++] = prev; }
                            else if (s == 17) { int r = 3 + bits.Get(3); while (r-- > 0) lens[i++] = 0; }
                            else { int r = 11 + bits.Get(7); while (r-- > 0) lens[i++] = 0; }
                        }
                        lit = new Huffman(lens.AsSpan(0, hlit));
                        dist = new Huffman(lens.AsSpan(hlit));
                    }
                    else throw new InvalidDataException($"Block type {type}.");
                    while (true)
                    {
                        int s = lit.Decode(ref bits);
                        if (s < 256) output[o++] = (byte)s;
                        else if (s == 256) break;
                        else
                        {
                            s -= 257;
                            int len = LengthBase[s] + bits.Get(LengthExtra[s]);
                            int ds = dist.Decode(ref bits);
                            int back = DistBase[ds] + bits.Get(DistExtra[ds]);
                            for (int i = 0; i < len; i++, o++) output[o] = output[o - back];
                        }
                    }
                }
                if (final == 1 || o >= size) return output;
            }
        }
    }
}

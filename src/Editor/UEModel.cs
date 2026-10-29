using Diorama.Core.Filetypes.GSC.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Diorama.Editor
{
    /// <summary>
    /// Reads the most detailed mesh of a .UEMODEL: the UEFormat file FModel exports Unreal Engine models as (Fortnite's
    /// LEGO pieces among them). Unlike an OBJ made from it, it keeps the colour mask Fortnite's materials paint by: each
    /// region (a hat's spikes, its cowl) has its own vertex colour. The colours themselves are in the game's materials,
    /// which aren't exported, so each region gets a placeholder grey to be coloured with Colour by region.
    /// </summary>
    public static class UEModel
    {
        public static bool IsUEModel(string path) => Path.GetExtension(path).Equals(".uemodel", StringComparison.OrdinalIgnoreCase);

        /// <summary>Placeholder colours for the mask's regions, light to dark so they can be told apart.</summary>
        static readonly Vector3[] Placeholders = [new(0.80f), new(0.55f), new(0.35f), new(0.68f), new(0.45f), new(0.25f), new(0.90f), new(0.60f)];

        public static ObjMeshData Read(string path, out List<string> notes)
        {
            notes = new();
            byte[] data = Decompress(File.ReadAllBytes(path), out string name);

            // The chunks (name, byte size of the rest, count, then the data) start with the LODs, most detailed first: read from the
            // first VERTICES to the next.
            int start = FindChunk(data, "VERTICES", 0);
            if (start < 0)
                throw new InvalidDataException("This .uemodel has no mesh (no VERTICES chunk).");

            Vector3[]? positions = null, normals = null;
            Vector2[]? uvs = null;
            int[]? indices = null;
            byte[][]? mask = null;
            string? material = null;

            int at = start;
            bool firstVertices = true;
            while (at + 4 <= data.Length)
            {
                int nameLength = BitConverter.ToInt32(data, at);
                if (nameLength <= 0 || nameLength > 64 || at + 4 + nameLength + 8 > data.Length) break;
                string chunk = Encoding.ASCII.GetString(data, at + 4, nameLength);
                int o = at + 4 + nameLength;
                // the byte size counts the count field too
                int size = BitConverter.ToInt32(data, o) - 4, count = BitConverter.ToInt32(data, o + 4);
                o += 8;
                if (size < 0 || o + size > data.Length) break;

                if (chunk == "VERTICES" && !firstVertices) break; // the next LOD
                switch (chunk)
                {
                    case "VERTICES":
                        firstVertices = false;
                        positions = new Vector3[count];
                        for (int i = 0; i < count; i++)
                            positions[i] = FromUnreal(Float3(data, o + i * 12)) / 100; // centimetres to metres
                        break;
                    case "NORMALS": // w (the binormal's sign), then x, y, z
                        normals = new Vector3[count];
                        for (int i = 0; i < count && size >= count * 16; i++)
                            normals[i] = FromUnreal(Float3(data, o + i * 16 + 4));
                        break;
                    case "TEXCOORDS": // per set: its name, its count, then U and V (V down, as the game has it)
                        {
                            int q = o;
                            q += 4 + BitConverter.ToInt32(data, q);
                            int n = BitConverter.ToInt32(data, q);
                            q += 4;
                            uvs = new Vector2[n];
                            for (int i = 0; i < n; i++)
                                uvs[i] = new Vector2(BitConverter.ToSingle(data, q + i * 8), BitConverter.ToSingle(data, q + i * 8 + 4));
                        }
                        break;
                    case "INDICES":
                        indices = new int[count];
                        Buffer.BlockCopy(data, o, indices, 0, count * 4);
                        break;
                    case "VERTEXCOLORS" when count > 0: // per set: its name, its count, then RGBA bytes
                        {
                            int q = o;
                            q += 4 + BitConverter.ToInt32(data, q);
                            int n = BitConverter.ToInt32(data, q);
                            q += 4;
                            mask = new byte[n][];
                            for (int i = 0; i < n; i++)
                                mask[i] = data[(q + i * 4)..(q + i * 4 + 4)];
                        }
                        break;
                    case "MATERIALS" when count > 0:
                        {
                            int l = BitConverter.ToInt32(data, o);
                            material = Encoding.ASCII.GetString(data, o + 4, l);
                        }
                        break;
                }
                at = o + size;
            }

            if (positions == null || indices == null || indices.Length < 3)
                throw new InvalidDataException("This .uemodel's mesh has no triangles.");

            var obj = new ObjMeshData { HasNormals = normals != null, HasUVs = uvs != null && uvs.Length == positions.Length };
            var regions = mask?.Length == positions.Length ? MaskRegions(mask) : null;
            for (int i = 0; i < positions.Length; i++)
            {
                obj.Vertices.Add(new Vertex
                {
                    Position = positions[i],
                    Normal = normals != null && i < normals.Length ? normals[i] : Vector3.Zero,
                    UVSet01 = obj.HasUVs ? new Vector4(uvs![i], 0, 0) : Vector4.Zero,
                    ColorSet0 = new Vector4(regions != null ? Placeholders[regions[Convert.ToHexString(mask![i])] % Placeholders.Length] : Vector3.One, 1),
                });
                obj.VertexMaterials.Add(material);
            }
            obj.Indices.AddRange(indices);
            int regionCount = regions?.Values.Distinct().Count() ?? 0;
            obj.HasColours = regionCount > 1;

            if (obj.HasColours)
                notes.Add($"{Path.GetFileName(path)} marks {regionCount} colour regions (Fortnite paints each with its own colour, which the export doesn't include), so each got a placeholder grey. Pick their colours with Colour by region.");
            if (material != null && material.Contains("WorldGrid"))
                notes.Add("Its material wasn't exported (it names Unreal's WorldGridMaterial), so it brings no texture either.");
            return obj;
        }

        /// <summary>
        /// Each mask value's region, most used first. Values a few steps apart are one region (the same mask blended
        /// slightly at its edges: 60 and 61 grey).
        /// </summary>
        static Dictionary<string, int> MaskRegions(byte[][] mask)
        {
            var counts = mask.GroupBy(Convert.ToHexString).OrderByDescending(g => g.Count()).Select(g => g.First()).ToList();
            var leaders = new List<byte[]>();
            var result = new Dictionary<string, int>();
            foreach (var value in counts)
            {
                int region = leaders.FindIndex(l => Enumerable.Range(0, 4).All(c => Math.Abs(l[c] - value[c]) <= 4));
                if (region < 0) { region = leaders.Count; leaders.Add(value); }
                result[Convert.ToHexString(value)] = region;
            }
            return result;
        }

        /// <summary>Unreal is Z up and left-handed, in centimetres: to the game's Y up, as Blender's importer and OBJ export do.</summary>
        static Vector3 FromUnreal(Vector3 v) => new(v.X, v.Z, v.Y);

        static Vector3 Float3(byte[] d, int o) => new(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

        static int FindChunk(byte[] data, string name, int from)
        {
            byte[] key = [.. BitConverter.GetBytes(name.Length), .. Encoding.ASCII.GetBytes(name)];
            int i = data.AsSpan(from).IndexOf(key);
            return i < 0 ? -1 : from + i;
        }

        /// <summary>The file after its header, unpacked: "UEFORMAT", the kind, a version, the name, (newer: its asset path), then whether and how it's compressed.</summary>
        static byte[] Decompress(byte[] file, out string name)
        {
            if (file.Length < 16 || Encoding.ASCII.GetString(file, 0, 8) != "UEFORMAT")
                throw new InvalidDataException("This isn't a UEFormat file (it doesn't start with \"UEFORMAT\").");
            int p = 8;
            string Str() { int n = BitConverter.ToInt32(file, p); string s = Encoding.ASCII.GetString(file, p + 4, n); p += 4 + n; return s; }

            string kind = Str();
            if (kind != "UEMODEL")
                throw new InvalidDataException($"This UEFormat file holds a {kind}, not a model.");
            p++; // version
            name = Str();
            // newer files name the asset's path next; it starts with a length, where older ones have the compressed flag
            if (file[p] > 1) Str();
            bool compressed = file[p++] != 0;
            if (!compressed)
                return file[p..];

            string method = Str();
            int unpacked = BitConverter.ToInt32(file, p), packed = BitConverter.ToInt32(file, p + 4);
            p += 8;
            var body = file.AsSpan(p, Math.Min(packed, file.Length - p));
            switch (method.ToUpperInvariant())
            {
                case "ZSTD":
                    using (var zstd = new ZstdSharp.Decompressor())
                        return zstd.Unwrap(body).ToArray();
                case "GZIP":
                    using (var gzip = new GZipStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress))
                    using (var output = new MemoryStream(unpacked))
                    {
                        gzip.CopyTo(output);
                        return output.ToArray();
                    }
                default:
                    throw new InvalidDataException($"This .uemodel is compressed with {method}, which Diorama can't unpack.");
            }
        }
    }
}

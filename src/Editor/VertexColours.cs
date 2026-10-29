using Diorama.Core.Filetypes.GSC.Components;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>A part of a mesh that gets one colour: the vertices sharing a colour, or one separate piece.</summary>
    public sealed record ColourRegion(int Index, int Vertices, Vector3 Colour, string Label);

    public enum RegionSplit { Auto, Colours, Pieces }

    /// <summary>
    /// Vertex colours, which most LEGO parts take their brick colour from: from an OBJ's material colours, split into
    /// regions, and painted into a game mesh.
    /// </summary>
    public static class VertexColours
    {
        /// <summary>
        /// Colours an OBJ that has no vertex colours by its materials' colours (Kd in the .MTL), as LEGO building tools
        /// (Stud.io, Mecabricks, LDraw exports) write one material per brick colour. Needs at least two materials with
        /// different colours: a single grey one is just a default. Returns a note, or null if nothing was done.
        /// </summary>
        public static string? FromMaterials(ObjMeshData obj, string objPath)
        {
            if (obj.HasColours || obj.VertexMaterials.Count != obj.Vertices.Count)
                return null;
            var used = obj.VertexMaterials.Where(m => m != null).Select(m => m!).Distinct().ToList();
            if (used.Count < 2)
                return null;

            var kd = ReadMaterialColours(objPath);
            var colours = used.Where(kd.ContainsKey).ToDictionary(m => m, m => kd[m]);
            if (colours.Values.Select(Hex).Distinct().Count() < 2)
                return null;

            for (int i = 0; i < obj.Vertices.Count; i++)
                obj.Vertices[i].ColorSet0 = new Vector4(obj.VertexMaterials[i] is { } m && colours.TryGetValue(m, out var c) ? c : Vector3.One, 1);
            obj.HasColours = true;

            var listed = colours.Take(8).Select(p => $"{p.Key} #{Hex(p.Value)}");
            return $"The OBJ has no vertex colours, but its {colours.Count} materials have colours (Kd in the .mtl), so each became the colour of its faces: " +
                string.Join(", ", listed) + (colours.Count > 8 ? ", ..." : "") + ". Change any of them with Colour by region.";
        }

        /// <summary>Each material's diffuse colour (Kd) in the OBJ's .MTL files, as sRGB 0-1 (Blender writes it linear).</summary>
        static Dictionary<string, Vector3> ReadMaterialColours(string objPath)
        {
            var result = new Dictionary<string, Vector3>();
            string folder = Path.GetDirectoryName(objPath) ?? "";
            bool blender = false, first = true;
            var libraries = new List<string>();
            foreach (string raw in File.ReadLines(objPath))
            {
                string line = raw.Trim();
                if (first) { blender = line.Contains("Blender", StringComparison.OrdinalIgnoreCase); first = false; }
                if (line.StartsWith("mtllib ")) libraries.Add(line[7..].Trim());
                else if (line.StartsWith("v ")) break; // the libraries come first
            }

            foreach (string library in libraries)
            {
                string path = Path.Combine(folder, library);
                if (!File.Exists(path)) continue;
                string? material = null;
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("newmtl ")) material = line[7..].Trim();
                    else if (material != null && line.StartsWith("Kd "))
                    {
                        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 4 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                            && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                            && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                        {
                            var c = Vector3.Clamp(new Vector3(r, g, b), Vector3.Zero, Vector3.One);
                            result[material] = blender ? new Vector3(ToSrgb(c.X), ToSrgb(c.Y), ToSrgb(c.Z)) : c;
                        }
                    }
                }
            }
            return result;
        }

        static float ToSrgb(float linear) => linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;

        public static string Hex(Vector3 c) => $"{(int)MathF.Round(c.X * 255):X2}{(int)MathF.Round(c.Y * 255):X2}{(int)MathF.Round(c.Z * 255):X2}";

        /// <summary>A vertex's colour as RGB 0-1, from <see cref="OBJConverter.ReadVertices"/> (which holds it BGRA).</summary>
        public static Vector3 StoredRgb(Vertex v) => new(v.ColorSet0.Z, v.ColorSet0.Y, v.ColorSet0.X);

        /// <summary>
        /// Splits a mesh into regions to colour: by the colours it has (2 to 24 different ones), else into its separate
        /// pieces (geometry not joined to the rest; vertices in the same place count as joined). Pieces beyond the 15
        /// biggest are put together as one. Returns each vertex's region.
        /// </summary>
        public static int[] FindRegions(Vertex[] vertices, ushort[] indices, RegionSplit split, out List<ColourRegion> regions, out RegionSplit used)
        {
            regions = new();
            var rgb = vertices.Select(v => Hex(StoredRgb(v))).ToArray();
            var distinct = rgb.Distinct().ToList();
            used = split == RegionSplit.Auto ? (distinct.Count is >= 2 and <= 24 ? RegionSplit.Colours : RegionSplit.Pieces) : split;

            int[] region = new int[vertices.Length];
            if (used == RegionSplit.Colours)
            {
                var order = distinct.OrderByDescending(c => rgb.Count(x => x == c)).ToList();
                var index = order.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
                for (int i = 0; i < vertices.Length; i++) region[i] = index[rgb[i]];
                for (int r = 0; r < order.Count; r++)
                {
                    int count = region.Count(x => x == r);
                    var colour = StoredRgb(vertices[Array.IndexOf(region, r)]);
                    regions.Add(new ColourRegion(r, count, colour, $"#{order[r]}"));
                }
                return region;
            }

            // separate pieces: join each triangle's corners, and vertices in the same place (split by UV seams)
            int[] parent = Enumerable.Range(0, vertices.Length).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                Join(indices[t], indices[t + 1]);
                Join(indices[t], indices[t + 2]);
            }
            var byPlace = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = vertices[i].Position * 10000;
                var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                if (byPlace.TryGetValue(key, out int other)) Join(i, other);
                else byPlace[key] = i;
            }

            var pieces = Enumerable.Range(0, vertices.Length).GroupBy(Find).OrderByDescending(g => g.Count()).ToList();
            const int kept = 15;
            for (int p = 0; p < pieces.Count; p++)
                foreach (int v in pieces[p]) region[v] = Math.Min(p, kept);
            for (int r = 0; r <= Math.Min(kept, pieces.Count - 1); r++)
            {
                int count = region.Count(x => x == r);
                string label = r == kept ? $"{pieces.Count - kept} small pieces" : pieces.Count == 1 ? "Whole part" : r == 0 ? "Piece 1 (biggest)" : $"Piece {r + 1}";
                regions.Add(new ColourRegion(r, count, StoredRgb(vertices[Array.IndexOf(region, r)]), label));
            }
            return region;
        }

        /// <summary>
        /// Sets the colour (RGB 0-1) of each vertex of <paramref name="mesh"/> that has one in <paramref name="colours"/>,
        /// keeping alpha. The buffers are copied rather than edited, so a <see cref="MeshSnapshot"/> taken before still
        /// holds the old ones; meshes sharing a buffer are pointed at the copy. Returns false if the mesh keeps no colours.
        /// </summary>
        public static bool Paint(NuRenderMesh mesh, NuRenderMesh[] all, Vector3?[] colours)
        {
            bool painted = false;
            for (int b = 0; b < mesh.VertexBuffers.Length; b++)
            {
                var old = mesh.VertexBuffers[b];
                int offset = 0;
                VertexDefinition? colour = null;
                foreach (var def in old.Definitions)
                {
                    if (def.Variable == VertexDefinitionVariableEnum.colorSet0) { colour = def; break; }
                    offset += VertexList.SizeOf(def.Type);
                }
                if (colour == null || VertexList.SizeOf(colour.Type) != 4 || old.Stride <= 0)
                    continue;

                var copy = new VertexList(old.VerticesCount, (uint)old.Definitions.Length)
                {
                    Definitions = old.Definitions,
                    Stride = old.Stride,
                    VerticesDump = (byte[])old.VerticesDump.Clone(),
                };
                Array.Copy(old.InstancingDividers, copy.InstancingDividers, copy.InstancingDividers.Length);

                int skip = mesh.VertexBufferOffsets != null && b < mesh.VertexBufferOffsets.Length ? mesh.VertexBufferOffsets[b] / old.Stride : 0;
                int first = (int)mesh.VerticesBase + skip;
                for (int i = 0; i < colours.Length && i < mesh.VerticesCount; i++)
                {
                    if (colours[i] is not { } c) continue;
                    int at = (first + i) * old.Stride + offset;
                    if (at + 3 >= copy.VerticesDump.Length) break;
                    // stored blue, green, red, alpha
                    copy.VerticesDump[at] = (byte)MathF.Round(Math.Clamp(c.Z, 0, 1) * 255);
                    copy.VerticesDump[at + 1] = (byte)MathF.Round(Math.Clamp(c.Y, 0, 1) * 255);
                    copy.VerticesDump[at + 2] = (byte)MathF.Round(Math.Clamp(c.X, 0, 1) * 255);
                }

                foreach (var other in all)
                    for (int j = 0; j < other.VertexBuffers.Length; j++)
                        if (other.VertexBuffers[j] == old)
                            other.VertexBuffers[j] = copy;
                mesh.VertexBuffers[b] = copy;
                painted = true;
            }
            return painted;
        }
    }
}

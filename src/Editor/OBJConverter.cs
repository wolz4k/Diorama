using Diorama.Core;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Diorama.Editor
{
    /// <summary>
    /// A mesh read from an .OBJ, before it is fitted to the vertex layout of the mesh it replaces.
    /// </summary>
    public class ObjMeshData
    {
        public List<Vertex> Vertices = new();
        public List<int> Indices = new();

        public bool HasNormals;
        public bool HasUVs;
        public bool HasColours;

        public int Triangles => Indices.Count / 3;
    }

    public static class OBJConverter
    {
        // The game samples textures DirectX style (V = 0 at the top), .OBJ puts V = 0 at the bottom
        static float FlipV(float v) => 1f - v;

        static int ParseIndex(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i;
        }

        static float ParseFloat(string s)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                s = s.Replace(',', '.');
                value = float.Parse(s, CultureInfo.InvariantCulture);
            }
            return value;
        }

        static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);

        public static ObjMeshData ParseOBJ(IEnumerable<string> lines)
        {
            return ParseCore(lines, false)[0].Data;
        }

        /// <summary>
        /// Reads an .OBJ holding several objects (Blender writes an "o" line per object), one mesh per object.
        /// Objects without faces are left out.
        /// </summary>
        public static List<(string Name, ObjMeshData Data)> ParseOBJObjects(IEnumerable<string> lines)
        {
            return ParseCore(lines, true);
        }

        static List<(string Name, ObjMeshData Data)> ParseCore(IEnumerable<string> lines, bool splitObjects)
        {
            List<(string Name, ObjMeshData Data)> objects = new();
            string name = "";
            ObjMeshData data = new();

            List<Vector3> positions = new();
            List<Vector3> normals = new();
            List<Vector2> uvs = new();
            List<Vector3> colors = new();

            Dictionary<(int v, int vt, int vn), int> vertexMap = new();
            List<int> face = new();

            int lineNumber = 0;
            foreach (string rawLine in lines)
            {
                lineNumber++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                string[] split = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                try
                {
                    switch (split[0])
                    {
                        case "o" when splitObjects:
                            if (data.Indices.Count > 0)
                                objects.Add((name, data));
                            name = line.Length > 2 ? line[2..].Trim() : "";
                            data = new();
                            vertexMap.Clear();
                            break;
                        case "v":
                            positions.Add(new Vector3(ParseFloat(split[1]), ParseFloat(split[2]), ParseFloat(split[3])));
                            if (split.Length >= 7)
                            {
                                colors.Add(new Vector3(ParseFloat(split[4]), ParseFloat(split[5]), ParseFloat(split[6])));
                                data.HasColours = true;
                            }
                            else
                            {
                                colors.Add(Vector3.One);
                            }
                            break;
                        case "vn":
                            normals.Add(new Vector3(ParseFloat(split[1]), ParseFloat(split[2]), ParseFloat(split[3])));
                            break;
                        case "vt":
                            uvs.Add(new Vector2(ParseFloat(split[1]), split.Length > 2 ? FlipV(ParseFloat(split[2])) : 1f));
                            break;
                        case "f":
                            face.Clear();
                            for (int i = 1; i < split.Length; i++)
                            {
                                var parts = split[i].Split('/');

                                int v = ParseIndex(parts[0], positions.Count);
                                int vt = parts.Length > 1 && parts[1] != "" ? ParseIndex(parts[1], uvs.Count) : -1;
                                int vn = parts.Length > 2 && parts[2] != "" ? ParseIndex(parts[2], normals.Count) : -1;

                                if (vt >= 0) data.HasUVs = true;
                                if (vn >= 0) data.HasNormals = true;

                                var key = (v, vt, vn);
                                if (!vertexMap.TryGetValue(key, out int index))
                                {
                                    index = data.Vertices.Count;
                                    data.Vertices.Add(new Vertex
                                    {
                                        Position = positions[v],
                                        Normal = vn >= 0 ? normals[vn] : Vector3.Zero,
                                        UVSet01 = vt >= 0 ? new Vector4(uvs[vt], 0, 0) : Vector4.Zero,
                                        ColorSet0 = new Vector4(colors[v], 1)
                                    });
                                    vertexMap[key] = index;
                                }

                                face.Add(index);
                            }

                            // fan-triangulate quads and n-gons
                            for (int i = 1; i < face.Count - 1; i++)
                            {
                                data.Indices.Add(face[0]);
                                data.Indices.Add(face[i]);
                                data.Indices.Add(face[i + 1]);
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                {
                    throw new InvalidDataException($"Line {lineNumber} of the OBJ could not be read: \"{line}\"");
                }
            }

            if (data.Indices.Count > 0)
                objects.Add((name, data));

            if (objects.Count == 0)
                throw new InvalidDataException("The OBJ has no faces. Export it with faces (and triangulate them if you can).");

            return objects;
        }

        /// <summary>
        /// Reads the vertices of a mesh as the game stores them (indices are relative to VerticesBase).
        /// </summary>
        public static Vertex[] ReadVertices(NuRenderMesh nuMesh)
        {
            Vertex[] vertices = VertexList.CreateVerticesArray((int)nuMesh.VerticesCount);
            foreach (var vList in nuMesh.VertexBuffers)
                vList.FillVertices(ref vertices, (int)nuMesh.VerticesBase);
            return vertices;
        }

        public static ushort[] ReadIndices(NuRenderMesh nuMesh)
        {
            return nuMesh.Indices.Skip((int)nuMesh.IndicesBase).Take((int)nuMesh.IndicesCount).ToArray();
        }

        /// <summary>
        /// Fills in what an .OBJ can't carry (bone weights, vertex alpha, second UV sets, and colours if it has none)
        /// from the nearest vertex of the mesh being replaced, works out normals and tangents, and checks the
        /// result fits the game's 16-bit indices. Returns notes for the modder about what was done.
        /// </summary>
        public static List<string> FitToOriginal(ObjMeshData obj, Vertex[] original, IEnumerable<VertexDefinitionVariableEnum> layout, bool hasBlendShape)
        {
            List<string> notes = new();

            if (obj.Vertices.Count > ushort.MaxValue + 1)
                throw new InvalidDataException($"The OBJ has {obj.Vertices.Count:N0} vertices once split by UV seams and hard edges, but a game mesh can hold at most {ushort.MaxValue + 1:N0}. Decimate it or split it across several parts.");

            var attributes = layout.ToHashSet();
            bool skinned = attributes.Contains(VertexDefinitionVariableEnum.blendIndices0);

            if (!obj.HasNormals)
            {
                ComputeNormals(obj);
                notes.Add("The OBJ has no normals, so smooth normals were calculated. (Blender: tick Geometry > Normals when exporting.)");
            }

            ComputeTangents(obj);

            if (original.Length > 0)
            {
                var nearest = new NearestVertexFinder(original);
                foreach (var v in obj.Vertices)
                {
                    Vertex source = original[nearest.Find(v.Position)];

                    if (!obj.HasColours)
                        v.ColorSet0 = new Vector4(source.ColorSet0.Z, source.ColorSet0.Y, source.ColorSet0.X, source.ColorSet0.W); // stored BGRA, held here as RGBA
                    else
                        v.ColorSet0.W = source.ColorSet0.W;

                    v.ColorSet1 = source.ColorSet1;
                    v.UVSet01.Z = source.UVSet01.Z;
                    v.UVSet01.W = source.UVSet01.W;
                    v.UVSet23 = source.UVSet23;
                    v.BlendIndices = source.BlendIndices;
                    v.BlendWeights = source.BlendWeights;
                }
            }

            if (attributes.Contains(VertexDefinitionVariableEnum.colorSet0))
            {
                notes.Add(obj.HasColours
                    ? "Vertex colours come from the OBJ."
                    : "The OBJ has no vertex colours, so each vertex copies the colour of the nearest original vertex. (Blender: tick Geometry > Colors when exporting to bring your own.)");
            }

            if (!obj.HasUVs && attributes.Contains(VertexDefinitionVariableEnum.uvSet01))
                notes.Add("The OBJ has no UVs, so the texture will show as a single colour. Unwrap it in Blender and export with UV Coordinates ticked.");

            if (skinned)
                notes.Add("This part is skinned: each vertex copies the bone weights of the nearest original vertex, so it keeps following the skeleton. Keep new geometry close to the part it replaces.");

            if (hasBlendShape)
                notes.Add("Warning: this part has blend shapes (facial expressions). They were made for the old vertices and will distort or break the new mesh in game.");

            if (original.Length > 0)
            {
                var (oMin, oMax) = Bounds(original.Select(v => v.Position));
                var (nMin, nMax) = Bounds(obj.Vertices.Select(v => v.Position));
                Vector3 oSize = oMax - oMin, nSize = nMax - nMin;
                notes.Add($"Size: original {Size(oSize)}, imported {Size(nSize)}.");

                float oLen = oSize.Length(), nLen = nSize.Length();
                if (oLen > 0 && (nLen > oLen * 5 || nLen < oLen / 5))
                    notes.Add("Warning: the new mesh is a very different size from the original. Check the scale you exported at, and that Blender's exporter uses Y up / -Z forward (its defaults).");
            }

            return notes;
        }

        static (Vector3 min, Vector3 max) Bounds(IEnumerable<Vector3> points)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (min, max);
        }

        static string Size(Vector3 s) => $"{s.X:0.###} x {s.Y:0.###} x {s.Z:0.###}";

        static void ComputeNormals(ObjMeshData obj)
        {
            // area weighted, shared across vertices at the same position so UV seams stay smooth
            Dictionary<Vector3, Vector3> sums = new();
            for (int i = 0; i < obj.Indices.Count; i += 3)
            {
                Vector3 p0 = obj.Vertices[obj.Indices[i]].Position;
                Vector3 p1 = obj.Vertices[obj.Indices[i + 1]].Position;
                Vector3 p2 = obj.Vertices[obj.Indices[i + 2]].Position;
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                foreach (var p in (Vector3[])[p0, p1, p2])
                    sums[p] = sums.GetValueOrDefault(p) + n;
            }

            foreach (var v in obj.Vertices)
            {
                Vector3 n = sums.GetValueOrDefault(v.Position);
                v.Normal = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
            }
        }

        static void ComputeTangents(ObjMeshData obj)
        {
            Vector3[] tangents = new Vector3[obj.Vertices.Count];

            for (int i = 0; i < obj.Indices.Count; i += 3)
            {
                int i0 = obj.Indices[i], i1 = obj.Indices[i + 1], i2 = obj.Indices[i + 2];
                var v0 = obj.Vertices[i0];
                var v1 = obj.Vertices[i1];
                var v2 = obj.Vertices[i2];

                Vector3 edge1 = v1.Position - v0.Position;
                Vector3 edge2 = v2.Position - v0.Position;

                Vector2 deltaUV1 = (v1.UVSet01 - v0.UVSet01).ToVector2();
                Vector2 deltaUV2 = (v2.UVSet01 - v0.UVSet01).ToVector2();

                float area = deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y;
                if (Math.Abs(area) < 1e-12f)
                    continue;

                Vector3 tangent = (deltaUV2.Y * edge1 - deltaUV1.Y * edge2) / area;

                tangents[i0] += tangent;
                tangents[i1] += tangent;
                tangents[i2] += tangent;
            }

            for (int i = 0; i < tangents.Length; i++)
            {
                var n = obj.Vertices[i].Normal;
                var t = tangents[i] - n * Vector3.Dot(n, tangents[i]);

                if (t.LengthSquared() < 1e-12f) // no usable UVs here: any direction along the surface will do
                    t = Vector3.Cross(n, Math.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX);

                obj.Vertices[i].Tangent = t.LengthSquared() > 0 ? Vector3.Normalize(t) : Vector3.UnitX;
            }
        }

        public static RenderMesh MeshFromOBJ(string path, RenderMesh originalMesh, EditorScene scene, out List<string> notes)
        {
            ObjMeshData obj = ParseOBJ(File.ReadLines(path));

            notes = ReplaceMeshData(originalMesh.OriginalMesh, obj, Path.GetFileName(path));
            notes.Add(SaveReminder);

            return BuildRenderMesh(originalMesh.OriginalMesh, scene);
        }

        /// <summary>
        /// Swaps the vertices and triangles of a game mesh for those of an .OBJ, keeping its vertex layout so the
        /// material still lines up. Returns notes for the modder about what was carried over or worked out.
        /// </summary>
        public static List<string> ReplaceMeshData(NuRenderMesh nuMesh, ObjMeshData obj, string fileName)
        {
            Vertex[] original = ReadVertices(nuMesh);
            int originalTriangles = (int)nuMesh.IndicesCount / 3;

            var layout = nuMesh.VertexBuffers.SelectMany(b => b.Definitions).Select(d => d.Variable);
            List<string> notes = FitToOriginal(obj, original, layout, nuMesh.Shape != null);
            notes.Insert(0, $"Imported {obj.Vertices.Count:N0} vertices and {obj.Triangles:N0} triangles from {fileName} (the original had {original.Length:N0} vertices and {originalTriangles:N0} triangles).");

            for (int i = 0; i < nuMesh.VertexBuffers.Length; i++)
            {
                nuMesh.VertexBuffers[i] = VertexList.FromVertices(obj.Vertices, nuMesh.VertexBuffers[i].Definitions);

                // fixes a vertex explosion; a buffer that shared another mesh's (flag 0) now stands alone, 0x503 (blend shapes) is kept
                if (nuMesh.VertexBufferFlags[i] == 0)
                    nuMesh.VertexBufferFlags[i] = 0x502;
                nuMesh.VertexBufferOffsets[i] = 0;
            }

            nuMesh.Indices = obj.Indices.Select(i => (ushort)i).ToArray();
            nuMesh.IndicesFlags = 0x102;
            nuMesh.IndicesBase = 0;
            nuMesh.IndicesCount = (uint)nuMesh.Indices.Length;
            nuMesh.VerticesBase = 0;
            nuMesh.VerticesCount = (uint)obj.Vertices.Count;

            // the game culls by this box; skinned parts leave it zeroed, so only refresh one that was filled in
            if (nuMesh.CentreExtents[0] != Vector4.UnitW || nuMesh.CentreExtents[1] != Vector4.Zero)
            {
                var (min, max) = Bounds(obj.Vertices.Select(v => v.Position));
                nuMesh.CentreExtents[0] = new Vector4((min + max) / 2, 1);
                nuMesh.CentreExtents[1] = new Vector4((max - min) / 2, 0);
            }

            return notes;
        }

        public const string SaveReminder = "The change is only in memory until you right-click the scene in the hierarchy and choose Save GScene, which overwrites the file you opened.";

        static RenderMesh BuildRenderMesh(NuRenderMesh nuMesh, EditorScene scene)
        {
            RenderVertexBuffer[] vertexBuffers = new RenderVertexBuffer[nuMesh.VertexBuffers.Length];
            for (int i = 0; i < vertexBuffers.Length; i++)
            {
                vertexBuffers[i] = (RenderVertexBuffer)scene.GetOrAdd(RenderVertexBuffer.FromBuffer(nuMesh.VertexBuffers[i]));
                nuMesh.VertexBuffers[i] = vertexBuffers[i].Original;
            }

            RenderIndicesBuffer indicesBuffer = (RenderIndicesBuffer)scene.GetOrAdd(RenderIndicesBuffer.FromBuffer(nuMesh.Indices));
            nuMesh.Indices = indicesBuffer.Indices;

            RenderMesh mesh = new RenderMesh(vertexBuffers, indicesBuffer);
            mesh.IndicesBase = 0;
            mesh.IndicesCount = (int)nuMesh.IndicesCount;
            mesh.VerticesBase = 0;
            mesh.VerticesCount = (int)nuMesh.VerticesCount;
            mesh.OriginalMesh = nuMesh;

            return mesh;
        }

        public static void WriteMeshToOBJ(EditorGeometryObject geo, string path)
        {
            var nuMesh = geo.Mesh.OriginalMesh;
            string name = Path.GetFileNameWithoutExtension(path);
            string? materialName = null;

            var diffuse = geo.Material?.Diffuse0?.Original;
            if (geo.Material != null)
            {
                materialName = string.IsNullOrWhiteSpace(geo.Material.Name) ? "material" : geo.Material.Name.Replace(' ', '_');
                List<string> mtl = [$"newmtl {materialName}", "Kd 1 1 1"];

                if (diffuse?.ImageHeader != null && diffuse.Data != null)
                {
                    string textureFile = name + "_diffuse.dds";
                    using (var fs = File.Create(Path.Join(Path.GetDirectoryName(path), textureFile)))
                    {
                        fs.Write(diffuse.ImageHeader);
                        fs.Write(diffuse.Data);
                    }
                    mtl.Add($"map_Kd {textureFile}");
                }

                File.WriteAllLines(Path.ChangeExtension(path, "mtl"), mtl);
            }

            using var writer = new StreamWriter(path);
            WriteOBJ(writer, ReadVertices(nuMesh), ReadIndices(nuMesh), name, materialName != null ? name + ".mtl" : null, materialName);
        }

        public static void WriteOBJ(TextWriter writer, Vertex[] vertices, ushort[] indices, string objectName, string? mtlFile, string? materialName)
        {
            WriteParts(writer, [new ObjPart { Name = objectName, Vertices = vertices, Indices = indices, Material = materialName }], mtlFile);
        }

        public static void WriteParts(TextWriter writer, IEnumerable<ObjPart> parts, string? mtlFile)
        {
            writer.WriteLine("# Exported by Diorama. Positions are Y up; vertex colours follow each position as r g b.");
            if (mtlFile != null)
                writer.WriteLine($"mtllib {mtlFile}");

            int first = 1; // .OBJ indices count from 1 across the whole file
            foreach (var part in parts)
            {
                writer.WriteLine($"o {part.Name}");

                foreach (var v in part.Vertices) // colours are stored BGRA
                    writer.WriteLine($"v {F(v.Position.X)} {F(v.Position.Y)} {F(v.Position.Z)} {F(v.ColorSet0.Z)} {F(v.ColorSet0.Y)} {F(v.ColorSet0.X)}");
                foreach (var v in part.Vertices)
                    writer.WriteLine($"vt {F(v.UVSet01.X)} {F(FlipV(v.UVSet01.Y))}");
                foreach (var v in part.Vertices)
                    writer.WriteLine($"vn {F(v.Normal.X)} {F(v.Normal.Y)} {F(v.Normal.Z)}");

                if (part.Material != null)
                    writer.WriteLine($"usemtl {part.Material}");

                for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    int i0 = part.Indices[i] + first, i1 = part.Indices[i + 1] + first, i2 = part.Indices[i + 2] + first;
                    writer.WriteLine($"f {i0}/{i0}/{i0} {i1}/{i1}/{i1} {i2}/{i2}/{i2}");
                }

                first += part.Vertices.Length;
            }
        }

        /// <summary>
        /// The name a part gets in a multi-part .OBJ. The "__m" number is the game mesh it came from, which is how
        /// the part finds its way back on import, so it has to survive renaming in Blender (".001" suffixes are fine).
        /// </summary>
        public static string PartName(string? objectName, int meshIndex)
        {
            string clean = System.Text.RegularExpressions.Regex.Replace(objectName ?? "", @"[^A-Za-z0-9-]+", "_").Trim('_');
            return $"{(clean == "" ? "part" : clean)}__m{meshIndex}";
        }

        public static int? MeshIndexFromName(string name)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(name, @"__m(\d+)");
            return matches.Count > 0 ? int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        static Vertex Transformed(Vertex v, Matrix4x4 m)
        {
            return new Vertex
            {
                Position = Vector3.Transform(v.Position, m),
                Normal = v.Normal.LengthSquared() > 0 ? Vector3.Normalize(Vector3.TransformNormal(v.Normal, m)) : v.Normal,
                Tangent = v.Tangent,
                ColorSet0 = v.ColorSet0,
                ColorSet1 = v.ColorSet1,
                UVSet01 = v.UVSet01,
                UVSet23 = v.UVSet23,
                BlendIndices = v.BlendIndices,
                BlendWeights = v.BlendWeights,
            };
        }

        /// <summary>
        /// True when an imported part has the same triangles, positions, UVs and (if it has them) colours as the mesh,
        /// so a part that went to Blender and back untouched isn't rebuilt (which would lose its exact vertex order).
        /// </summary>
        public static bool IsUnchanged(ObjMeshData obj, NuRenderMesh mesh)
        {
            if (obj.Triangles != mesh.IndicesCount / 3)
                return false;

            Vertex[] original = ReadVertices(mesh);

            // the .OBJ holds 6 decimals, so match within a tolerance; vertices sharing a position (UV seams) are told apart by UV and colour
            const float cell = 1e-3f;
            static (int, int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            Dictionary<(int, int, int), List<int>> grid = new();
            for (int i = 0; i < original.Length; i++)
            {
                var key = CellOf(original[i].Position);
                if (!grid.TryGetValue(key, out var list))
                    grid[key] = list = new();
                list.Add(i);
            }

            // game vertices hold colour as BGRA, imported ones as RGBA
            static Vector3 GameRgb(Vertex v) => new(v.ColorSet0.Z, v.ColorSet0.Y, v.ColorSet0.X);

            bool Same(Vertex o, Vector3 position, Vector2 uv, Vector3 rgb) =>
                Vector3.Distance(o.Position, position) < 1e-4f &&
                Vector2.Distance(o.UVSet01.ToVector2(), uv) < 1e-4f &&
                (!obj.HasColours || Vector3.Distance(GameRgb(o), rgb) < 1f / 255);

            // the first original vertex that looks the same; vertices differing only in normal share one
            int FindFirst(Vector3 position, Vector2 uv, Vector3 rgb)
            {
                var (cx, cy, cz) = CellOf(position);
                int best = -1;
                for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    if (grid.TryGetValue((x, y, z), out var list))
                        foreach (int o in list)
                            if ((best < 0 || o < best) && Same(original[o], position, uv, rgb))
                                best = o;
                }
                return best;
            }

            int[] match = new int[obj.Vertices.Count];
            for (int i = 0; i < obj.Vertices.Count; i++)
            {
                var v = obj.Vertices[i];
                match[i] = FindFirst(v.Position, v.UVSet01.ToVector2(), new Vector3(v.ColorSet0.X, v.ColorSet0.Y, v.ColorSet0.Z));
                if (match[i] < 0)
                    return false;
            }

            // same triangles, in any order, keeping their winding
            static (int, int, int) Triangle(int a, int b, int c) =>
                a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);

            ushort[] indices = ReadIndices(mesh);
            int[] rep = original.Select(o => FindFirst(o.Position, o.UVSet01.ToVector2(), GameRgb(o))).ToArray();
            HashSet<(int, int, int)> triangles = new();
            for (int i = 0; i + 2 < indices.Length; i += 3)
                triangles.Add(Triangle(rep[indices[i]], rep[indices[i + 1]], rep[indices[i + 2]]));

            for (int i = 0; i + 2 < obj.Indices.Count; i += 3)
                if (!triangles.Contains(Triangle(match[obj.Indices[i]], match[obj.Indices[i + 1]], match[obj.Indices[i + 2]])))
                    return false;

            return true;
        }

        /// <summary>
        /// Replaces every mesh named in a multi-part .OBJ (see <see cref="PartName"/>). <paramref name="transforms"/>
        /// holds the transform each part was exported with, which is undone here. Returns notes for the modder.
        /// </summary>
        public static List<string> ReplaceParts(NuRenderMesh[] meshes, List<(string Name, ObjMeshData Data)> objects, IReadOnlyDictionary<int, Matrix4x4> transforms, out List<int> replaced)
        {
            List<string> notes = new();
            replaced = new();
            List<string> unnamed = new();
            HashSet<int> seen = new();
            int unchanged = 0;

            foreach (var (name, data) in objects)
            {
                int? index = MeshIndexFromName(name);
                if (index == null || index >= meshes.Length)
                {
                    unnamed.Add(name == "" ? "(unnamed)" : name);
                    continue;
                }

                if (!seen.Add(index.Value))
                {
                    notes.Add($"{name}: another object is also named for mesh {index}, so this one was ignored. Join them in Blender (Ctrl+J) if both belong to the part.");
                    continue;
                }

                if (transforms.TryGetValue(index.Value, out var transform) && !transform.IsIdentity && Matrix4x4.Invert(transform, out var toLocal))
                {
                    for (int i = 0; i < data.Vertices.Count; i++)
                        data.Vertices[i] = Transformed(data.Vertices[i], toLocal);
                }

                if (IsUnchanged(data, meshes[index.Value]))
                {
                    unchanged++;
                    continue;
                }

                try
                {
                    var partNotes = ReplaceMeshData(meshes[index.Value], data, name);
                    replaced.Add(index.Value);
                    notes.Add($"{name}: now {data.Vertices.Count:N0} vertices and {data.Triangles:N0} triangles.");
                    notes.AddRange(partNotes.Where(n => n.StartsWith("Warning") || n.StartsWith("The OBJ has no")).Select(n => $"{name}: {n}"));
                }
                catch (InvalidDataException ex)
                {
                    notes.Add($"{name}: not replaced. {ex.Message}");
                }
            }

            notes.Insert(0, replaced.Count == 0
                ? "No parts changed, so nothing was replaced."
                : $"Replaced {replaced.Count} part{(replaced.Count == 1 ? "" : "s")}. Bone weights, vertex alpha and anything else an OBJ can't hold came from the nearest original vertex.");

            if (unchanged > 0)
                notes.Add($"{unchanged} part{(unchanged == 1 ? " was" : "s were")} the same as in the game file and left as they are.");

            if (unnamed.Count > 0)
                notes.Add($"Skipped {unnamed.Count} object{(unnamed.Count == 1 ? "" : "s")} whose name doesn't end in \"__m\" and a mesh number, so it isn't clear which part they replace: {string.Join(", ", unnamed.Take(8))}{(unnamed.Count > 8 ? ", ..." : "")}. To add new geometry, join it (Ctrl+J) into the part it belongs to.");

            if (replaced.Count > 0)
                notes.Add(SaveReminder);

            return notes;
        }

        /// <summary>
        /// The transform each mesh is drawn with, taken from the first object that draws it.
        /// </summary>
        static Dictionary<int, Matrix4x4> PartTransforms(EditorScene scene, NuRenderMesh[] meshes)
        {
            Dictionary<int, Matrix4x4> transforms = new();
            foreach (var geo in scene.AllGeometry())
            {
                int index = Array.IndexOf(meshes, geo.Mesh?.OriginalMesh);
                if (index < 0 || transforms.ContainsKey(index))
                    continue;

                var m = geo.Transform;
                transforms[index] = new Matrix4x4(
                    m.M11, m.M12, m.M13, m.M14,
                    m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34,
                    m.M41, m.M42, m.M43, m.M44);
            }
            return transforms;
        }

        /// <summary>
        /// Writes every part shown right now (the picked character LOD, breakup parts if they're on) to one .OBJ, with
        /// an .MTL and the diffuse textures as .DDS beside it. Returns how many parts were written.
        /// </summary>
        public static int ExportParts(EditorScene scene, string path)
        {
            var meshes = scene.OriginalScene.MeshSceneBlock.Meshes;
            var transforms = PartTransforms(scene, meshes);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string directory = Path.GetDirectoryName(path) ?? "";

            List<ObjPart> parts = new();
            Dictionary<object, string> materialNames = new();
            Dictionary<object, string> textureFiles = new();
            List<string> mtl = new();
            HashSet<int> written = new();

            foreach (var sceneObject in scene.Objects.OfType<EditorSceneObject>().Where(scene.IsShown))
            {
                var clip = sceneObject.ClipObject ?? sceneObject.Lods?.FirstOrDefault()?.ClipObject;
                if (clip == null)
                    continue;

                foreach (var geo in clip.Elements)
                {
                    int index = Array.IndexOf(meshes, geo.Mesh?.OriginalMesh);
                    if (index < 0 || !written.Add(index))
                        continue;

                    string? materialName = null;
                    if (geo.Material != null && !materialNames.TryGetValue(geo.Material, out materialName))
                    {
                        materialName = PartName(geo.Material.Name, materialNames.Count).Replace("__m", "__mat");
                        materialNames[geo.Material] = materialName;
                        mtl.Add($"newmtl {materialName}");
                        mtl.Add("Kd 1 1 1");

                        var diffuse = geo.Material.Diffuse0?.Original;
                        if (diffuse?.ImageHeader != null && diffuse.Data != null)
                        {
                            if (!textureFiles.TryGetValue(diffuse, out string? textureFile))
                            {
                                textureFile = $"{baseName}_tex{textureFiles.Count}.dds";
                                textureFiles[diffuse] = textureFile;
                                using var fs = File.Create(Path.Join(directory, textureFile));
                                fs.Write(diffuse.ImageHeader);
                                fs.Write(diffuse.Data);
                            }
                            mtl.Add($"map_Kd {textureFile}");
                        }
                        mtl.Add("");
                    }

                    var vertices = ReadVertices(geo.Mesh!.OriginalMesh);
                    if (transforms.TryGetValue(index, out var transform) && !transform.IsIdentity)
                        vertices = vertices.Select(v => Transformed(v, transform)).ToArray();

                    parts.Add(new ObjPart
                    {
                        Name = PartName(sceneObject.DisplayName, index),
                        Vertices = vertices,
                        Indices = ReadIndices(geo.Mesh.OriginalMesh),
                        Material = materialName,
                    });
                }
            }

            File.WriteAllLines(Path.ChangeExtension(path, "mtl"), mtl);
            using (var writer = new StreamWriter(path))
                WriteParts(writer, parts, baseName + ".mtl");

            return parts.Count;
        }

        /// <summary>
        /// Brings back a multi-part .OBJ written by <see cref="ExportParts"/> (and edited in Blender). Runs on the GL thread.
        /// </summary>
        public static List<string> ImportParts(EditorScene scene, string path)
        {
            var meshes = scene.OriginalScene.MeshSceneBlock.Meshes;
            var objects = ParseOBJObjects(File.ReadLines(path));

            var notes = ReplaceParts(meshes, objects, PartTransforms(scene, meshes), out var replaced);

            var geometry = scene.AllGeometry().ToList();
            foreach (int index in replaced)
            {
                var mesh = BuildRenderMesh(meshes[index], scene);
                foreach (var geo in geometry.Where(g => g.Mesh?.OriginalMesh == meshes[index]))
                    geo.Mesh = mesh;
            }

            return notes;
        }
    }

    public class ObjPart
    {
        public string Name = "";
        public Vertex[] Vertices = [];
        public ushort[] Indices = [];
        public string? Material;
    }

    /// <summary>
    /// Finds the closest of a fixed set of vertices to a point, using a k-d tree.
    /// </summary>
    public class NearestVertexFinder
    {
        readonly Vector3[] points;
        readonly int[] order; // a balanced tree laid out in place: the middle of each range splits it
        readonly int[] axes;

        public NearestVertexFinder(Vertex[] vertices)
        {
            points = vertices.Select(v => v.Position).ToArray();
            order = Enumerable.Range(0, points.Length).ToArray();
            axes = new int[points.Length];
            Build(0, points.Length);
        }

        static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        void Build(int start, int end)
        {
            if (end - start <= 1)
                return;

            // split along the widest axis of this range
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = start; i < end; i++)
            {
                min = Vector3.Min(min, points[order[i]]);
                max = Vector3.Max(max, points[order[i]]);
            }
            Vector3 size = max - min;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;

            Array.Sort(order, start, end - start, Comparer<int>.Create((a, b) => Get(points[a], axis).CompareTo(Get(points[b], axis))));

            int mid = (start + end) / 2;
            axes[mid] = axis;
            Build(start, mid);
            Build(mid + 1, end);
        }

        public int Find(Vector3 p)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            Search(p, 0, points.Length, ref best, ref bestDist);
            return best;
        }

        void Search(Vector3 p, int start, int end, ref int best, ref float bestDist)
        {
            if (start >= end)
                return;

            int mid = (start + end) / 2;
            int index = order[mid];

            float d = Vector3.DistanceSquared(points[index], p);
            if (d < bestDist)
            {
                bestDist = d;
                best = index;
            }

            if (end - start == 1)
                return;

            int axis = axes[mid];
            float delta = Get(p, axis) - Get(points[index], axis);

            // the side the point is on first, then the other side only if it could hold something closer
            if (delta < 0)
            {
                Search(p, start, mid, ref best, ref bestDist);
                if (delta * delta < bestDist)
                    Search(p, mid + 1, end, ref best, ref bestDist);
            }
            else
            {
                Search(p, mid + 1, end, ref best, ref bestDist);
                if (delta * delta < bestDist)
                    Search(p, start, mid, ref best, ref bestDist);
            }
        }
    }
}

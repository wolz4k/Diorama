using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor;
using System.Numerics;

namespace Diorama.Tests
{
    /// <summary>
    /// Mesh replacement from .OBJ files: parsing what Blender writes, and exporting then re-importing real game
    /// parts without losing anything the game needs. Game files are read into memory, never written.
    /// </summary>
    [TestClass]
    public class ObjImportTests
    {
        private static string GamePath => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";

        private static GScene Load(string relativePath)
        {
            string path = Path.Join(GamePath, relativePath);
            if (!File.Exists(path))
                Assert.Inconclusive($"Missing {path}");

            using var input = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
            input.SetFileLocation(new FilesystemFileLocation(path));
            return GScene.Parse(input);
        }

        private static GScene Reparse(GScene scene)
        {
            var buffer = new MemoryStream();
            using (var output = new RawFile(buffer))
                scene.Write(output, new GSerializationContext());

            using var input = new RawFile(new MemoryStream(buffer.ToArray(), false));
            input.SetFileLocation(new FilesystemFileLocation(scene.Path));
            return GScene.Parse(input);
        }

        private static ObjMeshData ExportAndParse(NuRenderMesh mesh)
        {
            var writer = new StringWriter();
            OBJConverter.WriteOBJ(writer, OBJConverter.ReadVertices(mesh), OBJConverter.ReadIndices(mesh), "part", "part.mtl", "mat");
            return OBJConverter.ParseOBJ(writer.ToString().Split('\n'));
        }

        [TestMethod]
        public void ParsesBlenderStyleObj()
        {
            string obj = """
                # Blender 4.2.0
                mtllib cube.mtl

                o Cube
                v 0 0 0
                v 1 0 0
                v	1 1 0
                v 0 1 0
                vt 0 0
                vt 1 0 0
                vt 1 1
                vt 0 1
                s 0
                usemtl Material
                f 1/1 2/2 3/3 4/4
                f -4/-4 -2/-2 -1/-1
                """;

            var data = OBJConverter.ParseOBJ(obj.Split('\n'));

            Assert.AreEqual(3, data.Triangles); // the quad fans into two
            Assert.AreEqual(4, data.Vertices.Count); // the last face reuses the quad's corners
            Assert.IsTrue(data.HasUVs);
            Assert.IsFalse(data.HasNormals);
            Assert.IsFalse(data.HasColours);
            Assert.AreEqual(1f, data.Vertices[0].UVSet01.Y, "V is flipped from .OBJ's bottom-left origin to the game's top-left");
        }

        [TestMethod]
        public void ReportsTheBadLine()
        {
            var ex = Assert.ThrowsException<InvalidDataException>(() => OBJConverter.ParseOBJ(["v 0 0 0", "v 1 0", "f 1 2 1"]));
            StringAssert.Contains(ex.Message, "Line 2");
        }

        [TestMethod]
        public void RefusesMoreVerticesThanTheGameCanIndex()
        {
            var data = new ObjMeshData();
            for (int i = 0; i < 70000; i++)
                data.Vertices.Add(new Vertex { Position = new Vector3(i, 0, 0) });
            data.Indices.AddRange([0, 1, 2]);

            Assert.ThrowsException<InvalidDataException>(() => OBJConverter.FitToOriginal(data, [], [], false));
        }

        [TestMethod]
        public void IndicesAreRelativeToVerticesBase()
        {
            foreach (var file in new[] { @"COMMONOBJECTS\LAYOUT_ASSETS\LAYOUT_CLAYFACE_BIGFIG_DX11.GSC", @"CHARS\CUSTOMISER_BOW\BOW_B_ITEM_DX11.GHG" })
            {
                foreach (var mesh in Load(file).MeshSceneBlock.Meshes)
                {
                    int max = OBJConverter.ReadIndices(mesh).Max();
                    Assert.IsTrue(max < mesh.VerticesCount, $"{file}: index {max} past {mesh.VerticesCount} vertices");
                }
            }
        }

        /// <summary>
        /// Export each part of a skinned, textured accessory to .OBJ and bring it straight back: positions, UVs,
        /// colours and bone weights must survive, and the saved file must parse with the new mesh in it.
        /// </summary>
        [TestMethod]
        public void SkinnedPartSurvivesExportAndReimport()
        {
            var scene = Load(@"CHARS\CUSTOMISER_BOW\BOW_B_ITEM_DX11.GHG");
            var meshes = scene.MeshSceneBlock.Meshes;
            var originals = meshes.Select(OBJConverter.ReadVertices).ToArray();

            for (int m = 0; m < meshes.Length; m++)
            {
                var notes = OBJConverter.ReplaceMeshData(meshes[m], ExportAndParse(meshes[m]), "part.obj");
                Assert.IsTrue(notes.Any(n => n.Contains("bone weights")), "skinned parts say their weights were carried over");
                Assert.IsTrue(notes.Any(n => n.Contains("come from the OBJ")), "the exported OBJ carries colours");
            }

            var saved = Reparse(scene);

            for (int m = 0; m < meshes.Length; m++)
            {
                var before = originals[m];
                var after = OBJConverter.ReadVertices(saved.MeshSceneBlock.Meshes[m]);
                Assert.AreEqual(before.Length, after.Length);

                for (int i = 0; i < before.Length; i++)
                {
                    Assert.IsTrue(Vector3.Distance(before[i].Position, after[i].Position) < 1e-3f, $"mesh {m} vertex {i} position");
                    Assert.IsTrue(Vector4.Distance(before[i].UVSet01, after[i].UVSet01) < 1e-3f, $"mesh {m} vertex {i} UV");
                    Assert.IsTrue(Vector4.Distance(before[i].ColorSet0, after[i].ColorSet0) < 2f / 255, $"mesh {m} vertex {i} colour {before[i].ColorSet0} -> {after[i].ColorSet0}");
                    Assert.AreEqual(before[i].BlendIndices, after[i].BlendIndices, $"mesh {m} vertex {i} bones");
                    Assert.IsTrue(Vector4.Distance(before[i].BlendWeights, after[i].BlendWeights) < 1e-3f, $"mesh {m} vertex {i} weights");
                    Assert.IsTrue(Vector3.Dot(before[i].Normal, after[i].Normal) > 0.95f, $"mesh {m} vertex {i} normal");
                }
            }
        }

        [TestMethod]
        public void StaticPartGetsANewCullingBox()
        {
            var scene = Load(@"CHARS\ITEMS\CLAYFACE_BIGFIGMACE_DX11.GSC");
            var mesh = scene.MeshSceneBlock.Meshes[0];

            var obj = ExportAndParse(mesh);
            foreach (var v in obj.Vertices)
                v.Position = v.Position * 2 + new Vector3(1, 0, 0);
            Vector4 oldCentre = mesh.CentreExtents[0], oldExtents = mesh.CentreExtents[1];

            OBJConverter.ReplaceMeshData(mesh, obj, "mace.obj");

            var saved = Reparse(scene).MeshSceneBlock.Meshes[0];
            Assert.IsTrue(Vector3.Distance(saved.CentreExtents[0].AsVector3() , oldCentre.AsVector3() * 2 + new Vector3(1, 0, 0)) < 1e-2f, $"centre {saved.CentreExtents[0]}");
            Assert.IsTrue(Vector3.Distance(saved.CentreExtents[1].AsVector3(), oldExtents.AsVector3() * 2) < 1e-2f, $"extents {saved.CentreExtents[1]}");
        }

        [TestMethod]
        public void ObjWithoutColoursOrNormalsBorrowsFromTheOriginal()
        {
            var scene = Load(@"CHARS\CUSTOMISER_BOW\BOW_B_ITEM_DX11.GHG");
            var mesh = scene.MeshSceneBlock.Meshes[0];
            var before = OBJConverter.ReadVertices(mesh);

            // what Blender writes with Colors and Normals unticked
            var lines = new StringWriter();
            OBJConverter.WriteOBJ(lines, before, OBJConverter.ReadIndices(mesh), "part", null, null);
            var stripped = lines.ToString().Split('\n')
                .Where(l => !l.StartsWith("vn "))
                .Select(l => l.StartsWith("v ") ? string.Join(' ', l.Split(' ').Take(4)) : l)
                .Select(l => l.StartsWith("f ") ? string.Join(' ', l.Split(' ').Select(c => c.Contains('/') ? c[..c.LastIndexOf('/')] : c)) : l);

            var data = OBJConverter.ParseOBJ(stripped);
            var notes = OBJConverter.ReplaceMeshData(mesh, data, "part.obj");
            Assert.IsTrue(notes.Any(n => n.Contains("no vertex colours")));
            Assert.IsTrue(notes.Any(n => n.Contains("no normals")));

            var after = OBJConverter.ReadVertices(mesh);
            var nearest = new NearestVertexFinder(before);
            foreach (var v in after)
            {
                var source = before[nearest.Find(v.Position)];
                Assert.IsTrue(Vector4.Distance(source.ColorSet0, v.ColorSet0) < 2f / 255, $"colour {source.ColorSet0} -> {v.ColorSet0}");
                Assert.IsTrue(Math.Abs(v.Normal.Length() - 1) < 0.05f, "calculated normals are unit length");
            }
        }

        [TestMethod]
        public void PartNamesSurviveBlenderRenames()
        {
            Assert.AreEqual("DARKSEID_LOD1__m12", OBJConverter.PartName("DARKSEID LOD1", 12));
            Assert.AreEqual(12, OBJConverter.MeshIndexFromName("DARKSEID_LOD1__m12.001"));
            Assert.AreEqual(3, OBJConverter.MeshIndexFromName("odd__m7_name__m3"));
            Assert.IsNull(OBJConverter.MeshIndexFromName("Cube"));
        }

        private static List<(string Name, ObjMeshData Data)> ExportAllParts(GScene scene, Func<int, Matrix4x4>? transform = null)
        {
            var meshes = scene.MeshSceneBlock.Meshes;
            var parts = meshes.Select((mesh, i) => new ObjPart
            {
                Name = OBJConverter.PartName("part", i),
                Vertices = OBJConverter.ReadVertices(mesh).Select(v =>
                {
                    if (transform != null)
                        v.Position = Vector3.Transform(v.Position, transform(i));
                    return v;
                }).ToArray(),
                Indices = OBJConverter.ReadIndices(mesh),
            });

            var writer = new StringWriter();
            OBJConverter.WriteParts(writer, parts, null);
            return OBJConverter.ParseOBJObjects(writer.ToString().Split('\n'));
        }

        /// <summary>
        /// Exporting a whole character and bringing it straight back must change nothing at all, so an edit to one
        /// part doesn't disturb the others (face blend shapes depend on the exact vertex order).
        /// </summary>
        [TestMethod]
        public void UntouchedPartsAreLeftAlone()
        {
            string path = @"CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.GHG";
            var scene = Load(path);
            var transforms = Enumerable.Range(0, scene.MeshSceneBlock.Meshes.Length)
                .ToDictionary(i => i, i => Matrix4x4.CreateRotationY(i * 0.3f) * Matrix4x4.CreateTranslation(i, 0, 0));

            var objects = ExportAllParts(scene, i => transforms[i]);
            var notes = OBJConverter.ReplaceParts(scene.MeshSceneBlock.Meshes, objects, transforms, out var replaced);

            Assert.AreEqual(0, replaced.Count, string.Join("\n", notes));
            StringAssert.Contains(notes[0], "nothing was replaced");

            var buffer = new MemoryStream();
            using (var output = new RawFile(buffer))
                scene.Write(output, new GSerializationContext());
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Join(GamePath, path)), buffer.ToArray());
        }

        [TestMethod]
        public void OnlyTheEditedPartIsReplaced()
        {
            var scene = Load(@"CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.GHG");
            var meshes = scene.MeshSceneBlock.Meshes;
            var objects = ExportAllParts(scene);

            int edited = objects.Count / 2;
            foreach (var v in objects[edited].Data.Vertices)
                v.Position *= 1.1f;
            objects.Add(("Cube", objects[0].Data)); // an object Blender added, with no part number

            var notes = OBJConverter.ReplaceParts(meshes, objects, new Dictionary<int, Matrix4x4>(), out var replaced);

            CollectionAssert.AreEqual(new[] { edited }, replaced);
            Assert.IsTrue(notes.Any(n => n.Contains("Skipped 1 object") && n.Contains("Cube")), string.Join("\n", notes));
            Assert.IsTrue(notes.Any(n => n.Contains($"{meshes.Length - 1} parts were the same")), string.Join("\n", notes));

            var saved = Reparse(scene).MeshSceneBlock.Meshes[edited];
            Assert.AreEqual(objects[edited].Data.Vertices.Count, (int)saved.VerticesCount);
        }

        [TestMethod]
        public void BlendShapeOffsetsReencodeExactly()
        {
            var scene = Load(@"CHARS\SUPER_CHARACTER\FACE\FACE_ATROCITUS_DX11.GHG");
            int shapes = 0;
            foreach (var mesh in scene.MeshSceneBlock.Meshes)
                for (var shape = mesh.Shape; shape != null; shape = shape.Next)
                {
                    if (shape.CompressionFormat != 2) continue; // 234 of the game's 155,740 shapes use the older layout
                    var offsets = shape.DecodeOffsets((int)mesh.VerticesCount);
                    Assert.IsNotNull(offsets);
                    var copy = new NuBlendShape();
                    copy.EncodeOffsets(offsets);
                    CollectionAssert.AreEqual(shape.Buffer, copy.Buffer);
                    CollectionAssert.AreEqual(shape.RunBatchTableV2, copy.RunBatchTableV2);
                    shapes++;
                }
            Assert.IsTrue(shapes > 10, $"only {shapes} shapes");
        }

        /// <summary>
        /// A face replaced through an OBJ keeps its expressions: every blend shape is rebuilt for the new vertices from the
        /// nearest original vertex, and the saved file still holds them. Here the same face goes out and comes back, so each
        /// vertex must move exactly as before.
        /// </summary>
        [TestMethod]
        public void ReplacedFaceKeepsItsExpressions()
        {
            var scene = Load(@"CHARS\SUPER_CHARACTER\FACE\FACE_ATROCITUS_DX11.GHG");
            var mesh = scene.MeshSceneBlock.Meshes.First(m => m.Shape != null);
            int index = Array.IndexOf(scene.MeshSceneBlock.Meshes, mesh);
            var before = OBJConverter.ReadVertices(mesh);
            var beforeShapes = new List<Vector3[]>();
            for (var s = mesh.Shape; s != null; s = s.Next)
                beforeShapes.Add(s.DecodeOffsets(before.Length)!);

            var meshes = scene.MeshSceneBlock.Meshes;
            Assert.IsTrue(meshes.Any(m => m != mesh && m.VertexBuffers.Any(b => mesh.VertexBuffers.Contains(b))),
                "this face's parts share vertex buffers, which the replacement must keep valid");
            var othersBefore = meshes.Select(m => m == mesh ? null : OBJConverter.ReadVertices(m)).ToArray();

            var obj = ExportAndParse(mesh);
            var notes = OBJConverter.ReplaceMeshData(mesh, obj, "face.obj", meshes);
            Assert.IsTrue(notes.Any(n => n.Contains("expressions carry over")), string.Join("\n", notes));

            var reparsed = Reparse(scene).MeshSceneBlock.Meshes;
            for (int m = 0; m < reparsed.Length; m++)
            {
                if (othersBefore[m] == null) continue;
                var again = OBJConverter.ReadVertices(reparsed[m]);
                Assert.AreEqual(othersBefore[m]!.Length, again.Length);
                for (int i = 0; i < again.Length; i++)
                    Assert.AreEqual(othersBefore[m]![i].Position, again[i].Position, $"mesh {m} vertex {i} moved");
            }

            var saved = reparsed[index];
            var after = OBJConverter.ReadVertices(saved);
            var nearest = new NearestVertexFinder(before);
            int k = 0;
            for (var s = saved.Shape; s != null; s = s.Next, k++)
            {
                var offsets = s.DecodeOffsets(after.Length);
                Assert.IsNotNull(offsets, $"shape {k} no longer decodes");
                for (int i = 0; i < after.Length; i++)
                {
                    int j = nearest.Find(after[i].Position);
                    Assert.IsTrue(Vector3.Distance(offsets[i], beforeShapes[k][j]) < 1e-6f, $"shape {k} vertex {i}");
                }
            }
            Assert.AreEqual(beforeShapes.Count, k, "every shape is still there");
        }

        [TestMethod]
        public void NearestVertexFinderMatchesBruteForce()
        {
            var random = new Random(1);
            var points = Enumerable.Range(0, 500).Select(_ => new Vertex { Position = new Vector3(random.NextSingle(), random.NextSingle() * 3, random.NextSingle()) }).ToArray();
            var finder = new NearestVertexFinder(points);

            for (int i = 0; i < 300; i++)
            {
                var p = new Vector3(random.NextSingle() * 4 - 1.5f, random.NextSingle() * 6 - 1.5f, random.NextSingle() * 4 - 1.5f);
                int expected = Enumerable.Range(0, points.Length).MinBy(j => Vector3.DistanceSquared(points[j].Position, p));
                Assert.AreEqual(Vector3.DistanceSquared(points[expected].Position, p), Vector3.DistanceSquared(points[finder.Find(p)].Position, p), 1e-6f);
            }
        }
    }
}

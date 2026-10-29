using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor;
using System.Numerics;
using System.Text;

namespace Diorama.Tests
{
    /// <summary>
    /// Colours for models made elsewhere: an OBJ's material colours, a .UEMODEL's colour regions, splitting a part into
    /// regions, and painting colours into a game mesh. Game files are read into memory, never written.
    /// </summary>
    [TestClass]
    public class ColourImportTests
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

        private static string TempFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "DioramaColours_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void MaterialColoursBecomeVertexColours()
        {
            string dir = TempFolder();
            try
            {
                // as a building tool writes it: one material per brick colour
                File.WriteAllLines(Path.Combine(dir, "bricks.obj"), ["mtllib bricks.mtl", "v 0 0 0", "v 1 0 0", "v 0 1 0", "v 1 1 0",
                    "usemtl Red", "f 1 2 3", "usemtl Blue", "f 2 4 3"]);
                File.WriteAllLines(Path.Combine(dir, "bricks.mtl"), ["newmtl Red", "Kd 0.8 0.1 0.05", "newmtl Blue", "Kd 0 0.33 0.75"]);
                var obj = OBJConverter.ParseOBJ(File.ReadLines(Path.Combine(dir, "bricks.obj")));
                Assert.AreEqual(6, obj.Vertices.Count, "vertices on both materials' faces are kept apart");

                string? note = VertexColours.FromMaterials(obj, Path.Combine(dir, "bricks.obj"));
                Assert.IsNotNull(note);
                Assert.IsTrue(obj.HasColours);
                Assert.AreEqual(new Vector4(0.8f, 0.1f, 0.05f, 1), obj.Vertices[0].ColorSet0);
                Assert.AreEqual(new Vector4(0, 0.33f, 0.75f, 1), obj.Vertices[^1].ColorSet0);

                // Blender writes Kd linear: it's turned into the sRGB the game's colours are
                File.WriteAllLines(Path.Combine(dir, "blender.obj"), ["# Blender 4.2.0", "mtllib blender.mtl", "v 0 0 0", "v 1 0 0", "v 0 1 0", "v 1 1 0",
                    "usemtl A", "f 1 2 3", "usemtl B", "f 2 4 3"]);
                File.WriteAllLines(Path.Combine(dir, "blender.mtl"), ["newmtl A", "Kd 0.214 0.214 0.214", "newmtl B", "Kd 1 1 1"]);
                var blender = OBJConverter.ParseOBJ(File.ReadLines(Path.Combine(dir, "blender.obj")));
                VertexColours.FromMaterials(blender, Path.Combine(dir, "blender.obj"));
                Assert.AreEqual(0.5f, blender.Vertices[0].ColorSet0.X, 0.01f);

                // one grey material is just a default: left alone
                File.WriteAllLines(Path.Combine(dir, "plain.obj"), ["mtllib plain.mtl", "v 0 0 0", "v 1 0 0", "v 0 1 0", "usemtl Grey", "f 1 2 3"]);
                File.WriteAllLines(Path.Combine(dir, "plain.mtl"), ["newmtl Grey", "Kd 0.8 0.8 0.8"]);
                var plain = OBJConverter.ParseOBJ(File.ReadLines(Path.Combine(dir, "plain.obj")));
                Assert.IsNull(VertexColours.FromMaterials(plain, Path.Combine(dir, "plain.obj")));
                Assert.IsFalse(plain.HasColours);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void SeparatePiecesAreRegions()
        {
            // a quad split by a UV seam (two vertices in the same places) and a separate triangle
            var v = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                new Vector3(5, 0, 0), new Vector3(6, 0, 0), new Vector3(5, 1, 0) }
                .Select(p => new Vertex { Position = p, ColorSet0 = Vector4.One }).ToArray();
            ushort[] indices = [0, 1, 2, 3, 4, 5, 6, 7, 8];

            int[] region = VertexColours.FindRegions(v, indices, RegionSplit.Auto, out var regions, out var used);
            Assert.AreEqual(RegionSplit.Pieces, used, "all one colour, so it splits by piece");
            Assert.AreEqual(2, regions.Count);
            Assert.AreEqual(6, regions[0].Vertices);
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, region);

            v[6].ColorSet0 = v[7].ColorSet0 = v[8].ColorSet0 = new Vector4(0, 0, 1, 1);
            VertexColours.FindRegions(v, indices, RegionSplit.Auto, out regions, out used);
            Assert.AreEqual(RegionSplit.Colours, used);
            Assert.AreEqual(2, regions.Count);
        }

        [TestMethod]
        public void PaintingKeepsTheOldBuffersForUndo()
        {
            var scene = Load(@"CHARS\SUPER_CHARACTER\HAT\HAT_ARES_DX11.GSC");
            var meshes = scene.MeshSceneBlock.Meshes;
            var mesh = meshes[0];
            var oldBuffers = mesh.VertexBuffers.ToArray();
            var before = OBJConverter.ReadVertices(mesh);

            var colours = Enumerable.Range(0, before.Length).Select(i => (Vector3?)(i % 2 == 0 ? new Vector3(1, 0, 0) : null)).ToArray();
            Assert.IsTrue(VertexColours.Paint(mesh, meshes, colours));

            var after = OBJConverter.ReadVertices(mesh);
            Assert.AreEqual(new Vector3(1, 0, 0), VertexColours.StoredRgb(after[0]));
            Assert.AreEqual(VertexColours.StoredRgb(before[1]), VertexColours.StoredRgb(after[1]), "vertices without a colour keep theirs");
            Assert.AreEqual(before[0].ColorSet0.W, after[0].ColorSet0.W, "alpha is kept");
            Assert.AreEqual(before[0].Position, after[0].Position);
            Assert.IsTrue(oldBuffers.Any(b => !mesh.VertexBuffers.Contains(b)), "the coloured buffer is a copy");
            mesh.VertexBuffers = oldBuffers;
            Assert.AreEqual(VertexColours.StoredRgb(before[0]), VertexColours.StoredRgb(OBJConverter.ReadVertices(mesh)[0]), "the old buffer is unchanged");
        }

        /// <summary>A small .UEMODEL as FModel writes one (uncompressed), with two colour regions.</summary>
        private static byte[] SmallUEModel()
        {
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            void Str(string s) { w.Write(s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }
            void Chunk(string name, int count, Action body)
            {
                var inner = new MemoryStream();
                var saved = w;
                w = new BinaryWriter(inner);
                body();
                w = saved;
                Str(name);
                w.Write((int)inner.Length + 4); // counting the count
                w.Write(count);
                w.Write(inner.ToArray());
            }
            w.Write(Encoding.ASCII.GetBytes("UEFORMAT"));
            Str("UEMODEL");
            w.Write((byte)10);
            Str("SKM_Test");
            Str("/Game/SKM_Test.SKM_Test");
            w.Write((byte)0); // not compressed
            Str("LODS"); w.Write(4); w.Write(1);
            Str("LOD0"); w.Write(4); w.Write(0);
            // Unreal: Z up, centimetres
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 0, 100), new Vector3(100, 0, 100) };
            Chunk("VERTICES", 4, () => { foreach (var p in positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); } });
            Chunk("NORMALS", 4, () => { for (int i = 0; i < 4; i++) { w.Write(1f); w.Write(0f); w.Write(-1f); w.Write(0f); } });
            Chunk("TEXCOORDS", 1, () => { Str("UV0"); w.Write(4); for (int i = 0; i < 4; i++) { w.Write(i * 0.25f); w.Write(0.5f); } });
            Chunk("INDICES", 6, () => { foreach (int i in new[] { 0, 1, 2, 2, 1, 3 }) w.Write(i); });
            Chunk("VERTEXCOLORS", 1, () => { Str("COL0"); w.Write(4); w.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 61, 61, 61, 255, 60, 60, 60, 255 }); });
            Chunk("MATERIALS", 1, () => { Str("WorldGridMaterial"); Str("/Engine/EngineMaterials/WorldGridMaterial.WorldGridMaterial"); w.Write(0); w.Write(2); });
            return o.ToArray();
        }

        [TestMethod]
        public void UEModelIsReadWithItsRegions()
        {
            string dir = TempFolder();
            try
            {
                string path = Path.Combine(dir, "test.uemodel");
                File.WriteAllBytes(path, SmallUEModel());
                var obj = UEModel.Read(path, out var notes);

                Assert.AreEqual(4, obj.Vertices.Count);
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 2, 1, 3 }, obj.Indices);
                Assert.AreEqual(new Vector3(1, 0, 0), obj.Vertices[1].Position, "centimetres to metres");
                Assert.AreEqual(new Vector3(0, 1, 0), obj.Vertices[2].Position, "Unreal's Z up is the game's Y up");
                Assert.AreEqual(new Vector3(0, 0, -1), obj.Vertices[0].Normal);
                Assert.AreEqual(new Vector4(0.5f, 0.5f, 0, 0), obj.Vertices[2].UVSet01);
                Assert.IsTrue(obj.HasColours && obj.HasNormals && obj.HasUVs);

                // 60 and 61 grey are one region, so two placeholder greys
                Assert.AreEqual(obj.Vertices[0].ColorSet0, obj.Vertices[1].ColorSet0);
                Assert.AreEqual(obj.Vertices[2].ColorSet0, obj.Vertices[3].ColorSet0);
                Assert.AreNotEqual(obj.Vertices[0].ColorSet0, obj.Vertices[2].ColorSet0);
                Assert.IsTrue(notes.Any(n => n.Contains("2 colour regions")), string.Join("\n", notes));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void FortniteHeadpieceKeepsItsRegionsOnAHat()
        {
            string path = Environment.GetEnvironmentVariable("BWL_UEMODEL")
                ?? @"C:\Users\glenn\Downloads\The Batman Who Laughs Lego Asset\The Batman Who Laughs Lego Asset\SKM_HeadAcc_C0058.uemodel";
            if (!File.Exists(path))
                Assert.Inconclusive($"Missing {path}");

            var obj = UEModel.Read(path, out _);
            Assert.AreEqual(3553, obj.Vertices.Count);
            Assert.AreEqual(17238, obj.Indices.Count);
            // the same place as the OBJ Blender made from it (its first vertex: 0.000001 0.742932 -0.096850)
            Assert.AreEqual(0.742932f, obj.Vertices[0].Position.Y, 1e-5f);
            Assert.AreEqual(-0.09685f, obj.Vertices[0].Position.Z, 1e-5f);

            var scene = Load(@"CHARS\SUPER_CHARACTER\HAT\HAT_ARES_DX11.GSC");
            var mesh = scene.MeshSceneBlock.Meshes[0];
            OBJConverter.ReplaceMeshData(mesh, obj, "bwl.uemodel", scene.MeshSceneBlock.Meshes, headPart: true);
            VertexColours.FindRegions(OBJConverter.ReadVertices(mesh), OBJConverter.ReadIndices(mesh), RegionSplit.Auto, out var regions, out var used);
            Assert.AreEqual(RegionSplit.Colours, used);
            CollectionAssert.AreEqual(new[] { 2085, 1323, 145 }, regions.Select(r => r.Vertices).ToArray(), "cowl and ears, band and spikes, inside of the band");
        }
    }
}

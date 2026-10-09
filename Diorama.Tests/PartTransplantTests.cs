using Diorama.Core;
using Diorama.Core.Filetypes.GSC.Components.RESH;
using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor;

namespace Diorama.Tests
{
    /// <summary>
    /// Bringing LEGO Marvel's Avengers' Falcon wings into LEGO DC Super-Villains' feather wings (tried in game: they show
    /// and move on Hawkman). Needs both installs; set DIORAMA_GAME_PATH / DIORAMA_OTHER_GAME_PATH to point elsewhere.
    /// </summary>
    [TestClass]
    public class PartTransplantTests
    {
        static string Game => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";
        static string Other => Environment.GetEnvironmentVariable("DIORAMA_OTHER_GAME_PATH") ?? @"D:\Games\LEGO Marvel's Avengers";

        static GScene_4F Load(string path)
        {
            if (!File.Exists(path)) Assert.Inconclusive($"{path} isn't here");
            var input = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
            input.SetFileLocation(new FilesystemFileLocation(path));
            return (GScene_4F)GScene.Parse(input);
        }

        [TestMethod]
        public void FalconWingsIntoFeatherWings()
        {
            var source = Load(Path.Combine(Other, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\WINGS_FALCON_AOU_DX11.GHG"));
            var target = Load(Path.Combine(Game, @"CHARS\SUPER_CHARACTER\WINGS_FEATHER\WINGS_FEATHER_DX11.GHG"));

            var notes = PartTransplant.Transplant(source, target, out var changed);
            foreach (var n in notes) Console.WriteLine(n);
            Assert.IsTrue(changed.Count > 0);
            Assert.IsTrue(notes.Any(n => n.Contains("layer mask 2")), "the part's .CD needs telling which layer to draw");

            var buffer = new MemoryStream();
            using (var output = new RawFile(buffer)) target.Write(output, new GSerializationContext());
            byte[] bytes = buffer.ToArray();
            string? keep = Environment.GetEnvironmentVariable("DIORAMA_TRANSPLANT_OUT");
            if (keep != null) File.WriteAllBytes(keep, bytes);

            // The result reads back, in this game's version, with the other part's geometry in every LOD.
            var back = (GScene_4F)GScene.Parse(new RawFile(new MemoryStream(bytes, false)).Also(f => f.SetFileLocation(new FilesystemFileLocation("x.GHG"))));
            Assert.AreEqual(target.NU20Version, back.NU20Version);
            for (int lod = 0; lod < back.CharacterData.Count; lod++)
            {
                int expected = PartTransplant.MeshesOf(source, Math.Min(lod, source.CharacterData.Count - 1)).Where(m => !m.Breakup).Select(m => m.Mesh).Distinct().Sum(m => (int)m.VerticesCount);
                var main = PartTransplant.MeshesOf(back, lod).Where(m => !m.Breakup).Select(m => m.Mesh).First();
                Assert.AreEqual(expected, (int)main.VerticesCount, $"LOD {lod}");
                Assert.IsTrue(main.SkinMtxMap.Where(j => j != 255).All(j => j < back.CharacterData[0].JointData.Count));
            }
        }
    }

    /// <summary>
    /// Falcon's wings brought in with their textures as a new part (tried in game on Hawkman and Batman: textured,
    /// backpack holes and all, and they move with the feather wings' animations).
    /// </summary>
    [TestClass]
    public class PartImportTests
    {
        static string Game => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";
        static string Other => Environment.GetEnvironmentVariable("DIORAMA_OTHER_GAME_PATH") ?? @"D:\Games\LEGO Marvel's Avengers";

        static GScene_4F Load(string path)
        {
            var input = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
            input.SetFileLocation(new FilesystemFileLocation(path));
            return (GScene_4F)GScene.Parse(input);
        }

        static NuResourceHeader ReadHeader(string path)
        {
            var header = new NuResourceHeader();
            using var input = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
            header.Handle(new SchemaSerializer(input, false), 0);
            return header;
        }

        [TestMethod]
        public void FalconWingsWithTextures()
        {
            string source = Path.Combine(Other, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\WINGS_FALCON_AOU_DX11.GHG");
            string basepart = Path.Combine(Game, @"CHARS\SUPER_CHARACTER\WINGS_FEATHER\WINGS_FEATHER_DX11.GHG");
            if (!File.Exists(source) || !File.Exists(basepart)) Assert.Inconclusive("needs both games");
            string dir = Path.Combine(Path.GetTempPath(), "DioramaPartImport", @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT");
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, "WINGS_FALCON_AOU_DX11.GHG");

            var notes = PartImport.Build(source, basepart, output);
            foreach (var n in notes) Console.WriteLine(n);

            // the model: the new texture names, and every material's texture bindings point at the texture file's
            var model = Load(output);
            var textures = Diorama.Core.Filetypes.TEXTURES.NxgTextures.Read(Path.Combine(dir, "WINGS_FALCON_AOU_DX11.NXG_TEXTURES")).TextureSet.Textures;
            Assert.AreEqual(2, textures.Length);
            CollectionAssert.AreEqual(textures.Select(t => t.Header.Name).ToList(), model.Metadata.MetaStrings.Select(s => s.Value).ToList());
            Assert.AreEqual(1024, textures[0].Width);
            Assert.AreEqual(11, textures[0].MipCount);
            foreach (var m in model.MaterialBlock.Materials.OfType<NuMaterialData_E0>())
            {
                var bound = m.PixelFixupData.Concat(m.VertexFixupData ?? new()).Where(h => h.Checksum.Any(x => x != 0)).ToList();
                Assert.IsTrue(bound.Count >= 2, m.MaterialName);
                foreach (var h in bound)
                    Assert.IsTrue(textures.Any(t => t.Header.Checksum.SequenceEqual(h.Checksum) && t.Header.Name == h.Name), $"{m.MaterialName} binds {h.Name}, which the texture file doesn't have");
            }

            // the files it names are its own, in its folder
            var named = model.ResourceHeader.Files();
            Console.WriteLine(string.Join(" | ", named));
            CollectionAssert.Contains(named, @"chars\super_character\torso_attachment\wings_falcon_aou_dx11.nxg_textures");
            CollectionAssert.Contains(named, @"chars\super_character\torso_attachment\wings_falcon_aou.shaders");
            foreach (var file in new[] { "WINGS_FALCON_AOU_DX11.GHG.RES", "WINGS_FALCON_AOU.SHADERS" })
            {
                var listed = ReadHeader(Path.Combine(dir, file)).Files();
                Console.WriteLine(file + ": " + string.Join(" | ", listed));
                Assert.IsTrue(listed.Count > 0 && listed.All(p => p.Contains("wings_falcon_aou") || !p.Contains("wings_feather")), file);
                Assert.IsFalse(listed.Any(p => p.Contains("wings_feather")), file);
            }
        }

        /// <summary>
        /// Peggy Carter's hair (hat and hair) on Aquaman's long hair: its top joint is named after the part
        /// (SkinnedHair_PeggyCarter, not HairRoot) and is matched anyway. Tried in game on a copy of Sea King.
        /// </summary>
        [TestMethod]
        public void HairWhoseTopJointIsNamedAfterIt()
        {
            string source = Path.Combine(Other, @"CHARS\SUPER_CHARACTER\SKINNEDHAIR\SKINNEDHAIR_PEGGYCARTER_DX11.GHG");
            string basepart = Path.Combine(Game, @"CHARS\SUPER_CHARACTER\SKINNEDHAIR\SKINNEDHAIR_AQUAMAN_LONG_DX11.GHG");
            if (!File.Exists(source) || !File.Exists(basepart)) Assert.Inconclusive("needs both games");
            string dir = Path.Combine(Path.GetTempPath(), "DioramaPartImport", @"CHARS\SUPER_CHARACTER\SKINNEDHAIR");
            Directory.CreateDirectory(dir);
            var notes = PartImport.Build(source, basepart, Path.Combine(dir, "SKINNEDHAIR_PEGGYCARTER_DX11.GHG"));
            foreach (var n in notes) Console.WriteLine(n);
            Assert.IsFalse(notes.Any(n => n.StartsWith("Warning")), "every joint should be matched");
        }

        /// <summary>
        /// Mach-5's armour on the SWAT armour, whose vertices have no tangents: it takes another part's material laid out
        /// like that, in the same material version (the hot dog guy's doesn't fit). Tried in game on a copy of a SWAT officer.
        /// </summary>
        [TestMethod]
        public void ArmourOnAnotherLayout()
        {
            string source = Path.Combine(Other, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\ARMOUR_MACH5_DX11.GHG");
            string basepart = Path.Combine(Game, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\TORSO_ATTACHMENT_ARMOUR_SWAT_DX11.GHG");
            if (!File.Exists(source) || !File.Exists(basepart)) Assert.Inconclusive("needs both games");
            string dir = Path.Combine(Path.GetTempPath(), "DioramaPartImport", @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT");
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, "ARMOUR_MACH5_DX11.GHG");
            var notes = PartImport.Build(source, basepart, output);
            foreach (var n in notes) Console.WriteLine(n);
            Assert.IsFalse(notes[0].Contains("HOTDOGGUY"), "the hot dog guy's material is laid out differently");
            var model = Load(output);
            var block = model.MaterialBlock.Materials;
            Assert.IsTrue(block.Where(m => m != null).Select(m => m.Version).Distinct().Count() == 1, "every material in the block's version");
            for (int lod = 0; lod < model.CharacterData.Count; lod++)
                foreach (var (mesh, _, _) in PartTransplant.MeshesOf(model, lod))
                    Assert.IsTrue((mesh.SkinMtxMap?.Count ?? 0) <= 27, "a mesh's joint map holds 27");
        }

        /// <summary>
        /// The Blaster backpack has no texture of its own (vertex colours) and pieces hung on a joint without weights,
        /// which would fold away when skinned: they move with the backpack's joint. Tried in game on a copy of a SWAT
        /// officer: shows in its colours (the gun in its rest pose, as Avengers' animations don't come along).
        /// </summary>
        [TestMethod]
        public void PlainPartWithRigidPieces()
        {
            string source = Path.Combine(Other, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\BACKPACK_BLASTER_DX11.GHG");
            string basepart = Path.Combine(Game, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\BACKPACK_BEDROLL_DX11.GHG");
            if (!File.Exists(source) || !File.Exists(basepart)) Assert.Inconclusive("needs both games");
            string dir = Path.Combine(Path.GetTempPath(), "DioramaPartImport", @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT");
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, "BACKPACK_BLASTER_DX11.GHG");
            var notes = PartImport.Build(source, basepart, output);
            foreach (var n in notes) Console.WriteLine(n);
            StringAssert.Contains(notes[0], "none of its own");
            Assert.IsTrue(notes.Any(n => n.Contains("weren't weighted")));
            // its see-through pieces: a mesh of their own, drawn with a blending material added to the model (tried in game)
            Assert.IsTrue(notes.Any(n => n.Contains("see-through triangles in 1 mesh")));
            var built = Load(output);
            var clear = built.MaterialBlock.Materials.Last();
            Assert.IsTrue(clear.MaterialName.EndsWith(":SeeThrough") && clear.blendMode != 0);
            foreach (var (mesh, _, breakup) in PartTransplant.MeshesOf(Load(output), 0))
                if (!breakup)
                    Assert.IsTrue(OBJConverter.ReadVertices(mesh).All(v => v.BlendWeights.X + v.BlendWeights.Y + v.BlendWeights.Z + v.BlendWeights.W > 0.5f), "every vertex weighted");
        }

        /// <summary>
        /// Citizen V's armour has a 1024x1024 texture: the cells grow to fit it. Tried in game on copies of a SWAT officer
        /// and of Adam Strange (moving with him as he flies). The bedroll's own textures stay in the file, ahead of it.
        /// </summary>
        [TestMethod]
        public void BigTexture()
        {
            string source = Path.Combine(Other, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\ARMOUR_CITIZENV_DX11.GHG");
            string basepart = Path.Combine(Game, @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT\BACKPACK_BEDROLL_DX11.GHG");
            if (!File.Exists(source) || !File.Exists(basepart)) Assert.Inconclusive("needs both games");
            string dir = Path.Combine(Path.GetTempPath(), "DioramaPartImport", @"CHARS\SUPER_CHARACTER\TORSO_ATTACHMENT");
            Directory.CreateDirectory(dir);
            var notes = PartImport.Build(source, basepart, Path.Combine(dir, "ARMOUR_CITIZENV_DX11.GHG"));
            foreach (var n in notes) Console.WriteLine(n);
            var packed = Diorama.Core.Filetypes.TEXTURES.NxgTextures.Read(Path.Combine(dir, "ARMOUR_CITIZENV_DX11.NXG_TEXTURES")).TextureSet.Textures.Single(t => t.Header.Name.EndsWith("armour_citizenv_diff.nut"));
            Assert.AreEqual(2048, packed.Width);
            Assert.AreEqual(12, packed.MipCount);
        }

        /// <summary>The game's resource-header files read and write back unchanged, and renaming keeps every file listed.</summary>
        [TestMethod]
        public void ResourceFilesRename()
        {
            string folder = Path.Combine(Game, @"CHARS\SUPER_CHARACTER");
            if (!Directory.Exists(folder)) Assert.Inconclusive("needs the game");
            int count = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*.GHG.RES", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(folder, "*.SHADERS", SearchOption.AllDirectories)))
            {
                byte[] bytes = File.ReadAllBytes(path);
                Assert.IsTrue(PartImport.RenamedResourceFile(path, p => p) is { } same, path);
                var before = ReadHeader(path).Files();
                string temp = Path.GetTempFileName();
                File.WriteAllBytes(temp, PartImport.RenamedResourceFile(path, p => p.Replace("chars", "x"))!);
                var after = ReadHeader(temp).Files();
                File.Delete(temp);
                CollectionAssert.AreEquivalent(before.Select(p => p.Replace("chars", "x")).ToList(), after, path);
                count++;
            }
            Console.WriteLine($"{count} files");
            Assert.IsTrue(count > 100);
        }
    }

    static class Extensions
    {
        public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
    }
}

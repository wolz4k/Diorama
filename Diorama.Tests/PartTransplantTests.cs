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

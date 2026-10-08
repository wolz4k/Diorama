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

    static class Extensions
    {
        public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
    }
}

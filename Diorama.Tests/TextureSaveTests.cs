using Diorama.Core;
using Diorama.Core.Filetypes.TEXTURES;

namespace Diorama.Tests
{
    /// <summary>
    /// Save Textures (NxgTextures.ToBytes) on real texture files read into memory. Over the whole install, 3911 of 3911
    /// come back byte for byte; it used to swap in a new header list, which wrote "ROTV" where DC Super-Villains has zeros.
    /// </summary>
    [TestClass]
    public class TextureSaveTests
    {
        private static string GamePath => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";

        private static (NxgTextures Textures, byte[] Original) Load(string relativePath)
        {
            string path = Path.Join(GamePath, relativePath);
            if (!File.Exists(path))
                Assert.Inconclusive($"Missing {path}");

            byte[] original = File.ReadAllBytes(path);
            VectorMarkers.Reset();
            using var input = new RawFile(new MemoryStream(original, false));
            input.SetFileLocation(new FilesystemFileLocation(path));
            return (NxgTextures.Read(input), original);
        }

        [TestMethod]
        [DataRow(@"CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.NXG_TEXTURES")]
        [DataRow(@"CHARS\ITEMS\CLAYFACE_BIGFIGMACE_DX11.NXG_TEXTURES")] // uses the LEGO texture page
        [DataRow(@"LEVELS\HUB\ARKHAMFOREST\ARKHAMGROUND_DX11.NXG_TEXTURES")] // names textures kept in a shared scene
        public void UnchangedTexturesSaveAsRead(string relativePath)
        {
            var (textures, original) = Load(relativePath);
            byte[] saved = textures.ToBytes(textures.TextureSet.Textures.ToList());
            CollectionAssert.AreEqual(original, saved);
        }

        [TestMethod]
        public void ReplacedTextureIsSavedAndTheRestKept()
        {
            var (textures, original) = Load(@"CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.NXG_TEXTURES");
            var list = textures.TextureSet.Textures.ToList();
            Assert.IsTrue(list.Count > 1);

            // put the first texture's image in the last slot, as Edit Textures' replace does (it keeps the slot's header)
            var last = list[^1];
            var replacement = new NuTexture { Header = last.Header, ImageHeader = list[0].ImageHeader, Data = list[0].Data };
            list[^1] = replacement;
            byte[] saved = textures.ToBytes(list);

            VectorMarkers.Reset();
            using var input = new RawFile(new MemoryStream(saved, false));
            input.SetFileLocation(new FilesystemFileLocation(Path.Join(GamePath, "saved.NXG_TEXTURES")));
            var reread = NxgTextures.Read(input).TextureSet.Textures;
            Assert.AreEqual(list.Count, reread.Length);
            CollectionAssert.AreEqual(list[0].Data, reread[^1].Data);
            Assert.AreEqual(last.Header.Name, reread[^1].Header.Name);
            CollectionAssert.AreEqual(list[1].Data, reread[1].Data);
        }
    }
}

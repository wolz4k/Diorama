using Diorama.Core.Filetypes.TEXTURES;

namespace Diorama.Tests
{
    /// <summary>
    /// About half of DC Super-Villains' texture files point at the shared LEGO texture page (/LEGOTpage/...)
    /// instead of holding their own copy. In a loose install it must be found beside the game's folders even
    /// when no game folder is set in Settings.
    /// </summary>
    [TestClass]
    public class TexturePageTests
    {
        [TestMethod]
        public void SharedTexturePageLoadsFromALooseInstall()
        {
            string path = @"D:\Games\LDC\CHARS\ITEMS\CLAYFACE_BIGFIGMACE_DX11.NXG_TEXTURES";
            if (!File.Exists(path))
                Assert.Inconclusive($"Missing {path}");

            var textures = NxgTextures.Read(path);

            Assert.IsNotNull(textures, "the texture file failed to load");
            Assert.IsTrue(textures.TextureSet.Textures.Any(t => t.Header.Name == string.Empty), "this file is expected to use the shared page");
            Assert.IsTrue(textures.TextureSet.Textures.All(t => t.Data != null), "every texture, including the shared page, has its pixels");
        }
    }
}

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
        public void ExportedDdsLoadsBackAsTheSameImage()
        {
            // Edit Textures exports ImageHeader + Data as a .DDS; loading that file is what Replace does
            var (textures, _) = Load(@"CHARS\BIGFIG\DARKSEID\DARKSEID_DX11.NXG_TEXTURES");
            var original = textures.TextureSet.Textures[0];
            byte[] dds = original.ImageHeader.Concat(original.Data).ToArray();

            using var file = new RawFile(new MemoryStream(dds, false));
            var loaded = NuTexture.Load(file, new Diorama.Core.Filetypes.GSC.Components.NuTexGenHdr { Name = "replacement" });

            Assert.AreEqual(original.Width, loaded.Width);
            Assert.AreEqual(original.Height, loaded.Height);
            Assert.AreEqual(original.FourCC, loaded.FourCC);
            Assert.AreEqual(original.MipCount, loaded.MipCount);
            CollectionAssert.AreEqual(original.ImageHeader, loaded.ImageHeader);
            CollectionAssert.AreEqual(original.Data, loaded.Data);
        }

        [TestMethod]
        public void ObjTextureBecomesAGameTexture()
        {
            // an OBJ from elsewhere naming a PNG in its .MTL, with a picture for another part beside it
            string dir = Path.Combine(Path.GetTempPath(), "DioramaObjTexture_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var bitmap = new SkiaSharp.SKBitmap(300, 200))
                {
                    for (int y = 0; y < 200; y++)
                        for (int x = 0; x < 300; x++)
                            bitmap.SetPixel(x, y, x < 150 ? SkiaSharp.SKColors.Red : SkiaSharp.SKColors.Blue);
                    using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(dir, "skin.png"), png.ToArray());
                    File.WriteAllBytes(Path.Combine(dir, "torso.png"), png.ToArray());
                }
                File.WriteAllLines(Path.Combine(dir, "hat.obj"), ["mtllib hat.mtl", "o Hat", "v 0 0 0", "v 1 0 0", "v 0 1 0", "vt 0 0", "usemtl Skin", "f 1/1 2/1 3/1"]);
                File.WriteAllLines(Path.Combine(dir, "hat.mtl"), ["newmtl Other", "map_Kd torso.png", "newmtl Skin", "Kd 1 1 1", @"map_Kd -s 1 1 1 C:\elsewhere\skin.png"]);

                string? picture = Diorama.Editor.ObjTexture.Find(Path.Combine(dir, "hat.obj"));
                Assert.AreEqual(Path.Combine(dir, "skin.png"), picture);

                var (textures, _) = Load(@"CHARS\SUPER_CHARACTER\HAT\HAT_ARES_DX11.NXG_TEXTURES");
                var old = textures.TextureSet.Textures.First(t => t.Data != null && t.Width > 0);
                uint oldFourCC = old.FourCC;
                var made = Diorama.Editor.ObjTexture.Build(picture!, old, out var notes);

                Assert.AreEqual(256, made.Width);
                Assert.AreEqual(256, made.Height);
                Assert.AreEqual(9, made.MipCount);
                Assert.AreEqual(9u, old.Header.Level); // the entry now counts the new image's mipmaps
                Assert.AreEqual(oldFourCC == 0x35545844 ? 0x35545844u : 0x31545844u, made.FourCC);
                Assert.AreEqual(1, notes.Count); // it was scaled to a power of two

                var decoded = DdsDecoder.Decode(made.ImageHeader.Concat(made.Data).ToArray());
                Assert.IsNotNull(decoded);
                int left = (128 * 256 + 20) * 4, right = (128 * 256 + 230) * 4;
                Assert.IsTrue(decoded.Rgba[left] > 200 && decoded.Rgba[left + 2] < 50, "left half red");
                Assert.IsTrue(decoded.Rgba[right + 2] > 200 && decoded.Rgba[right] < 50, "right half blue");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void NonDdsFileIsRefused()
        {
            using var file = new RawFile(new MemoryStream(new byte[256], false));
            Assert.ThrowsException<InvalidDataException>(() => NuTexture.Load(file, new Diorama.Core.Filetypes.GSC.Components.NuTexGenHdr()));
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

using Diorama.Core.Filetypes.GSC;
using Diorama.Editor;

namespace Diorama.Tests
{
    /// <summary>
    /// The editor's own save steps (not just GScene.Write), on real game files read into memory.
    /// </summary>
    [TestClass]
    public class SaveTests
    {
        private static string GamePath => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";

        private static (GScene Scene, EditorScene Editor) Load(string relativePath)
        {
            string path = Path.Join(GamePath, relativePath);
            if (!File.Exists(path))
                Assert.Inconclusive($"Missing {path}");

            using var input = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
            input.SetFileLocation(new FilesystemFileLocation(path));
            var scene = GScene.Parse(input);
            var editor = new EditorScene { OriginalScene = scene };
            foreach (var special in scene.DisplayScene.SpecialObjects)
            {
                special.Name ??= scene.NameTable.Names.GetString((int)special.NameIndex);
                editor.SpecialObjects.Add(new EditorSpecialObject(special));
            }
            return (scene, editor);
        }

        /// <summary>
        /// The name table also holds joint, layer and other names that point into it by offset. Saving used to rebuild it
        /// from the special objects alone, dropping the rest (a bow's jnt_bow_rest_* joints, its TT0_mainNXG layer).
        /// </summary>
        [TestMethod]
        public void SavingKeepsEveryNameInTheTable()
        {
            var (scene, editor) = Load(@"CHARS\CUSTOMISER_BOW\BOW_B_ITEM_DX11.GHG");
            byte[] before = (byte[])scene.NameTable.Names.Buffer.Clone();

            GSceneConverter.CreateNameTable(editor);
            CollectionAssert.AreEqual(before, scene.NameTable.Names.Buffer, "nothing renamed: the table is unchanged");

            var special = (EditorSpecialObject)editor.SpecialObjects[0];
            special.Name = "MyRenamedPart";
            GSceneConverter.CreateNameTable(editor);

            var names = scene.NameTable.Names;
            Assert.AreEqual("MyRenamedPart", names.GetString((int)special.Original.NameIndex));
            CollectionAssert.AreEqual(before, names.Buffer.Take(before.Length).ToArray(), "renaming only adds to the end");
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(names.Buffer), "jnt_bow_rest_Upper01");
        }
    }
}

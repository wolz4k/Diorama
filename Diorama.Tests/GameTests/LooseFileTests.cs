using Diorama.Core.Filetypes.GSC;
using System.Collections.Concurrent;

namespace Diorama.Tests.GameTests
{
    /// <summary>
    /// Parses and round-trips every .GSC/.GHG in an extracted game install (loose files, no .DAT archives).
    /// Set DIORAMA_GAME_PATH to point it elsewhere. Files are read into memory, so the install is never written.
    /// </summary>
    [TestClass]
    public class LooseFileTests
    {
        private static string GamePath => Environment.GetEnvironmentVariable("DIORAMA_GAME_PATH") ?? @"D:\Games\LDC";

        private static IEnumerable<string> SceneFiles()
        {
            if (!Directory.Exists(GamePath))
                Assert.Inconclusive($"No game install at {GamePath}");

            return Directory.EnumerateFiles(GamePath, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".GSC", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".GHG", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        [TestCategory("LooseFiles")]
        public void RoundTripLooseScenes()
        {
            // DC Super-Villains leaves the vector markers zeroed; a save keeps whatever the file had, whatever the app setting
            AppSettings.ShouldWriteROTV = true;

            var failures = new ConcurrentDictionary<string, ConcurrentBag<string>>();
            int total = 0, parsed = 0, identical = 0;

            Parallel.ForEach(SceneFiles(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
            {
                Interlocked.Increment(ref total);
                byte[] original = File.ReadAllBytes(path);

                GScene scene;
                try
                {
                    using var input = new RawFile(new MemoryStream(original, writable: false));
                    input.SetFileLocation(new FilesystemFileLocation(path)); // Parse reads the scene's path from it
                    scene = GScene.Parse(input);
                    Interlocked.Increment(ref parsed);
                }
                catch (Exception e)
                {
                    failures.GetOrAdd("Parse: " + e.Message, _ => new()).Add(path);
                    return;
                }

                try
                {
                    var buffer = new MemoryStream();
                    using (var output = new RawFile(buffer))
                    {
                        scene.Write(output, new GSerializationContext());
                    }

                    if (buffer.ToArray().AsSpan().SequenceEqual(original))
                        Interlocked.Increment(ref identical);
                    else
                        failures.GetOrAdd("Write differs from original", _ => new()).Add(path);
                }
                catch (Exception e)
                {
                    failures.GetOrAdd("Write: " + e.Message, _ => new()).Add(path);
                }
            });

            foreach (var (reason, paths) in failures.OrderByDescending(f => f.Value.Count))
            {
                Console.WriteLine($"{reason} - {paths.Count}");
                foreach (var path in paths.OrderBy(p => p).Take(10))
                    Console.WriteLine($"\t{Path.GetRelativePath(GamePath, path)}");
            }

            Console.WriteLine("--- FINAL RESULT ---");
            Console.WriteLine($"parsed {parsed}/{total}, identical after write {identical}/{total}");
        }
    }
}

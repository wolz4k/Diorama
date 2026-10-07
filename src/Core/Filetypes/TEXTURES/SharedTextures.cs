using Diorama.Core.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Diorama.Core.Filetypes.TEXTURES
{
    /// <summary>
    /// The hub levels (Arkham, Metropolis, Apokolips, Kansas...) keep most textures in a shared texture scene such as
    /// levels/hub/arkhamforest/arkham_textures_dx11.gsc, listed in each piece's resource header. The piece's own
    /// .NXG_TEXTURES only names those textures (0 x 0, no pixels); this finds the pixels in the shared scene's.
    /// </summary>
    public static class SharedTextures
    {
        // shared scene path -> its textures by name, or null if it couldn't be read; kept, since a hub's pieces share one
        private static readonly Dictionary<string, Dictionary<string, NuTexture>?> loaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="texture"/> is only a name, its pixels kept elsewhere.</summary>
        public static bool IsNameOnly(NuTexture texture) =>
            !string.IsNullOrEmpty(texture.Header?.Name) && (texture.Data == null || texture.Data.Length == 0) && texture.Width == 0;

        /// <summary>The texture called <paramref name="name"/> in the first of <paramref name="sharedScenes"/> that has it.</summary>
        public static (NuTexture Texture, string Scene)? Find(IEnumerable<string> sharedScenes, string name)
        {
            string key = Normalise(name);
            foreach (string scene in sharedScenes)
            {
                if (Load(scene) is { } textures && textures.TryGetValue(key, out var texture))
                    return (texture, scene);
            }
            return null;
        }

        private static Dictionary<string, NuTexture>? Load(string scene)
        {
            lock (loaded)
            {
                if (loaded.TryGetValue(scene, out var known))
                    return known;

                Dictionary<string, NuTexture>? textures = null;
                try
                {
                    using RawFile? file = FileProvider.GetFile(Path.ChangeExtension(scene, ".nxg_textures"));
                    if (file != null && NxgTextures.Read(file)?.TextureSet?.Textures is { } all)
                    {
                        textures = new();
                        foreach (var texture in all.Where(t => !IsNameOnly(t) && t.Data != null && !string.IsNullOrEmpty(t.Header?.Name)))
                            textures.TryAdd(Normalise(texture.Header.Name), texture);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Couldn't read the shared textures of {scene}: {e.Message}");
                }

                loaded[scene] = textures;
                return textures;
            }
        }

        private static string Normalise(string name) => name.Replace('\\', '/').ToLowerInvariant();
    }
}

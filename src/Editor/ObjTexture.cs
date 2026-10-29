using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Rendering;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>
    /// The texture an imported .OBJ comes with, made into a game texture for the part it replaces: a new shape's UVs
    /// only make sense with its own picture.
    /// </summary>
    public static class ObjTexture
    {
        static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".bmp", ".dds"];

        /// <summary>
        /// The picture the OBJ's material names (map_Kd in its .MTL, as Blender writes for an Image Texture), or null.
        /// Only what the material names: a picture that merely sits beside the OBJ is often for another part (a
        /// headpiece downloaded with its figure's torso print).
        /// </summary>
        public static string? Find(string objPath)
        {
            string folder = Path.GetDirectoryName(objPath) ?? "";
            string? mtlFile = null, material = null;
            foreach (string raw in File.ReadLines(objPath))
            {
                string line = raw.Trim();
                if (mtlFile == null && line.StartsWith("mtllib ")) mtlFile = line[7..].Trim();
                else if (material == null && line.StartsWith("usemtl ")) material = line[7..].Trim();
                if (mtlFile != null && material != null) break;
            }
            if (mtlFile == null || !File.Exists(Path.Combine(folder, mtlFile)))
                return null;

            bool inMaterial = material == null; // no usemtl: the first map_Kd will do
            foreach (string raw in File.ReadLines(Path.Combine(folder, mtlFile)))
            {
                string line = raw.Trim();
                if (line.StartsWith("newmtl "))
                    inMaterial = material == null || line[7..].Trim() == material;
                else if (inMaterial && line.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase))
                {
                    string name = line[7..].Trim();
                    // options such as "-s 1 1 1" come before the file name
                    if (name.StartsWith('-')) name = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    foreach (string candidate in new[] { Path.Combine(folder, name), Path.Combine(folder, Path.GetFileName(name.Replace('\\', '/'))) })
                        if (File.Exists(candidate) && Pictures.Contains(Path.GetExtension(candidate).ToLowerInvariant()))
                            return candidate;
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// A game texture of <paramref name="picture"/> in the place of <paramref name="old"/>, keeping its entry (name,
        /// header) and its format: DXT1, or DXT5 when the old one is or the picture has soft transparency. A .DDS is taken as
        /// it is; anything else is scaled to the nearest power-of-two size (up to 2048) and given mipmaps.
        /// </summary>
        public static NuTexture Build(string picture, NuTexture old, out List<string> notes)
        {
            notes = new();
            if (Path.GetExtension(picture).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                return NuTexture.Load(picture, old.Header);

            using var decoded = SKBitmap.Decode(picture) ?? throw new InvalidDataException($"{Path.GetFileName(picture)} couldn't be read as a picture.");
            using var source = decoded.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException($"{Path.GetFileName(picture)} couldn't be converted.");
            int width = PowerOfTwo(source.Width), height = PowerOfTwo(source.Height);
            if (width != source.Width || height != source.Height)
                notes.Add($"{Path.GetFileName(picture)} is {source.Width} x {source.Height}; it was scaled to {width} x {height}, since the game's textures are powers of two.");

            using var top = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High) ?? source;
            return FromImage(new DecodedImage(width, height, top.Bytes), old);
        }

        /// <summary>A game texture of a power-of-two image, with mipmaps, in place of <paramref name="old"/> (see <see cref="Build"/>).</summary>
        public static NuTexture FromImage(DecodedImage image, NuTexture old)
        {
            int width = image.Width, height = image.Height;
            var levels = new List<DecodedImage> { image };
            for (int w = width, h = height; w > 1 || h > 1;)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                levels.Add(Halve(levels[^1], w, h));
            }

            bool soft = levels[0].Rgba.Where((_, i) => i % 4 == 3).Any(a => a is > 0 and < 255);
            bool holes = levels[0].Rgba.Where((_, i) => i % 4 == 3).Any(a => a < 128);
            bool dxt5 = old.FourCC == 0x35545844 || soft;

            var data = new MemoryStream();
            foreach (var level in levels)
            {
                var padded = PadToBlocks(level);
                data.Write(dxt5 ? DxtEncoder.EncodeDxt5(padded) : DxtEncoder.EncodeDxt1(padded));
            }

            byte[] header = Header(old.ImageHeader, width, height, levels.Count, dxt5, holes);
            var dds = new MemoryStream();
            dds.Write(header);
            data.Position = 0;
            data.CopyTo(dds);

            using var file = new RawFile(new MemoryStream(dds.ToArray(), false));
            return NuTexture.Load(file, old.Header);
        }

        /// <summary>
        /// After <paramref name="geo"/>'s mesh was replaced from <paramref name="objPath"/>: puts the picture the OBJ's
        /// material names on the part's texture, or, if it names none, says that the old texture now shows through the
        /// new UVs. Render thread only; <see cref="MeshSnapshot"/> undoes it with the mesh.
        /// </summary>
        /// <param name="colours">Where the mesh's colours came from: colours from the file itself (its materials, a
        /// .UEMODEL's regions) are the whole look, so without a texture of its own the part gets a plain white one.</param>
        public static void Apply(string objPath, EditorGeometryObject geo, EditorScene scene, List<string> notes, ColourSource colours = ColourSource.None)
        {
            var texture = geo.Material?.Diffuse0;
            if (texture == null || RenderTexture.IsWhitePlaceholder(texture) || texture.Original?.Header == null)
                return;

            string? picture = UEModel.IsUEModel(objPath) ? null : Find(objPath);
            if (picture == null)
            {
                if (colours is ColourSource.Materials or ColourSource.Mask)
                    MakeWhite(geo, scene, notes, "its colours come from the file");
                else if (File.ReadLines(objPath).Any(l => l.StartsWith("vt ")))
                    notes.Add($"The OBJ names no texture of its own, so the part shows its old one ({texture.ShortName}) through the new shape's UVs, which " +
                        "usually looks scrambled. Colour by region gives it plain colours (and a white texture) instead; or give it an image in Blender " +
                        "(an Image Texture on its material, exported with the OBJ), or paint one for its UVs and swap it in with Edit Textures.");
                return;
            }

            var old = texture.Original;
            if (Path.GetExtension(picture).Equals(".dds", StringComparison.OrdinalIgnoreCase) && old.ImageHeader != null && old.Data != null
                && File.ReadAllBytes(picture).AsSpan().SequenceEqual([.. old.ImageHeader, .. old.Data]))
                return; // the texture Export wrote beside the part, unchanged

            uint level = old.Header.Level;
            NuTexture made;
            List<string> made_notes;
            try
            {
                made = Build(picture, old, out made_notes);
            }
            catch (Exception ex)
            {
                old.Header.Level = level;
                notes.Add($"The OBJ's texture {Path.GetFileName(picture)} couldn't be used ({ex.Message}), so the part keeps {texture.ShortName}.");
                return;
            }
            if (made.FourCC is not (0x31545844 or 0x33545844 or 0x35545844))
            {
                old.Header.Level = level;
                notes.Add($"The OBJ's texture {Path.GetFileName(picture)} isn't DXT1/DXT5, which the game needs here, so the part keeps {texture.ShortName}. Save it as DXT5 (BC3) with mipmaps, or as a PNG.");
                return;
            }

            texture.Reload(made);
            notes.Add($"Its texture {Path.GetFileName(picture)} (named by the OBJ's material) replaced {texture.ShortName} ({made.Width} x {made.Height}, {(made.FourCC == 0x35545844 ? "DXT5" : "DXT1")}).");
            notes.AddRange(made_notes);
            var mesh = geo.Mesh?.OriginalMesh;
            int others = scene.AllGeometry().Where(g => g.Material?.Diffuse0 == texture && g.Mesh?.OriginalMesh != mesh)
                .Select(g => g.Mesh?.OriginalMesh).Distinct().Count();
            if (others > 0)
                notes.Add($"{others} other part{(others == 1 ? " uses" : "s use")} {texture.ShortName} too and now show{(others == 1 ? "s" : "")} the new picture.");
            notes.Add("Save keeps only the shape: also use Save Textures (the scene's right-click menu) to keep the new texture.");
        }

        /// <summary>Whether the part's texture is already plain white (so painting it white again changes nothing).</summary>
        public static bool IsPlainWhite(RenderTexture? texture) =>
            texture?.Original is { Width: <= 8, Height: <= 8 } t && texture.IsReplaced;

        /// <summary>
        /// Gives the part a plain white texture (8 x 8, DXT1) in place of its own, so only its vertex colours show: the
        /// old picture would show through the new shape's UVs. Render thread only; <see cref="MeshSnapshot"/> undoes it.
        /// </summary>
        public static void MakeWhite(EditorGeometryObject geo, EditorScene scene, List<string> notes, string why)
        {
            var texture = geo.Material?.Diffuse0;
            if (texture == null || RenderTexture.IsWhitePlaceholder(texture) || texture.Original?.Header == null || IsPlainWhite(texture))
                return;

            var white = new byte[8 * 8 * 4];
            Array.Fill(white, (byte)255);
            var old = texture.Original;
            uint level = old.Header.Level;
            try
            {
                texture.Reload(FromImage(new DecodedImage(8, 8, white), old));
            }
            catch (Exception ex)
            {
                old.Header.Level = level;
                notes.Add($"Couldn't give it a plain white texture ({ex.Message}).");
                return;
            }
            notes.Add($"Its texture {texture.ShortName} became plain white, since {why}: the old picture would show through the new shape's UVs.");
            var mesh = geo.Mesh?.OriginalMesh;
            int others = scene.AllGeometry().Where(g => g.Material?.Diffuse0 == texture && g.Mesh?.OriginalMesh != mesh)
                .Select(g => g.Mesh?.OriginalMesh).Distinct().Count();
            if (others > 0)
                notes.Add($"{others} other part{(others == 1 ? " uses" : "s use")} {texture.ShortName} too and now show{(others == 1 ? "s" : "")} white.");
            notes.Add("Save keeps only the shape: also use Save Textures (the scene's right-click menu) to keep the white texture.");
        }

        static int PowerOfTwo(int size)
        {
            int lower = 1;
            while (lower * 2 <= size) lower *= 2;
            int nearest = size - lower <= lower * 2 - size ? lower : lower * 2;
            return Math.Clamp(nearest, 4, 2048);
        }

        /// <summary>The next mipmap: each pixel the average of the 2x2 (or 2x1) it covers, colours weighted by alpha.</summary>
        static DecodedImage Halve(DecodedImage image, int width, int height)
        {
            var rgba = new byte[width * height * 4];
            int sx = image.Width / width, sy = image.Height / height;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    float alpha = 0;
                    for (int dy = 0; dy < sy; dy++)
                        for (int dx = 0; dx < sx; dx++)
                        {
                            int o = ((y * sy + dy) * image.Width + x * sx + dx) * 4;
                            float a = image.Rgba[o + 3] / 255f;
                            sum += new Vector4(image.Rgba[o] * a, image.Rgba[o + 1] * a, image.Rgba[o + 2] * a, image.Rgba[o + 3]);
                            alpha += a;
                        }
                    int n = sx * sy, t = (y * width + x) * 4;
                    float weight = alpha > 0 ? alpha : 1;
                    rgba[t] = (byte)Math.Round(sum.X / weight);
                    rgba[t + 1] = (byte)Math.Round(sum.Y / weight);
                    rgba[t + 2] = (byte)Math.Round(sum.Z / weight);
                    rgba[t + 3] = (byte)Math.Round(sum.W / n);
                }
            return new DecodedImage(width, height, rgba);
        }

        /// <summary>A mipmap smaller than a 4x4 block, repeated to fill one (only its corner is read).</summary>
        static DecodedImage PadToBlocks(DecodedImage image)
        {
            int width = (image.Width + 3) / 4 * 4, height = (image.Height + 3) / 4 * 4;
            if (width == image.Width && height == image.Height)
                return image;
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    Array.Copy(image.Rgba, ((y % image.Height) * image.Width + x % image.Width) * 4, rgba, (y * width + x) * 4, 4);
            return new DecodedImage(width, height, rgba);
        }

        /// <summary>
        /// A DDS header for the new image: the old texture's own (so whatever the game keeps in it stays), or a plain one,
        /// with the size, mipmap count, format and TT's DXT1 alpha marker ("ALPH"/"OPAQ" in caps3) set.
        /// </summary>
        static byte[] Header(byte[]? old, int width, int height, int mips, bool dxt5, bool holes)
        {
            byte[] h = old is { Length: 128 } ? (byte[])old.Clone() : new byte[128];
            void Put(int at, uint value) => BitConverter.GetBytes(value).CopyTo(h, at);
            if (old is not { Length: 128 })
            {
                "DDS "u8.ToArray().CopyTo(h, 0);
                Put(4, 124);
                Put(108, 0x401008); // texture, mipmaps, complex
            }
            Put(76, 32);           // pixel format size
            Put(80, 0x4);          // a compressed format, named by its FourCC
            Array.Clear(h, 88, 20); // no bit counts or masks
            Put(8, 0x000A1007);   // caps, height, width, pixel format, mipmap count, linear size
            Put(12, (uint)height);
            Put(16, (uint)width);
            Put(20, (uint)(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * (dxt5 ? 16 : 8)));
            Put(28, (uint)mips);
            Put(84, dxt5 ? 0x35545844u : 0x31545844u);
            if (!dxt5)
                Put(116, holes ? 0x48504c41u : 0x5141504fu); // ALPH / OPAQ
            return h;
        }
    }
}

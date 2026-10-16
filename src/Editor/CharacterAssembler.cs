using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Editor.Material;
using Diorama.Rendering;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Diorama.Editor
{
    /// <summary>A body part's look, as Flux's CharacterLooks writes it: material number, tint (#RRGGBB) and textures.</summary>
    public record LookMaterial(int Id, string? Tint, List<string> Textures);

    /// <summary>A part a character wears: its model (.GHG, or .GSC plus the object in it), locator, tint and own materials' tints.</summary>
    public record LookPart(string Name, string? Model, string? Object, int Locator, int? Layer, string? Tint, List<LookMaterial> Materials);

    /// <summary>What a character looks like (Flux's CharacterLook, read from its .CD with every file resolved).</summary>
    public record CharacterLook(string Character, string? Body, List<int> Layers, List<LookMaterial> Materials, List<LookPart> Parts, List<string> Notes);

    /// <summary>
    /// Builds a character the way the game puts it together: the body model (SUPER_MINIFIG…) showing the layers its
    /// .CD lists, each body material painted with the .CD's textures over its tint colour, and every part (face, hat,
    /// cape…) placed at its locator on the body's skeleton in bind pose. Render thread only (it makes textures).
    /// </summary>
    public static class CharacterAssembler
    {
        /// <summary>The .CD's material numbers by the body material they colour (the game's names, DX11_…_GAME).</summary>
        static readonly Dictionary<string, int> MaterialIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HEAD_FRONT"] = 1, ["HEAD_BACK"] = 2, ["BODY_FRONT"] = 3, ["BODY_BACK"] = 4, ["BODY_NECK"] = 3,
            ["RIGHT_ARM"] = 5, ["RIGHT_HAND"] = 6, ["HIPS"] = 7, ["RIGHT_LEG"] = 8, ["GLASSES"] = 9,
            ["LEFT_ARM"] = 24, ["LEFT_LEG"] = 25, ["LEFT_HAND"] = 26,
        };

        /// <summary>The in-game model of the LODs a character carries (0 is the cutscene one).</summary>
        const int GameLod = 1;

        /// <summary>The minifig's TT10_Cape and TT36_ComplexCape layers.</summary>
        const int CapeLayer = 10, ComplexCapeLayer = 36, HeadLayer = 5;

        /// <summary>The face LOD shown: the detailed ones rest with the mouth open (the game closes it by animating), the simplest smiles.</summary>
        const int FaceLod = 3;

        public static CharacterLook Read(string jsonPath) => FromJson(File.ReadAllText(jsonPath));

        public static CharacterLook FromJson(string json) =>
            JsonSerializer.Deserialize<CharacterLook>(json) ?? throw new InvalidDataException("Not a character look.");

        /// <summary>The scenes that make up the character: the body first, then its parts.</summary>
        public static List<EditorScene> Assemble(CharacterLook look, List<string> problems)
        {
            var scenes = new List<EditorScene>();
            problems.AddRange(look.Notes);
            if (look.Body == null || !File.Exists(look.Body))
            {
                problems.Add($"{look.Character}'s body model wasn't found.");
                return scenes;
            }
            var body = GSceneConverter.FromGScene(look.Body, out var bodyProblems);
            problems.AddRange(bodyProblems);
            body.Name = look.Character;
            // The layers the .CD shows by default, plus the ones its parts switch on (a face shows the head).
            body.ShownLayers = look.Layers.Count > 0 ? look.Layers.Concat(look.Parts.Select(p => p.Layer).OfType<int>()).ToHashSet() : null;
            body.FixedLod = GameLod;
            // The x-ray skeleton (shown when electrocuted) and the head's depth-only copy aren't part of the look.
            body.HiddenObject = name => name.EndsWith("_Skeleton_Mesh", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_Dummy_Mesh", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ZHead", StringComparison.OrdinalIgnoreCase);
            var byId = look.Materials.ToDictionary(m => m.Id);
            foreach (var material in body.Materials)
                if (MaterialIds.TryGetValue(Key(material.Name), out int id) && byId.TryGetValue(id, out var m))
                    Paint(material, m, problems);
            scenes.Add(body);

            var poiIxs = (body.OriginalScene as GScene_4F)?.CharacterData.FirstOrDefault()?.PoiIxs;
            // The game shows the cloth-simulated complex cape or the simple one, not both: the simple one holds its shape in bind pose.
            bool hasSimpleCape = look.Parts.Any(p => p.Layer == CapeLayer);
            foreach (var part in look.Parts)
            {
                if (part.Layer == ComplexCapeLayer && hasSimpleCape) continue;
                // A cape's rest pose is spread up like a collar; the game lets it hang through its animation, which isn't played here.
                if (part.Layer is CapeLayer or ComplexCapeLayer)
                {
                    problems.Add($"{part.Name} (a cape) isn't shown: it only hangs right when animated.");
                    continue;
                }
                if (part.Model == null || !File.Exists(part.Model))
                {
                    problems.Add($"{part.Name} wasn't found.");
                    continue;
                }
                EditorScene scene;
                try
                {
                    scene = GSceneConverter.FromGScene(part.Model, out var partProblems);
                    problems.AddRange(partProblems);
                }
                catch (Exception ex)
                {
                    problems.Add($"Couldn't read {part.Name} ({Path.GetFileName(part.Model)}): {ex.Message}");
                    continue;
                }
                scene.Name = part.Name;
                scene.OnlyObject = part.Object;
                scene.FixedLod = part.Layer == HeadLayer ? FaceLod : GameLod;
                scene.DepthBias = part.Layer == HeadLayer;
                // On its locator, whose number picks one of the body's points of interest. A skinned part (face, hair,
                // cape) hangs from its root bone, so the root's own bind pose is taken off first.
                if (poiIxs != null && part.Locator >= 0 && part.Locator < poiIxs.Count && poiIxs[part.Locator] < body.PoIs.Count
                    && body.PoIs[poiIxs[part.Locator]] is EditorPointOfInterest poi)
                {
                    var root = scene.AllJoints.OfType<EditorJoint>().FirstOrDefault()?.WorldTransform ?? Matrix4.Identity;
                    scene.SceneTransform = root.Inverted() * poi.Transform * (poi.Parent?.WorldTransform ?? Matrix4.Identity) * body.SceneTransform;
                }
                else
                    problems.Add($"{part.Name}'s locator {part.Locator} isn't one of the body's.");
                var tint = Colour(part.Tint) ?? Vector4.One;
                // A part named straight by its scene (no .CD of its own, like Joker's hair) takes the attachment's tint.
                if (part.Materials.Count == 0 && tint is not { X: > 0.9f, Y: > 0.9f, Z: > 0.9f })
                    foreach (var material in scene.Materials.Where(x => !x.Name.Contains("TRANSPARENT", StringComparison.OrdinalIgnoreCase)))
                        material.Colour1 = material.Colour1 * tint;
                foreach (var m in part.Materials)
                {
                    // A near-white tint changes nothing in the game; written here it would hide a part's vertex colours.
                    if (Colour(m.Tint) is not { } own || (own * tint) is { X: > 0.9f, Y: > 0.9f, Z: > 0.9f }) continue;
                    // Material 0 of a rigid part (a hat in its scene) colours the whole part, apart from see-through bits.
                    var targets = m.Id == 0 && part.Object != null
                        ? scene.Materials.Where(x => !x.Name.Contains("TRANSPARENT", StringComparison.OrdinalIgnoreCase))
                        : scene.Materials.Where((x, i) => i == m.Id);
                    // The tint multiplies the part's own colour (a black cowl stays black under a near-white tint).
                    foreach (var material in targets) material.Colour1 = material.Colour1 * own * tint;
                }
                scenes.Add(scene);
            }
            return scenes;
        }

        /// <summary>DX11_BODY_FRONT_GAME:VARIANT_AUTO → BODY_FRONT.</summary>
        static string Key(string name)
        {
            string key = name.Split(':')[0];
            if (key.StartsWith("DX11_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NXG_", StringComparison.OrdinalIgnoreCase)) key = key[(key.IndexOf('_') + 1)..];
            if (key.EndsWith("_GAME", StringComparison.OrdinalIgnoreCase)) key = key[..^5];
            return key;
        }

        /// <summary>
        /// A body material in the character's colours: its colour texture painted over its tint (the decals of an
        /// _ALPHA_DIFF texture show the tint through their transparent parts), or just the tint.
        /// </summary>
        static void Paint(EditorMaterial material, LookMaterial look, List<string> problems)
        {
            var tint = Colour(look.Tint);
            string? texture = look.Textures.FirstOrDefault(t => !Path.GetFileNameWithoutExtension(t).Contains("_BRDF", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileNameWithoutExtension(t).Contains("_NRM", StringComparison.OrdinalIgnoreCase));
            if (texture == null)
            {
                if (tint is { } plain)
                {
                    material.Diffuse0 = RenderTexture.GetWhiteTexture();
                    material.Colour1 = plain;
                }
                return;
            }
            DecodedImage? image;
            try { image = DdsDecoder.Decode(File.ReadAllBytes(texture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { image = null; }
            if (image == null)
            {
                problems.Add($"Couldn't read {Path.GetFileName(texture)}.");
                return;
            }
            byte[] rgba = image.Rgba;
            if (tint is { } under)
            {
                rgba = (byte[])rgba.Clone();
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    float a = rgba[i + 3] / 255f;
                    rgba[i] = (byte)(rgba[i] * a + under.X * 255 * (1 - a));
                    rgba[i + 1] = (byte)(rgba[i + 1] * a + under.Y * 255 * (1 - a));
                    rgba[i + 2] = (byte)(rgba[i + 2] * a + under.Z * 255 * (1 - a));
                    rgba[i + 3] = 255;
                }
            }
            material.Diffuse0 = RenderTexture.FromRgba(rgba, image.Width, image.Height, Path.GetFileName(texture));
            material.Colour1 = Vector4.One;
        }

        /// <summary>#RRGGBB as a colour, or null.</summary>
        static Vector4? Colour(string? hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7 || hex[0] != '#') return null;
            try
            {
                int v = Convert.ToInt32(hex[1..7], 16);
                return new Vector4(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1);
            }
            catch (FormatException) { return null; }
        }
    }
}

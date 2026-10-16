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
    public record LookMaterial(int Id, string? Tint, List<string> Textures, List<int>? Slots = null, List<string?>? Tints = null,
        string? Remap = null, string? RemapMaterial = null);

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
            // SUPER_MINIFIG_TPAGE_COMBINED (civilians, goons): one texture page for the whole body, the .CD's material 1
            ["TPAGE"] = 1, ["GLASSES_LEFT"] = 9, ["GLASSES_RIGHT"] = 9,
        };

        /// <summary>The in-game model of the LODs a character carries (0 is the cutscene one).</summary>
        const int GameLod = 1;

        /// <summary>The minifig's TT10_Cape and TT36_ComplexCape layers.</summary>
        const int CapeLayer = 10, ComplexCapeLayer = 36, HeadLayer = 5;

        /// <summary>The .CD's material numbers for the head front and back.</summary>
        const int HeadFront = 1, HeadBack = 2;

        /// <summary>The .CD's material number for what's attached to the torso (additional models, wings).</summary>
        const int AttachmentMaterial = 13;

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
            // The layers the .CD shows (its Default Layers: Batman's leave out the head and hat his face model covers).
            var shown = look.Layers.ToHashSet();
            body.ShownLayers = shown.Count > 0 ? shown : null;
            var byId = look.Materials.ToDictionary(m => m.Id);
            // A head front without a tint of its own is the head's colour (the head back's).
            if (byId.TryGetValue(HeadFront, out var front) && front.Tint == null && byId.TryGetValue(HeadBack, out var back) && back.Tint != null)
                byId[HeadFront] = front with { Tint = back.Tint };
            body.FixedLod = GameLod;
            // The x-ray skeleton (shown when electrocuted) and the head's depth-only copy aren't part of the look.
            body.HiddenObject = name => name.EndsWith("_Skeleton_Mesh", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_Dummy_Mesh", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ZHead", StringComparison.OrdinalIgnoreCase);
            foreach (var material in body.Materials)
                if (MaterialIds.TryGetValue(Key(material.Name), out int id) && byId.TryGetValue(id, out var m))
                {
                    // A remapped material takes the remap's textures only when the .CD gives it no colour texture of its own
                    // (Black Adam's chest); otherwise the remap is just its shader (Cyborg's glow, Gentleman Ghost's haze).
                    if (m.Remap != null && !m.Textures.Any(t => !Name(t).Contains("BRDF") && !Name(t).Contains("_NRM"))) Remap(material, m, problems);
                    // A body material the .CD gives textures but no colour is white under them (Jinx's robe).
                    else Paint(material, m.Tint == null && m.Textures.Count > 0 ? m with { Tint = "#FFFFFF" } : m, problems);
                }
            scenes.Add(body);

            var poiIxs = (body.OriginalScene as GScene_4F)?.CharacterData.FirstOrDefault()?.PoiIxs;
            foreach (var part in look.Parts)
            {
                // A part shows when its layer does (Arsenal's bow only comes out to shoot); the face always does.
                if (shown.Count > 0 && part.Layer is int layer && layer != HeadLayer && !shown.Contains(layer)) continue;
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
                // A face with features (eyes, mouth) shows its simplest smile; a textured face surface (Bane's mask) its game LOD.
                scene.FixedLod = part.Layer == HeadLayer && !part.Materials.Any(m => m.Textures.Count > 0) ? FaceLod : GameLod;
                // Every part is pulled slightly forwards, so a face beats the head it lies on while a hat, pulled as much,
                // still covers the face by its real depth.
                scene.DepthBias = true;
                // On its locator, whose number picks one of the body's points of interest. A skinned part (face, hair,
                // cape) hangs from its root bone, so the root's own bind pose is taken off first.
                // An additional model (no locator) is skinned to the body's own skeleton and laid out on its layers
                // (Black Manta's helmet and backpack): it stands where the body does, showing what the body shows.
                if (part.Locator < 0)
                {
                    scene.SceneTransform = body.SceneTransform;
                    scene.ShownLayers = body.ShownLayers;
                }
                else if (poiIxs != null && part.Locator < poiIxs.Count && poiIxs[part.Locator] < body.PoIs.Count
                    && body.PoIs[poiIxs[part.Locator]] is EditorPointOfInterest poi)
                {
                    var root = scene.AllJoints.OfType<EditorJoint>().FirstOrDefault()?.WorldTransform ?? Matrix4.Identity;
                    scene.SceneTransform = root.Inverted() * poi.Transform * (poi.Parent?.WorldTransform ?? Matrix4.Identity) * body.SceneTransform;
                }
                else
                    problems.Add($"{part.Name}'s locator {part.Locator} isn't one of the body's.");
                var tint = Colour(part.Tint) ?? Vector4.One;
                // A part named straight by its scene (no .CD of its own, like Joker's hair) takes the attachment's tint.
                // (That tint colours the whole part, tintable flag or not: Arsenal's cap is black by it.)
                if (!part.Materials.Any(m => m.Tint != null || m.Textures.Any(t => !Name(t).Contains("BRDF"))) && tint is not { X: > 0.9f, Y: > 0.9f, Z: > 0.9f })
                    foreach (var material in scene.Materials.Where(x => !SeeThrough(x)))
                        Tint(material, tint);
                // A face's tintable bits (Batman's stubble) are the skin under the head's mask: its layer 2 tint.
                if (part.Layer == HeadLayer && !part.Materials.Any(m => m.Tint != null || m.Textures.Count > 0) && SkinOf(byId) is { } skin)
                    foreach (var material in scene.Materials.Where(Tintable))
                        Tint(material, skin);
                // An additional model is coloured by the .CD's material 13, the attachment one (Creeper's red collar, quivers).
                var partMaterials = part.Locator < 0 && byId.TryGetValue(AttachmentMaterial, out var attachment)
                    ? new List<LookMaterial> { attachment } : part.Materials;
                foreach (var m in partMaterials)
                {
                    // A face's own texture (Bane's mask, on material 1: the head front) goes on its face model, over the head's colour.
                    if (m.Textures.Count > 0 && part.Layer == HeadLayer)
                    {
                        var headTint = m.Tint ?? (byId.TryGetValue(HeadFront, out var head) ? head.Tint : null);
                        foreach (var material in scene.Materials) Paint(material, m with { Tint = headTint }, problems);
                        continue;
                    }
                    // A rigid part's textured material (Riddler's bowler: green with its "?") is painted like a body part.
                    // Its number is the scene's material index (Johnny Quick's helmet is 0, its see-through visor 1).
                    if (m.Textures.Count > 0 && part.Object != null)
                    {
                        var own = m.Id >= 0 && m.Id < scene.Materials.Count ? Key(scene.Materials[m.Id].Name) : null;
                        foreach (var material in scene.Materials.Where(x => own != null
                            ? Key(x.Name).Equals(own, StringComparison.OrdinalIgnoreCase)
                            : !SeeThrough(x)))
                        {
                            Paint(material, m, problems);
                            // Its other layer tints still colour the parts its vertex colours pick (Johnny Quick's yellow wings).
                            for (int k = 1; m.Tints != null && k < m.Tints.Count && k < 4; k++)
                                if (Colour(m.Tints[k]) is { } layerTint && !IsWhite(layerTint * tint))
                                    SetLayerColour(material, k, layerTint * tint);
                        }
                        continue;
                    }
                    // A part's layer tints go in its materials' colour slots, which the shader blends by the vertex colours:
                    // Harley's pigtails are one material, layer 1 (blue) on one side and layer 2 (red) on the other; the
                    // Flash's hat is red with yellow ears; Catwoman's cowl is black with its goggle frame white (layer 2).
                    // Only the tintable materials when the part has some (Catwoman's goggle frame isn't), else all (the Flash's hat).
                    var tints = m.Tints ?? new List<string?> { m.Tint };
                    var opaque = scene.Materials.Where(x => !SeeThrough(x)).ToList();
                    var targets = opaque.Any(Tintable) ? opaque.Where(Tintable) : opaque;
                    foreach (var material in targets)
                        for (int k = 0; k < tints.Count && k < 4; k++)
                            if (Colour(tints[k]) is { } layerTint && !IsWhite(layerTint * tint))
                                SetLayerColour(material, k, layerTint * tint);
                }
                scenes.Add(scene);
            }
            return scenes;
        }

        /// <summary>
        /// Whether the game tints a material: its tintable flag (a hat's goggle frame or a face's eyes have it off), and
        /// not see-through (lenses keep their own colour).
        /// </summary>
        static bool Tintable(EditorMaterial material) => material.Original.materialFlags_tintable != 0
            && !SeeThrough(material);

        /// <summary>
        /// A lens or visor, which keeps its own colour: HAT_JOHNNY_TRANSPARENT_MAT, or Gentleman Ghost's goggles ("Glass",
        /// the top hat's only tintable materials; the hat itself isn't flagged but is coloured).
        /// </summary>
        static bool SeeThrough(EditorMaterial material) => material.Name.Contains("TRANSPARENT", StringComparison.OrdinalIgnoreCase)
            || material.Name.Split(':')[0].Equals("Glass", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The skin under a head's mask: the head front's layer 2 tint, when it's set (not plain white). Null otherwise,
        /// so a face isn't darkened by a black head (Deathstroke's orange mask half).
        /// </summary>
        static Vector4? SkinOf(Dictionary<int, LookMaterial> byId)
        {
            if (!byId.TryGetValue(HeadFront, out var head)) return null;
            var second = head.Tints is { Count: > 1 } t ? Colour(t[1]) : null;
            return second is { } skin && !IsWhite(skin) ? skin : null;
        }

        /// <summary>
        /// Tints a part's material: multiplied into whatever it draws, at the end of the shader (many part materials take
        /// their colour from vertex colours alone, so their own colour and texture can't carry it: Scarecrow's hat).
        /// </summary>
        static void Tint(EditorMaterial material, Vector4 tint) => material.PreviewTint *= tint;

        /// <summary>A material's colour slot <paramref name="layer"/> (0-3: Colour1-4, the shader's diffuse layer colours) times a tint.</summary>
        static void SetLayerColour(EditorMaterial material, int layer, Vector4 tint)
        {
            switch (layer)
            {
                case 0: material.Colour1 *= tint; break;
                case 1: material.Colour2 *= tint; break;
                case 2: material.Colour3 *= tint; break;
                case 3: material.Colour4 *= tint; break;
            }
        }

        /// <summary>A tint that changes nothing in the game (near white); written here it would hide a part's vertex colours.</summary>
        static bool IsWhite(Vector4 colour) => colour is { X: > 0.9f, Y: > 0.9f, Z: > 0.9f };

        /// <summary>DX11_BODY_FRONT_GAME:VARIANT_AUTO → BODY_FRONT.</summary>
        static string Key(string name)
        {
            string key = name.Split(':')[0];
            if (key.StartsWith("DX11_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NXG_", StringComparison.OrdinalIgnoreCase)) key = key[(key.IndexOf('_') + 1)..];
            // A remap scene's copy is BODY_FRONT_GAME_DX11.
            if (key.EndsWith("_DX11", StringComparison.OrdinalIgnoreCase)) key = key[..^5];
            if (key.EndsWith("_GAME", StringComparison.OrdinalIgnoreCase)) key = key[..^5];
            return key;
        }

        /// <summary>
        /// A material in the character's colours, built like the game's layered minifig shader: layer 1's tint, then each
        /// colour texture by slot painted over what's below through its alpha. A colour texture (_DIFF, _ALPHA_DIFF)
        /// paints its own colours; a mask (_MAIN, _SKIN) is coloured by its slot's layer tint (Batman's head: the black
        /// mask, then the skin of his chin). Shine (_BRDF, slot 4) and bump textures aren't drawn.
        /// </summary>
        static void Paint(EditorMaterial material, LookMaterial look, List<string> problems, List<DecodedImage>? over = null)
        {
            var baseTint = Colour(look.Tint);
            var layers = look.Textures
                .Select((path, i) => (Path: path, Slot: look.Slots != null && i < look.Slots.Count ? look.Slots[i] : 0))
                .Where(t => t.Slot < 4 && !Name(t.Path).Contains("BRDF") && !Name(t.Path).Contains("_NRM"))
                .OrderBy(t => t.Slot).ToList();
            if (layers.Count == 0 && (over == null || over.Count == 0))
            {
                if (baseTint is { } plain)
                {
                    material.Diffuse0 = RenderTexture.GetWhiteTexture();
                    material.Colour1 = plain;
                }
                return;
            }
            var images = new List<(DecodedImage Image, int Slot, bool Mask)>();
            foreach (var (path, slot) in layers)
            {
                DecodedImage? image;
                try { image = DdsDecoder.Decode(File.ReadAllBytes(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { image = null; }
                if (image == null) problems.Add($"Couldn't read {System.IO.Path.GetFileName(path)}.");
                else images.Add((image, slot, Mask(path)));
            }
            if (over != null) images.AddRange(over.Select((image, k) => (image, 1 + k, false)));
            if (images.Count == 0) return;
            int width = images.Max(i => i.Image.Width), height = images.Max(i => i.Image.Height);
            var rgba = new byte[width * height * 4];
            var under = baseTint ?? Vector4.One;
            // A slot 0 texture is the base layer itself, coloured by layer 1's tint, with nothing under it (Captain Cold's
            // goggles: white in the texture, cyan by the tint, see-through around them).
            bool hasBase = images.Any(i => i.Slot == 0);
            for (int p = 0; p < width * height; p++)
            {
                rgba[p * 4] = (byte)(under.X * 255); rgba[p * 4 + 1] = (byte)(under.Y * 255); rgba[p * 4 + 2] = (byte)(under.Z * 255);
                rgba[p * 4 + 3] = (byte)(baseTint == null || hasBase ? 0 : 255);
            }
            foreach (var (image, slot, mask) in images)
            {
                var tint = slot == 0 ? under
                    : mask && look.Tints != null && slot < look.Tints.Count ? Colour(look.Tints[slot]) ?? Vector4.One : Vector4.One;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int from = ((y * image.Height / height) * image.Width + x * image.Width / width) * 4, to = (y * width + x) * 4;
                        float a = image.Rgba[from + 3] / 255f;
                        rgba[to] = (byte)(rgba[to] * (1 - a) + image.Rgba[from] * tint.X * a);
                        rgba[to + 1] = (byte)(rgba[to + 1] * (1 - a) + image.Rgba[from + 1] * tint.Y * a);
                        rgba[to + 2] = (byte)(rgba[to + 2] * (1 - a) + image.Rgba[from + 2] * tint.Z * a);
                        rgba[to + 3] = Math.Max(rgba[to + 3], image.Rgba[from + 3]);
                    }
            }
            material.Diffuse0 = RenderTexture.FromRgba(rgba, width, height, layers.Count > 0 ? System.IO.Path.GetFileName(layers[0].Path) : look.RemapMaterial ?? "remap");
            material.Colour1 = Vector4.One;
        }

        /// <summary>
        /// Draws a body material with another scene's (Source Material Type 1: Black Adam's chest is MATERIAL_REMAP_BLACKADAM's
        /// BODY_FRONT_GAME, his lightning bolt in its own textures).
        /// </summary>
        static void Remap(EditorMaterial material, LookMaterial look, List<string> problems)
        {
            EditorScene source;
            try { source = GSceneConverter.FromGScene(look.Remap!, out _); }
            catch (Exception ex)
            {
                problems.Add($"Couldn't read {System.IO.Path.GetFileName(look.Remap)}: {ex.Message}");
                return;
            }
            string wanted = Key(look.RemapMaterial ?? material.Name);
            var match = source.Materials.FirstOrDefault(x => Key(x.Name).Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                problems.Add($"{System.IO.Path.GetFileName(look.Remap)} has no {look.RemapMaterial}.");
                return;
            }
            // Its textures go over the .CD's colour for the material (Black Adam's bolt and belt on his black chest, on its
            // second layer; the first is a 4x4 white stand-in).
            var images = new[] { match.Diffuse0, match.Diffuse1, match.Diffuse2 }
                .Select(t => t?.Original is { ImageHeader: { } header, Data: { } data } ? DdsDecoder.Decode([.. header, .. data]) : null)
                .OfType<DecodedImage>().Where(i => i.Width > 4 || i.Height > 4).ToList();
            if (images.Count == 0)
            {
                material.OverridingReference = match;
                return;
            }
            Paint(material, look with { Textures = new(), Slots = new() }, problems, images);
        }

        static string Name(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant();

        /// <summary>A mask the layer tint colours (_MAIN, _SKIN…), rather than a texture with colours of its own.</summary>
        static bool Mask(string path) => Name(path).Replace("_DX11", "") is var n
            && (n.EndsWith("_MAIN") || n.EndsWith("_SKIN") || n.EndsWith("_TINT") || n.EndsWith("_MASK"));

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

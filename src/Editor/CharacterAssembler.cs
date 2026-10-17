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
    public record LookPart(string Name, string? Model, string? Object, int Locator, int? Layer, string? Tint, List<LookMaterial> Materials,
        List<int>? PartLayers = null, Dictionary<int, int>? Specials = null, List<float>? Offset = null);

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

        /// <summary>The .CD's glasses material, and how many depth-bias units the glasses are pulled over the face.</summary>
        const int Glasses = 9, GlassesPull = 5;

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
                    if (m.Remap != null && !m.Textures.Any(t => !Name(t).Contains("BRDF") && !Name(t).Contains("_NRM") && !Name(t).Contains("_GLOW"))) Remap(material, m, problems);
                    // A body material the .CD gives textures but no colour is white under them (Jinx's robe).
                    else Paint(material, m.Tint == null && m.Textures.Count > 0 ? m with { Tint = "#FFFFFF" } : m, problems);
                }
            // The glasses lie just off the head, closer than the face's own pull towards the camera: pulled as well, so the
            // face doesn't cover them (Kite Man's goggles).
            foreach (var material in body.Materials.Where(x => MaterialIds.TryGetValue(Key(x.Name), out int id) && id == Glasses))
                material.DepthBiasBase = -GlassesPull;
            scenes.Add(body);

            var poiIxs = (body.OriginalScene as GScene_4F)?.CharacterData.FirstOrDefault()?.PoiIxs;
            foreach (var part in look.Parts)
            {
                // A part shows when its layer does (Arsenal's bow only comes out to shoot); the face always does.
                if (shown.Count > 0 && part.Layer is int layer && layer != HeadLayer && !shown.Contains(layer)) continue;
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
                // A cape shows the layers and the one cape shape its own .CD picks (CAPE_DX11 holds a dozen), hung down the back.
                if (part.Layer is CapeLayer or ComplexCapeLayer)
                {
                    if (part.PartLayers is { Count: > 0 } partLayers) scene.ShownLayers = partLayers.ToHashSet();
                    if (part.Specials != null) scene.HiddenObject = NotPicked(scene, part.Specials);
                    CapePose.Hang(scene, problems);
                }
                // A face with features (eyes, mouth) shows its simplest smile; a textured face surface (Bane's mask) its game LOD.
                scene.FixedLod = part.Layer == HeadLayer && !part.Materials.Any(m => m.Textures.Count > 0) ? FaceLod : GameLod;
                // Every part is pulled slightly forwards, so a face beats the head it lies on while a hat, pulled as much,
                // still covers the face by its real depth.
                scene.DepthBias = true;
                // Its layers' own depth biases count from its lowest, so a face's mask stays on the head, under Kite Man's
                // goggles, and only its eyes and mouth rise over the mask.
                // (A minifig's only: a bigfig's face lies deeper in its head and needs the whole pull, Steppenwolf's.)
                if (part.Layer == HeadLayer && scene.Materials.Count > 0)
                {
                    float lowest = scene.Materials.Min(x => x.Original.KDepthBias * x.Original.KDepthBiasScale);
                    foreach (var material in scene.Materials) material.DepthBiasBase = lowest;
                }
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
                    // The attachment's Object Offset places it on the locator (a bigfig's face, scaled onto its big head).
                    var offset = part.Offset is { Count: 16 } o
                        ? new Matrix4(o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], o[8], o[9], o[10], o[11], o[12], o[13], o[14], o[15])
                        : Matrix4.Identity;
                    scene.SceneTransform = root.Inverted() * offset * poi.Transform * (poi.Parent?.WorldTransform ?? Matrix4.Identity) * body.SceneTransform;
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
                    if (m.Textures.Count > 0 && IsFace(part))
                    {
                        // (A minifig's: a bigfig's head has its own colours, and its .CD's material 1 isn't its head; the
                        // face texture goes on clear, over the head: BANE_BIGFIG's mask on his black head, not green.)
                        var headTint = m.Tint ?? (part.Layer == HeadLayer && byId.TryGetValue(HeadFront, out var head) ? head.Tint : null);
                        foreach (var material in scene.Materials) Paint(material, m with { Tint = headTint }, problems);
                        continue;
                    }
                    // A rigid part's textured material (Riddler's bowler: green with its "?") is painted like a body part, and
                    // so is a cape's (Black Adam's gold one, CAPE_BLACKADAM; its _ALPHA cut-out isn't drawn). Its number is the
                    // scene's material index when it has that many (Johnny Quick's helmet is 0, its see-through visor 1).
                    if (m.Textures.Count > 0 && (part.Object != null || part.Layer is CapeLayer or ComplexCapeLayer))
                    {
                        var own = m.Id >= 0 && m.Id < scene.Materials.Count ? Key(scene.Materials[m.Id].Name) : null;
                        foreach (var material in scene.Materials.Where(x => own != null
                            ? Key(x.Name).Equals(own, StringComparison.OrdinalIgnoreCase)
                            : !SeeThrough(x)))
                        {
                            if (part.Layer is CapeLayer or ComplexCapeLayer) FillLayers(material, m, problems);
                            else Paint(material, m, problems);
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
                    // A face with none flagged takes no tint: its colours are its own (Simon Baz's skin is brown in its
                    // vertex colours; his .CD's dark brown on top made it black).
                    var targets = opaque.Any(Tintable) ? opaque.Where(Tintable)
                        : IsFace(part) ? Enumerable.Empty<EditorMaterial>() : opaque;
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
        /// The objects a part's layer specials leave out: each mask has a bit per object of its layer, in the file's order
        /// (CAPE_BAT's -4095 sets bit 0 of layer 0: BatCape_Mesh, not the other eleven capes).
        /// </summary>
        static Func<string, bool> NotPicked(EditorScene scene, Dictionary<int, int> specials)
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scene.OriginalScene is GScene_4F gscene)
                foreach (var lod in gscene.CharacterData)
                    foreach (var layer in lod.LayerMetadata.GroupBy(m => (int)m.Layer))
                    {
                        if (!specials.TryGetValue(layer.Key, out int mask)) continue;
                        int k = 0;
                        foreach (var meta in layer)
                        {
                            if (meta.SpecialIndex >= 0 && meta.SpecialIndex < gscene.DisplayScene.SpecialObjects.Count && (mask >> k & 1) == 0)
                                hidden.Add(gscene.DisplayScene.SpecialObjects[meta.SpecialIndex].Name);
                            k++;
                        }
                    }
            return hidden.Contains;
        }

        /// <summary>
        /// Whether the game tints a material: its tintable flag (a hat's goggle frame or a face's eyes have it off), and
        /// not see-through (lenses keep their own colour).
        /// </summary>
        static bool Tintable(EditorMaterial material) => material.Original.materialFlags_tintable != 0
            && !SeeThrough(material);

        /// <summary>A face: on a minifig's head layer, or a bigfig's, which goes on its layer 0 (FACE_BANE on BANE_BIGFIG).</summary>
        static bool IsFace(LookPart part) => part.Layer == HeadLayer || part.Name.StartsWith("FACE", StringComparison.OrdinalIgnoreCase);

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
        /// colour texture by slot painted over what's below through its alpha, times its slot's layer tint as the game's
        /// shader does: white in most .CDs, so a _DIFF keeps its own colours, while a white mask takes the tint (Batman's
        /// chin skin; Green Arrow's LEG_CUSTOM_BOOTS, green by his legs' layer 2). Shine (_BRDF, slot 4), bump and glow
        /// textures aren't drawn.
        /// </summary>
        static void Paint(EditorMaterial material, LookMaterial look, List<string> problems, List<DecodedImage>? over = null)
        {
            var baseTint = Colour(look.Tint);
            var layers = look.Textures
                .Select((path, i) => (Path: path, Slot: look.Slots != null && i < look.Slots.Count ? look.Slots[i] : 0))
                .Where(t => t.Slot < 4 && !Name(t.Path).Contains("BRDF") && !Name(t.Path).Contains("_NRM") && !Name(t.Path).Contains("_GLOW") && !Name(t.Path).Replace("_DX11", "").EndsWith("_ALPHA"))
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
                    : look.Tints != null && slot < look.Tints.Count ? Colour(look.Tints[slot]) ?? Vector4.One : Vector4.One;
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
            // Only layers laid over normally (blend 1): Black Adam's third is a glow mask, black but for the bolt, added as
            // light, and painting it hid his chest's lines and belt.
            var original = match.Original;
            var images = new[] { match.Diffuse0, original.layerBlendDiffuse == 1 ? match.Diffuse1 : null, original.layerBlendDiffuse1 == 1 ? match.Diffuse2 : null }
                .Select(t => t?.Original is { ImageHeader: { } header, Data: { } data } ? DdsDecoder.Decode([.. header, .. data]) : null)
                .OfType<DecodedImage>().Where(i => i.Width > 4 || i.Height > 4).ToList();
            if (images.Count == 0)
            {
                material.OverridingReference = match;
                return;
            }
            Paint(material, look with { Textures = new(), Slots = new() }, problems, images);
        }

        /// <summary>
        /// Puts each of a .CD material's textures in the scene material's diffuse layer of the same slot, tinted by that
        /// layer's tint, for the shader to blend by vertex colour as the game does: a cape's Cape_MAT has three layers,
        /// and CAPE_BLACKADAM (slot 0) and CAPE_BLACKADAM_ALPHA (slot 1) fill the first two.
        /// </summary>
        static void FillLayers(EditorMaterial material, LookMaterial look, List<string> problems)
        {
            for (int i = 0; i < look.Textures.Count; i++)
            {
                string path = look.Textures[i];
                int slot = look.Slots != null && i < look.Slots.Count ? look.Slots[i] : 0;
                if (slot > 2 || Name(path).Contains("BRDF") || Name(path).Contains("_NRM") || Name(path).Contains("_GLOW")) continue;
                DecodedImage? image;
                try { image = DdsDecoder.Decode(File.ReadAllBytes(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { image = null; }
                if (image == null)
                {
                    problems.Add($"Couldn't read {System.IO.Path.GetFileName(path)}.");
                    continue;
                }
                var texture = RenderTexture.FromRgba(image.Rgba, image.Width, image.Height, System.IO.Path.GetFileName(path));
                switch (slot)
                {
                    case 0: material.Diffuse0 = texture; break;
                    case 1: material.Diffuse1 = texture; break;
                    case 2: material.Diffuse2 = texture; break;
                }
            }
            for (int k = 0; look.Tints != null && k < look.Tints.Count && k < 3; k++)
                if (Colour(look.Tints[k]) is { } layerTint)
                    SetLayerColour(material, k, layerTint);
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

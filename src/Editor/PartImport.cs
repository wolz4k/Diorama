using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Core.Filetypes.GSC.Components.RESH;
using Diorama.Core.Filetypes.SHADERS;
using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Core;
using Diorama.Core.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>
    /// Brings a character part from another LEGO game in with its textures, as a new part of this game: the other
    /// part's shape and bone weights go into one of this game's parts of the same kind (<see cref="PartTransplant"/>),
    /// its colour textures (and any bits of its game's LEGO texture page it uses) are packed into one texture, and its
    /// meshes take a material of this game's that shows a colour texture. Writes the model, its textures, shaders and
    /// resource files under a new name. Tried in game: LEGO Marvel's Avengers' Falcon wings, textured, on Hawkman.
    /// </summary>
    public static class PartImport
    {
        /// <summary>The material tried first: diffuse and normal map, skinned (the hot dog guy's, in DC Super-Villains; tried in game).</summary>
        const string DonorScene = @"CHARS\SUPER_CHARACTER\ADDITIONALMODEL\ADDITIONALMODEL_HOTDOGGUY_DX11.GHG";
        const string DonorMaterial = "MAT_HOTDOGGUY";

        /// <summary>
        /// A material of this game's that the part can take: one showing a colour texture (and a normal map, if
        /// <see cref="Normal"/> is set) and binding no other texture, for vertices laid out like the base part's.
        /// </summary>
        record Donor(string Path, string Material, string? Variant, byte[] Diffuse, byte[]? Normal);

        static readonly Dictionary<(string Root, string Layout, uint Version, string Folder), Donor?> donors = new();

        const int Cell = 512;      // each colour texture gets a cell this size in the packed texture
        const int Align = 32;      // texture page bits are copied in 32-pixel squares, so their blocks line up down to mip 3

        enum Kind { Plain, Texture, Page }

        /// <summary>A bit of the other game's LEGO texture page (pixels, 32-aligned) and where it goes in the packed texture.</summary>
        record Bit(int X, int Y, int W, int H) { public int AX, AY; }

        /// <summary>
        /// Makes <paramref name="outPath"/> (an X_DX11.GHG) from <paramref name="sourcePath"/> (the other game's part) and
        /// <paramref name="basePath"/> (this game's part of the same kind), with X_DX11.NXG_TEXTURES, X_DX11.PC_SHADERS,
        /// X_DX11.GHG.RES and X.SHADERS beside it. Returns notes for the modder.
        /// </summary>
        public static List<string> Build(string sourcePath, string basePath, string outPath)
        {
            var source = Load(sourcePath);
            var target = Load(basePath);
            string gameRoot = GameRootOf(basePath) ?? throw new InvalidDataException("The part to build on should be in this game's CHARS folder, so its material can be found.");
            // the materials the part's own meshes are drawn with (not its breakup pieces), and their automatic variants;
            // the rest (blend-shape and rigid variants, breakup materials) stay as they are, with their textures
            var used = Enumerable.Range(0, target.CharacterData.Count).SelectMany(lod => PartTransplant.MeshesOf(target, lod))
                .Where(m => !m.Breakup).Select(m => m.Material).Where(i => i >= 0 && i < target.MaterialBlock.Materials.Length && target.MaterialBlock.Materials[i] != null).ToHashSet();
            if (used.Count == 0) throw new InvalidDataException($"{Path.GetFileName(basePath)} draws no meshes of its own to build on.");
            var main = target.MaterialBlock.Materials[used.Min()];
            foreach (int i in used.ToList())
                if ((target.MaterialBlock.Materials[i] as NuMaterialData_E0)?.ChildMaterial is { } child && child.MaterialName.EndsWith(":VARIANT_AUTO"))
                    used.Add(Array.IndexOf(target.MaterialBlock.Materials, child));
            used.Remove(-1);
            string layout = Layout(main.VertexLayout);
            var donor = FindDonor(gameRoot, layout, main.Version, Path.GetDirectoryName(Path.GetFullPath(basePath))!)
                ?? throw new InvalidDataException(layout.Contains("uvSet")
                    ? $"None of this game's character parts has a textured material for vertices laid out like this part's ({layout}); build on another part of the same kind, or bring it in without textures."
                    : $"{Path.GetFileName(basePath)} has no texture coordinates, so it can't show a texture; build on another part of the same kind, or bring it in without textures.");
            string donorPath = donor.Path;

            var sourceTex = NxgTextures.Read(Path.ChangeExtension(sourcePath, ".NXG_TEXTURES"))?.TextureSet?.Textures
                ?? throw new FileNotFoundException("The other game's part has no .NXG_TEXTURES beside it.");
            var notes = new List<string>();

            // what each of the other part's materials shows: one of its own textures, part of the LEGO page, or plain colour
            var kinds = new Dictionary<int, (Kind Kind, int Texture)>();
            var cells = new List<int>();
            for (int i = 0; i < source.MaterialBlock.Materials.Length; i++)
            {
                var m = source.MaterialBlock.Materials[i];
                int d = m?.Diffuse0Index ?? -1;
                if (d < 0 || d >= sourceTex.Length || sourceTex[d].Data == null) { kinds[i] = (Kind.Plain, -1); continue; }
                var t = sourceTex[d];
                if (string.IsNullOrEmpty(t.Header.Name)) { kinds[i] = (Kind.Page, d); continue; }
                kinds[i] = (Kind.Texture, d);
                if (!cells.Contains(d)) cells.Add(d);
            }
            if (cells.Count == 0) throw new InvalidDataException("The other game's part has no colour textures of its own; bring it in without textures (Bring In Part from Another Game, then Save As).");

            var first = sourceTex[cells[0]];
            foreach (int c in cells)
            {
                var t = sourceTex[c];
                if (t.FourCC != first.FourCC || t.IsCubemap) throw new InvalidDataException($"Its textures are stored in different formats ({t.Header.Name}); they need to be the same to be packed together.");
                if (t.Width > Cell || t.Height > Cell || t.Width != t.Height || (t.Width & (t.Width - 1)) != 0)
                    throw new InvalidDataException($"{t.Header.Name} is {t.Width}x{t.Height}; textures up to {Cell}x{Cell} (square) can be packed so far.");
            }
            int blockBytes = first.FourCC == 0x31545844 ? 8 : first.FourCC == 0x35545844 || first.FourCC == 0x33545844 ? 16 : 0;
            if (blockBytes == 0) throw new InvalidDataException("Its textures aren't DXT1/DXT3/DXT5, which is what can be packed so far.");
            int perSide = cells.Count + 1 <= 4 ? 2 : cells.Count + 1 <= 16 ? 4 : throw new InvalidDataException($"It has {cells.Count} textures; up to 15 can be packed.");
            int size = perSide * Cell;
            int spare = perSide * perSide - 1; // the last cell: white, plus the LEGO page bits
            (int X, int Y) CellAt(int index) => (index % perSide * Cell, index / perSide * Cell);
            var (spareX, spareY) = CellAt(spare);
            Vector2 white = new((spareX + Cell - 6) / (float)size, (spareY + Cell - 6) / (float)size);

            // the LEGO page bits, packed into the spare cell (its last 32 pixels stay white)
            NuTexture? page = null;
            var bits = new List<Bit>();
            var pageMeshes = Meshes(source).Where(m => kinds.TryGetValue(m.Material, out var k) && k.Kind == Kind.Page).ToList();
            if (pageMeshes.Count > 0)
            {
                page = sourceTex[kinds[pageMeshes[0].Material].Texture];
                if (page.FourCC != first.FourCC || page.Width % Align != 0)
                {
                    notes.Add("Its LEGO texture page is stored differently from its textures, so the parts that use it are plain colour.");
                    foreach (var k in kinds.Where(k => k.Value.Kind == Kind.Page).ToList()) kinds[k.Key] = (Kind.Plain, -1);
                    page = null;
                }
                else
                {
                    bits = PageBits(pageMeshes.Select(m => m.Mesh), page.Width);
                    if (!PackBits(bits, spareX, spareY))
                    {
                        notes.Add("The bits of the LEGO texture page it uses don't fit in the packed texture, so those parts are plain colour.");
                        foreach (var k in kinds.Where(k => k.Value.Kind == Kind.Page).ToList()) kinds[k.Key] = (Kind.Plain, -1);
                        page = null; bits.Clear();
                    }
                }
            }

            // UVs into the packed texture
            int clamped = 0;
            foreach (var (mesh, material) in Meshes(source))
            {
                var vertices = OBJConverter.ReadVertices(mesh);
                var (kind, tex) = kinds.TryGetValue(material, out var k) ? k : (Kind.Plain, -1);
                foreach (var v in vertices)
                {
                    float u = v.UVSet01.X, w = v.UVSet01.Y;
                    Vector2 uv;
                    if (kind == Kind.Texture)
                    {
                        if (u < -0.01f || u > 1.01f || w < -0.01f || w > 1.01f) clamped++;
                        var (cx, cy) = CellAt(cells.IndexOf(tex));
                        var t = sourceTex[tex];
                        uv = new((cx + Math.Clamp(u, 0, 1) * t.Width) / size, (cy + Math.Clamp(w, 0, 1) * t.Height) / size);
                    }
                    else if (kind == Kind.Page && page != null)
                    {
                        float x = u * page.Width, y = w * page.Height;
                        var bit = bits.FirstOrDefault(b => x >= b.X && x <= b.X + b.W && y >= b.Y && y <= b.Y + b.H);
                        uv = bit == null ? white : new((x - bit.X + bit.AX) / size, (y - bit.Y + bit.AY) / size);
                    }
                    else uv = white;
                    v.UVSet01 = new Vector4(uv.X, uv.Y, v.UVSet01.Z, v.UVSet01.W);
                }
                OBJConverter.SetMeshData(mesh, vertices, OBJConverter.ReadIndices(mesh), source.MeshSceneBlock.Meshes);
            }
            if (clamped > 0) notes.Add($"{clamped:N0} vertices had UVs outside their texture (repeating); they're held at its edge, so check those spots.");

            // the shape and weights
            var moved = PartTransplant.Transplant(source, target, out _);
            notes.AddRange(moved.Where(n => n.StartsWith("LOD") || n.StartsWith("Warning")));

            // the material, on the slots the part is drawn with (its variant on the variant slots)
            var block = target.MaterialBlock;
            var children = block.Materials.Select(m => (m as NuMaterialData_E0)?.ChildMaterial?.MaterialName).ToList();
            var replaced = new List<NuMaterialData_E0>();
            for (int i = 0; i < block.Materials.Length; i++)
            {
                var old = block.Materials[i];
                if (old == null || !used.Contains(i)) continue;
                // the donor's material or its variant: whichever is laid out like the slot (a variant may or may not be skinned)
                var donorMaterials = ((GScene_4F)GScene.Parse(donorPath)).MaterialBlock.Materials.OfType<NuMaterialData_E0>().ToList();
                var pair = new[] { donor.Material, donor.Variant }.Where(n => n != null).Select(n => donorMaterials.First(m => m.MaterialName == n)).ToList();
                var copy = pair.FirstOrDefault(m => Layout(m.VertexLayout) == Layout(old.VertexLayout))
                    ?? (old.MaterialName.Contains(":VARIANT") ? pair.Last() : pair[0]);
                copy.Parent = block;
                copy.MaterialName = old.MaterialName;
                block.Materials[i] = copy;
                replaced.Add(copy);
            }
            for (int i = 0; i < block.Materials.Length; i++)
                if (block.Materials[i] is NuMaterialData_E0 m)
                    m.ChildMaterial = children[i] == null ? null : block.Materials.FirstOrDefault(x => x?.MaterialName == children[i]);
            bool keepOwn = block.Materials.Any(m => m != null && !replaced.Contains(m));

            // the textures: the packed one and a flat normal map, in the base part's texture file
            string outStem = Stem(outPath), baseStem = Stem(basePath);
            string prefix = target.Metadata.MetaStrings.Select(s => s.Value).FirstOrDefault(v => v.Contains("super_character_texture/")) is { } known
                ? known[..(known.IndexOf("super_character_texture/") + "super_character_texture/".Length)]
                : "project_diana/project_diana_characters_images_nut/super_character_texture/";
            string diffName = prefix + "misc/" + outStem.ToLowerInvariant() + "_diff.nut", nrmName = prefix + "misc/" + outStem.ToLowerInvariant() + "_nrm.nut";

            var nxg = NxgTextures.Read(Path.ChangeExtension(basePath, ".NXG_TEXTURES")) ?? throw new FileNotFoundException("The part to build on has no .NXG_TEXTURES beside it.");
            var headers = nxg.TextureSet.Textures.Select(t => t.Header).ToList();
            var diffuse = new NuTexture { Header = Header(headers.ElementAtOrDefault(0), diffName) };
            var normal = new NuTexture { Header = Header(headers.ElementAtOrDefault(1) ?? headers.ElementAtOrDefault(0), nrmName) };
            PackedTexture(diffuse, sourceTex, cells, first, page, bits, perSide, blockBytes);
            FlatNormal(normal, first);
            var made = donor.Normal != null ? new[] { diffuse, normal } : new[] { diffuse };
            var own = keepOwn ? nxg.TextureSet.Textures : Array.Empty<NuTexture>();
            byte[] textureBytes = nxg.ToBytes(own.Concat(made));

            // texture names in the model, and in the material's own texture bindings (the game goes by those: checksum and name)
            var names = target.Metadata.MetaStrings;
            if (names.Count != own.Length)
            {
                names.Clear();
                foreach (var t in own) names.Add(new NuDynamicString(string.IsNullOrEmpty(t.Header.Name) ? t.Header.Path : t.Header.Name));
            }
            foreach (var t in made) names.Add(new NuDynamicString(t.Header.Name));
            foreach (var m in replaced)
            {
                m.Diffuse0Index = own.Length; m.Normal0Index = donor.Normal != null ? own.Length + 1 : -1;
                foreach (var list in new[] { m.PixelFixupData, m.VertexFixupData, m.TempVertexFixupData })
                    foreach (var h in list ?? new())
                    {
                        if (h.Checksum.All(x => x == 0)) continue;
                        var to = h.Checksum.SequenceEqual(donor.Diffuse) ? diffuse.Header : donor.Normal != null && h.Checksum.SequenceEqual(donor.Normal) ? normal.Header : null;
                        if (to == null) continue;
                        h.Name = to.Name;
                        h.Checksum = (byte[])to.Checksum.Clone();
                    }
            }

            // shaders: the base part's and the material's
            NxgShaders shaders;
            using (var f = ReadOnlyFile.Open(Path.ChangeExtension(basePath, ".PC_SHADERS"))) shaders = NxgShaders.Read(f);
            var have = shaders.ShaderCache.Select(x => x.ConfigHash).ToHashSet();
            using (var f = ReadOnlyFile.Open(Path.ChangeExtension(donorPath, ".PC_SHADERS")))
                foreach (var x in NxgShaders.Read(f).ShaderCache)
                    if (have.Add(x.ConfigHash)) shaders.ShaderCache.Add(x);

            // the files it names: its own, under the new name and folder
            string? baseDir = RelativeDir(basePath), outDir = RelativeDir(outPath);
            string Rename(string p)
            {
                string dir = Path.GetDirectoryName(p) ?? "", file = Path.GetFileName(p);
                if (file.StartsWith(baseStem, StringComparison.OrdinalIgnoreCase)) file = outStem.ToLowerInvariant() + file[baseStem.Length..];
                if (baseDir != null && outDir != null && dir.Equals(baseDir, StringComparison.OrdinalIgnoreCase)) dir = outDir;
                return dir.Length == 0 ? file : dir + "\\" + file;
            }
            target.ResourceHeader.RenameFiles(Rename);

            // everything is built in memory before anything is written
            var model = new MemoryStream();
            using (var output = new RawFile(model)) target.Write(output, new GSerializationContext());
            try
            {
                var check = new RawFile(new MemoryStream(model.ToArray(), false));
                check.SetFileLocation(new FilesystemFileLocation(outPath));
                GScene.Parse(check);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"The model came out unreadable ({ex.Message}), so nothing was written. {Path.GetFileName(basePath)} with {Stem(donor.Path)}'s material {donor.Material} doesn't work together; build on another part of the same kind.");
            }
            var shaderBytes = new MemoryStream();
            using (var output = new RawFile(shaderBytes)) shaders.Handle(new SchemaSerializer(output, true), 0);
            byte[]? res = RenamedResourceFile(Path.ChangeExtension(basePath, ".GHG.RES"), Rename);
            string baseShaderList = Path.Combine(Path.GetDirectoryName(basePath) ?? "", baseStem + ".SHADERS");
            byte[]? shaderList = RenamedResourceFile(baseShaderList, Rename);

            string folder = Path.GetDirectoryName(outPath) ?? "";
            string outBase = Path.GetFileNameWithoutExtension(outPath); // X_DX11
            File.WriteAllBytes(outPath, model.ToArray());
            File.WriteAllBytes(Path.Combine(folder, outBase + ".NXG_TEXTURES"), textureBytes);
            File.WriteAllBytes(Path.Combine(folder, outBase + ".PC_SHADERS"), shaderBytes.ToArray());
            if (res != null) File.WriteAllBytes(Path.Combine(folder, outBase + ".GHG.RES"), res);
            if (shaderList != null) File.WriteAllBytes(Path.Combine(folder, outStem + ".SHADERS"), shaderList);

            notes.Insert(0, $"Made {Path.GetFileName(outPath)} from {Path.GetFileName(sourcePath)}: its shape, bone weights and textures ({cells.Count} packed into one {size}x{size}{(bits.Count > 0 ? $", with {bits.Count} bit(s) of its LEGO texture page" : "")}), on {Path.GetFileName(basePath)}'s skeleton with {Stem(donor.Path)}'s material {donor.Material}.");
            notes.Add($"Written beside it: {outBase}.NXG_TEXTURES, {outBase}.PC_SHADERS{(res != null ? $", {outBase}.GHG.RES" : "")}{(shaderList != null ? $", {outStem}.SHADERS" : "")}.");
            int mask = PartTransplant.LayerMask(target);
            notes.Add($"For the game to use it, the part's .CD (Flux) loads {outStem} (name the .CD {outStem} too), with Default Layers, and the Cutscene, Hat, Hair, Cape and Christmas Hat Layers, set to {mask}. A character's attachment then names it as its Resource File, with Tint Colour white.");
            notes.Add("Parts with no texture of their own keep their vertex colours (times the attachment's Tint Colour); metallic or bumpy finishes from the other game don't come along.");
            return notes;
        }

        /// <summary>
        /// A textured material for vertices laid out as <paramref name="layout"/>: the hot dog guy's if it fits, otherwise
        /// a character part's (preferring one with a normal map) whose texture bindings are only its colour texture and
        /// normal map, both stored in its own texture file. Parts in <paramref name="folder"/> (the base part's: capes for
        /// a cape) are looked at first and faces last, as their materials do face things. Remembered per game, layout and folder.
        /// </summary>
        static Donor? FindDonor(string gameRoot, string layout, uint version, string folder)
        {
            lock (donors)
                if (donors.TryGetValue((gameRoot, layout, version, folder), out var known)) return known;
            Donor? found = null, withoutNormal = null;
            string first = Path.Combine(gameRoot, DonorScene);
            static bool IsFace(string f) => f.Contains(@"\FACE", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\SUPER_FACE", StringComparison.OrdinalIgnoreCase);
            var files = new[] { first }.Concat(Directory.EnumerateFiles(Path.Combine(gameRoot, "CHARS", "SUPER_CHARACTER"), "*_DX11.GHG", SearchOption.AllDirectories)
                .OrderByDescending(f => string.Equals(Path.GetDirectoryName(f), folder, StringComparison.OrdinalIgnoreCase)).ThenBy(IsFace).ThenBy(f => f));
            foreach (var file in files)
            {
                if (!File.Exists(file) || !File.Exists(Path.ChangeExtension(file, ".NXG_TEXTURES")) || !File.Exists(Path.ChangeExtension(file, ".PC_SHADERS"))) continue;
                GScene_4F? scene;
                try { scene = GScene.Parse(file) as GScene_4F; } catch { continue; }
                if (scene?.MaterialBlock?.Materials == null) continue;
                NuTexture[]? textures = null;
                foreach (var m in scene.MaterialBlock.Materials.OfType<NuMaterialData_E0>())
                {
                    // a material is read by its block's version, so one stored in another version can't move into this block
                    if (m.MaterialName.Contains(":VARIANT") || m.Diffuse0Index < 0 || m.Version != version || Layout(m.VertexLayout) != layout) continue;
                    try { textures ??= NxgTextures.Read(Path.ChangeExtension(file, ".NXG_TEXTURES"))?.TextureSet?.Textures; } catch { textures = null; }
                    if (textures == null) break;
                    var diffuse = m.Diffuse0Index < textures.Length ? textures[m.Diffuse0Index] : null;
                    var normal = m.Normal0Index >= 0 && m.Normal0Index < textures.Length ? textures[m.Normal0Index] : null;
                    // both in its own texture file (not the shared LEGO page), so the bindings can be pointed at ours
                    if (diffuse == null || string.IsNullOrEmpty(diffuse.Header.Name) || (m.Normal0Index >= 0 && (normal == null || string.IsNullOrEmpty(normal.Header.Name)))) continue;
                    var child = m.ChildMaterial as NuMaterialData_E0;
                    var bound = new[] { m, child }.Where(x => x != null).SelectMany(x => new[] { x!.PixelFixupData, x.VertexFixupData, x.TempVertexFixupData })
                        .SelectMany(l => l ?? new()).Where(h => h.Checksum.Any(b => b != 0)).ToList();
                    if (!bound.Any(h => h.Checksum.SequenceEqual(diffuse.Header.Checksum))) continue;
                    if (!bound.All(h => h.Checksum.SequenceEqual(diffuse.Header.Checksum) || (normal != null && h.Checksum.SequenceEqual(normal.Header.Checksum)))) continue;
                    var donor = new Donor(file, m.MaterialName, child?.MaterialName, diffuse.Header.Checksum, normal?.Header.Checksum);
                    if (normal != null) { found = donor; break; }
                    withoutNormal ??= donor;
                }
                if (found != null) break;
            }
            lock (donors) return donors[(gameRoot, layout, version, folder)] = found ?? withoutNormal;
        }

        static GScene_4F Load(string path)
        {
            if (GScene.Parse(path) is not GScene_4F scene) throw new InvalidDataException($"{Path.GetFileName(path)} isn't a scene Diorama can read.");
            if (scene.CharacterData.Count == 0) throw new InvalidDataException($"{Path.GetFileName(path)} isn't a character part (no joints or LODs).");
            return scene;
        }

        /// <summary>The folder holding CHARS above <paramref name="path"/>, or null.</summary>
        static string? GameRootOf(string path)
        {
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); dir != null; dir = Path.GetDirectoryName(dir))
                if (Path.GetFileName(dir).Equals("CHARS", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(dir);
            return null;
        }

        /// <summary>The folder of <paramref name="path"/> from CHARS down, as resource headers write it (chars\super_character\x), or null.</summary>
        static string? RelativeDir(string path)
        {
            string? root = GameRootOf(path);
            return root == null ? null : Path.GetRelativePath(root, Path.GetDirectoryName(Path.GetFullPath(path))!).Replace('/', '\\').ToLowerInvariant();
        }

        /// <summary>WINGS_FEATHER from ...\WINGS_FEATHER_DX11.GHG.</summary>
        static string Stem(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            return stem.EndsWith("_DX11", StringComparison.OrdinalIgnoreCase) ? stem[..^5] : stem;
        }

        static string Layout(VertexList? l) => l == null ? "" : string.Join(",", l.Definitions.Select(d => $"{d.Variable}:{d.Type}"));

        /// <summary>Every mesh any LOD draws (breakup parts too), once, with the material it's drawn with.</summary>
        static List<(NuRenderMesh Mesh, int Material)> Meshes(GScene_4F scene)
        {
            var seen = new HashSet<NuRenderMesh>();
            var list = new List<(NuRenderMesh, int)>();
            for (int lod = 0; lod < scene.CharacterData.Count; lod++)
                foreach (var (mesh, material, _) in PartTransplant.MeshesOf(scene, lod))
                    if (seen.Add(mesh)) list.Add((mesh, material));
            return list;
        }

        /// <summary>The squares of a texture page that these meshes' triangles use: overlapping ones merged, 32-aligned.</summary>
        static List<Bit> PageBits(IEnumerable<NuRenderMesh> meshes, int pageSize)
        {
            var boxes = new List<(int X0, int Y0, int X1, int Y1)>();
            foreach (var mesh in meshes)
            {
                var v = OBJConverter.ReadVertices(mesh);
                var ix = OBJConverter.ReadIndices(mesh);
                for (int i = 0; i + 2 < ix.Length; i += 3)
                {
                    var tri = new[] { v[ix[i]], v[ix[i + 1]], v[ix[i + 2]] };
                    float x0 = tri.Min(p => p.UVSet01.X) * pageSize, x1 = tri.Max(p => p.UVSet01.X) * pageSize;
                    float y0 = tri.Min(p => p.UVSet01.Y) * pageSize, y1 = tri.Max(p => p.UVSet01.Y) * pageSize;
                    int ax0 = Math.Max(0, (int)Math.Floor(x0 / Align) * Align), ay0 = Math.Max(0, (int)Math.Floor(y0 / Align) * Align);
                    int ax1 = Math.Min(pageSize, (int)Math.Ceiling(x1 / Align) * Align), ay1 = Math.Min(pageSize, (int)Math.Ceiling(y1 / Align) * Align);
                    if (ax1 == ax0) ax1 = Math.Min(pageSize, ax0 + Align);
                    if (ay1 == ay0) ay1 = Math.Min(pageSize, ay0 + Align);
                    boxes.Add((ax0, ay0, ax1, ay1));
                }
            }
            // merge boxes that touch until none do
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int a = 0; a < boxes.Count && !merged; a++)
                    for (int b = a + 1; b < boxes.Count && !merged; b++)
                    {
                        var p = boxes[a]; var q = boxes[b];
                        if (p.X0 <= q.X1 && q.X0 <= p.X1 && p.Y0 <= q.Y1 && q.Y0 <= p.Y1)
                        {
                            boxes[a] = (Math.Min(p.X0, q.X0), Math.Min(p.Y0, q.Y0), Math.Max(p.X1, q.X1), Math.Max(p.Y1, q.Y1));
                            boxes.RemoveAt(b);
                            merged = true;
                        }
                    }
            }
            return boxes.Select(b => new Bit(b.X0, b.Y0, b.X1 - b.X0, b.Y1 - b.Y0)).ToList();
        }

        /// <summary>Places the bits in the spare cell in rows, tallest first, keeping its last 32x32 white. False if they don't fit.</summary>
        static bool PackBits(List<Bit> bits, int cellX, int cellY)
        {
            int x = 0, y = 0, row = 0;
            foreach (var bit in bits.OrderByDescending(b => b.H))
            {
                if (bit.W > Cell) return false;
                if (x + bit.W > Cell) { x = 0; y += row; row = 0; }
                if (y + bit.H > Cell || (y + bit.H > Cell - Align && x + bit.W > Cell - Align)) return false;
                bit.AX = cellX + x; bit.AY = cellY + y;
                x += bit.W; row = Math.Max(row, bit.H);
            }
            return true;
        }

        /// <summary>A texture header like <paramref name="like"/> (this game's), under a new name and checksum.</summary>
        static NuTexGenHdr Header(NuTexGenHdr? like, string name)
        {
            var h = new NuTexGenHdr();
            if (like != null) { h.Path = like.Path; h.ResourceId = like.ResourceId; h.NutType = like.NutType; h.Flags = like.Flags; h.ObjectId = like.ObjectId; h.FixupType = like.FixupType; }
            h.Path = "";
            h.Name = name;
            h.RandomiseChecksum();
            return h;
        }

        static int Offset(int size, int level, int blockBytes)
        {
            int offset = 0;
            for (int k = 0; k < level; k++) { int b = Math.Max(1, (size >> k) / 4); offset += b * b * blockBytes; }
            return offset;
        }

        static byte[] WhiteBlock(int blockBytes) => blockBytes == 8
            ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 }
            : new byte[] { 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };

        /// <summary>
        /// The packed texture, block by block: each cell's texture copied as it is (all of them compressed the same way),
        /// the page bits copied into the spare cell, white elsewhere, at every mip level down to 1x1.
        /// </summary>
        static void PackedTexture(NuTexture into, NuTexture[] textures, List<int> cells, NuTexture first, NuTexture? page, List<Bit> bits, int perSide, int blockBytes)
        {
            int size = perSide * Cell, levels = (int)Math.Log2(size) + 1;
            var white = WhiteBlock(blockBytes);
            var tail = first.Data.AsSpan(first.Data.Length - blockBytes, blockBytes).ToArray(); // the first texture's 1x1, for the smallest levels
            var data = new List<byte>();
            for (int l = 0; l < levels; l++)
            {
                int blocks = Math.Max(1, (size >> l) / 4), cellBlocks = (Cell >> l) / 4;
                for (int by = 0; by < blocks; by++)
                    for (int bx = 0; bx < blocks; bx++)
                    {
                        if (cellBlocks == 0) { data.AddRange(tail); continue; }
                        int cell = by / cellBlocks * perSide + bx / cellBlocks, lx = bx % cellBlocks, ly = by % cellBlocks;
                        if (cell < cells.Count)
                        {
                            var t = textures[cells[cell]];
                            int tb = (t.Width >> l) / 4;
                            if (tb == 0 || lx >= tb || ly >= tb) { data.AddRange(white); continue; }
                            data.AddRange(t.Data.AsSpan(Offset(t.Width, l, blockBytes) + (ly * tb + lx) * blockBytes, blockBytes).ToArray());
                            continue;
                        }
                        // the spare cell: a page bit if this block is in one (while the bits stay block-aligned)
                        int px = bx * 4 << l, py = by * 4 << l;
                        var bit = page == null || l > 3 ? null : bits.FirstOrDefault(b => px >= b.AX && px < b.AX + b.W && py >= b.AY && py < b.AY + b.H);
                        if (bit == null) { data.AddRange(white); continue; }
                        int pb = (page!.Width >> l) / 4;
                        int sx = (bit.X + px - bit.AX >> l) / 4, sy = (bit.Y + py - bit.AY >> l) / 4;
                        data.AddRange(page.Data.AsSpan(Offset(page.Width, l, blockBytes) + (sy * pb + sx) * blockBytes, blockBytes).ToArray());
                    }
            }
            into.ImageHeader = DdsHeader(first.ImageHeader, size, size, levels, Math.Max(1, size / 4) * Math.Max(1, size / 4) * blockBytes, null);
            into.Data = data.ToArray();
            into.Width = into.Height = size;
            into.MipCount = levels;
            into.FourCC = first.FourCC;
            into.Header.Level = (uint)levels;
        }

        /// <summary>An 8x8 flat normal map (DXT1, as this game stores them; the other game's may be DXT5, which reads as noise here).</summary>
        static void FlatNormal(NuTexture into, NuTexture like)
        {
            var data = new List<byte>();
            for (int k = 0; k < 7; k++) data.AddRange(new byte[] { 0x1F, 0x84, 0x1F, 0x84, 0, 0, 0, 0 }); // (128, 128, 255): 8x8, 4x4, 2x2, 1x1
            into.ImageHeader = DdsHeader(like.ImageHeader, 8, 8, 4, 32, 0x31545844);
            into.Data = data.ToArray();
            into.Width = into.Height = 8;
            into.MipCount = 4;
            into.FourCC = 0x31545844;
            into.Header.Level = 4;
        }

        /// <summary>A copy of a DDS header with a new size, mip count and (optionally) compression.</summary>
        static byte[] DdsHeader(byte[] like, int width, int height, int mips, int linearSize, uint? fourCC)
        {
            var h = like.Take(128).ToArray(); // no DX10 extension: the formats here are DXT
            BitConverter.GetBytes(height).CopyTo(h, 12);
            BitConverter.GetBytes(width).CopyTo(h, 16);
            BitConverter.GetBytes(linearSize).CopyTo(h, 20);
            BitConverter.GetBytes(mips).CopyTo(h, 28);
            if (fourCC is { } cc) BitConverter.GetBytes(cc).CopyTo(h, 84);
            return h;
        }

        /// <summary>A standalone resource-header file (X_DX11.GHG.RES, X.SHADERS) with its files renamed, or null if it isn't there.</summary>
        public static byte[]? RenamedResourceFile(string path, Func<string, string> rename)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            var header = new NuResourceHeader();
            long end;
            using (var input = new RawFile(new MemoryStream(bytes, false)))
            {
                header.Handle(new SchemaSerializer(input, false), 0);
                end = input.Position;
            }
            header.RenameFiles(rename);
            var buffer = new MemoryStream();
            using (var output = new RawFile(buffer)) header.Handle(new SchemaSerializer(output, true), 0);
            return buffer.ToArray().Concat(bytes.Skip((int)end)).ToArray();
        }
    }
}

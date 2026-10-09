using System;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>
    /// Finds the same part in a character's other LODs, so one replacement can go to all of them. Each LOD is its own copy
    /// of the model with its own meshes, so a part is matched by material and by where it sits: the copy with the same
    /// material whose bounding box is closest. Over DC Super-Villains' 492 multi-LOD characters this finds 80% of
    /// part/LOD pairs; most of the rest have no counterpart (lower LODs merge or drop small parts, or draw them with another
    /// material).
    /// </summary>
    public static class LodParts
    {
        public static EditorGeometryObject? FindInLod(EditorScene scene, EditorGeometryObject part, int lod)
        {
            if (part.Mesh?.OriginalMesh == null) return null;
            var (centre, size) = Box(part);
            float scale = Math.Max(size.Length(), 0.001f);
            string material = Normalise(part.Material?.Name);

            var candidates = scene.AllGeometry()
                .Where(g => g.LodGroup == lod && g.IsBreakupPart == part.IsBreakupPart && g.Mesh?.OriginalMesh != null && g.Mesh != part.Mesh)
                .Distinct()
                .Select(g =>
                {
                    var (c, s) = Box(g);
                    return (geo: g, score: ((c - centre).Length() + (s - size).Length()) / scale, same: Normalise(g.Material?.Name) == material);
                })
                .OrderBy(x => x.score)
                .ToList();

            // Only the same material: the new mesh's UVs are laid out for this part's texture, so a lookalike with another
            // material (Darkseid's LOD 3 body is drawn with MAT_FACE) would come out wrongly textured
            return candidates.FirstOrDefault(x => x.same && x.score <= 0.25f).geo;
        }

        private static (Vector3 centre, Vector3 size) Box(EditorGeometryObject geo)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var v in OBJConverter.ReadVertices(geo.Mesh.OriginalMesh))
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            return ((min + max) / 2, max - min);
        }

        // MAT_GRUNDY_LEFT_CUTSCENE in LOD 0 is MAT_GRUNDY_LEFT:VARIANT_AUTO in the others
        private static string Normalise(string? name)
        {
            name ??= "";
            int variant = name.IndexOf(':');
            if (variant >= 0) name = name[..variant];
            if (name.EndsWith("_CUTSCENE", StringComparison.OrdinalIgnoreCase)) name = name[..^"_CUTSCENE".Length];
            return name.ToLowerInvariant();
        }
    }
}

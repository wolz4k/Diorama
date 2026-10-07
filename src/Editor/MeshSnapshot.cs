using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>
    /// The geometry of a scene before a mesh replacement, so it can be undone: every mesh's buffers, offsets, flags,
    /// indices, culling box and blend shapes (a replacement can also move other meshes' offsets in a shared buffer),
    /// and which render mesh each object drew. Replacing builds new objects rather than editing these, so keeping the
    /// references is enough; the small arrays that are edited in place are copied.
    /// </summary>
    public sealed class MeshSnapshot
    {
        private sealed record ShapeState(NuBlendShape Shape, uint Format, byte[] Buffer, List<uint> Table);

        private sealed record MeshState(NuRenderMesh Mesh, VertexList[] Buffers, int[] Offsets, uint[] Flags, ushort[] Indices,
            uint IndicesFlags, uint IndicesBase, uint IndicesCount, uint VerticesBase, uint VerticesCount, Vector4[] CentreExtents,
            List<ShapeState> Shapes);

        private readonly List<MeshState> meshes = new();
        private readonly List<(EditorGeometryObject Geo, RenderMesh Mesh)> drawn = new();

        /// <summary>What the replacement was, for the undo button and message ("Replace with face.obj").</summary>
        public string Label { get; }

        private MeshSnapshot(string label) => Label = label;

        public static MeshSnapshot Take(EditorScene scene, string label)
        {
            var snapshot = new MeshSnapshot(label);
            foreach (var mesh in scene.OriginalScene.MeshSceneBlock?.Meshes ?? [])
            {
                var shapes = new List<ShapeState>();
                for (var shape = mesh.Shape; shape != null; shape = shape.Next)
                    shapes.Add(new ShapeState(shape, shape.CompressionFormat, shape.Buffer, shape.RunBatchTableV2));
                snapshot.meshes.Add(new MeshState(mesh, (VertexList[])mesh.VertexBuffers.Clone(), (int[])mesh.VertexBufferOffsets.Clone(),
                    (uint[])mesh.VertexBufferFlags.Clone(), mesh.Indices, mesh.IndicesFlags, mesh.IndicesBase, mesh.IndicesCount,
                    mesh.VerticesBase, mesh.VerticesCount, (Vector4[])mesh.CentreExtents.Clone(), shapes));
            }
            foreach (var geo in scene.AllGeometry())
                snapshot.drawn.Add((geo, geo.Mesh));
            return snapshot;
        }

        /// <summary>Puts every mesh back as it was. Runs on the render thread, like the replacement.</summary>
        public void Restore()
        {
            foreach (var m in meshes)
            {
                var mesh = m.Mesh;
                mesh.VertexBuffers = m.Buffers;
                mesh.VertexBufferOffsets = m.Offsets;
                mesh.VertexBufferFlags = m.Flags;
                mesh.Indices = m.Indices;
                mesh.IndicesFlags = m.IndicesFlags;
                mesh.IndicesBase = m.IndicesBase;
                mesh.IndicesCount = m.IndicesCount;
                mesh.VerticesBase = m.VerticesBase;
                mesh.VerticesCount = m.VerticesCount;
                mesh.CentreExtents = m.CentreExtents;
                foreach (var s in m.Shapes)
                {
                    s.Shape.CompressionFormat = s.Format;
                    s.Shape.Buffer = s.Buffer;
                    s.Shape.RunBatchTableV2 = s.Table;
                }
            }
            foreach (var (geo, mesh) in drawn)
                geo.Mesh = mesh;
        }
    }
}

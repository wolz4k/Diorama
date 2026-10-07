using Avalonia.Controls.Shapes;
using BrickVault;
using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Diorama.Core.Filetypes.GSC.Components
{
    public class NuBlendShape : ISchemaSerializable
    {
        public NuBlendShape Next;

        public uint Id;

        public List<NuVec> Offsets;

        public byte HasAlphas;
        public List<NuBlendShapeAlpha> Alphas;

        public uint CompressionFormat;

        public List<NuBlendRunV1> RunV1;

        public byte[] Buffer;

        public List<uint> RunBatchTableV2;

        public void Handle(SchemaSerializer schema, uint parentVersion)
        {
            GSerializationContext? ctx = schema.Context as GSerializationContext;
            
            ctx?.AddReference(this);

            schema.HandleUInt(ref Id);

            schema.HandleOptional(ref Next, parentVersion);

            if (parentVersion < 0xae)
            {
                schema.HandleSchemaVarArray(ref Offsets);
            }
            else
            {
                schema.HandleSchemaVector(ref Offsets);
            }

            if (parentVersion < 0xae)
            {
                schema.HandleByte(ref HasAlphas);
                schema.HandleSchemaVarArray(ref Alphas);
            }

            schema.HandleUInt(ref CompressionFormat);
            if (parentVersion < 0xae)
            {
                schema.HandleSchemaVarArray(ref RunV1);
            }
            schema.HandleBuffer(ref Buffer);
            if (Buffer.Length != 0)
                ctx?.AddReference(Buffer);

            if (parentVersion < 0xae)
            {
                schema.HandleSchemaVarArray(ref RunBatchTableV2);
            }
            else
            {
                schema.HandleSerializableVector(ref RunBatchTableV2);
            }
        }

        /// <summary>Vertices per batch in a format 2 shape; each batch ends with a record of count 0.</summary>
        private const int OffsetBatch = 640;

        /// <summary>
        /// The per-vertex offsets of a format 2 shape (the one DC Super-Villains uses for all but 234 of 155,740 shapes):
        /// <see cref="Buffer"/> holds batches of <see cref="OffsetBatch"/> vertices, each a run-length list of
        /// (u32 count, big-endian x y z) records ending in a count-0 record, and <see cref="RunBatchTableV2"/> two
        /// little-endian numbers per batch: where it starts in the buffer and its length less 12. Null for other formats.
        /// </summary>
        public Vector3[]? DecodeOffsets(int vertexCount)
        {
            if (CompressionFormat != 2 || Buffer == null)
                return null;

            var offsets = new Vector3[vertexCount];
            int vertex = 0;
            for (int at = 0; at + 16 <= Buffer.Length; at += 16)
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(Buffer.AsSpan(at));
                var value = new Vector3(
                    BinaryPrimitives.ReadSingleBigEndian(Buffer.AsSpan(at + 4)),
                    BinaryPrimitives.ReadSingleBigEndian(Buffer.AsSpan(at + 8)),
                    BinaryPrimitives.ReadSingleBigEndian(Buffer.AsSpan(at + 12)));
                for (int i = 0; i < count && vertex < vertexCount; i++)
                    offsets[vertex++] = value;
            }
            return vertex == vertexCount ? offsets : null;
        }

        /// <summary>Stores <paramref name="offsets"/> (one per vertex) as a format 2 shape, the layout <see cref="DecodeOffsets"/> reads.</summary>
        public void EncodeOffsets(IReadOnlyList<Vector3> offsets)
        {
            var buffer = new List<byte>();
            var table = new List<uint>();
            var record = new byte[16];

            void Write(int count, Vector3 value)
            {
                BinaryPrimitives.WriteUInt32BigEndian(record, (uint)count);
                BinaryPrimitives.WriteSingleBigEndian(record.AsSpan(4), value.X);
                BinaryPrimitives.WriteSingleBigEndian(record.AsSpan(8), value.Y);
                BinaryPrimitives.WriteSingleBigEndian(record.AsSpan(12), value.Z);
                buffer.AddRange(record);
            }

            for (int start = 0; start < offsets.Count || start == 0; start += OffsetBatch)
            {
                int batchStart = buffer.Count;
                int end = Math.Min(start + OffsetBatch, offsets.Count);
                for (int i = start; i < end;)
                {
                    int run = 1;
                    while (i + run < end && Same(offsets[i + run], offsets[i])) run++;
                    Write(run, offsets[i]);
                    i += run;
                }
                Write(0, Vector3.Zero);
                // read back as big-endian uints, as the parser does
                table.Add(BinaryPrimitives.ReverseEndianness((uint)batchStart));
                table.Add(BinaryPrimitives.ReverseEndianness((uint)(buffer.Count - batchStart - 12)));
                if (offsets.Count == 0) break;
            }

            CompressionFormat = 2;
            Buffer = buffer.ToArray();
            RunBatchTableV2 = table;
        }

        private static bool Same(Vector3 a, Vector3 b) =>
            BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) &&
            BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) &&
            BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

        public static NuBlendShape Parse(RawFile file, GSerializationContext ctx, uint parentVersion)
        {
            var shape = new NuBlendShape();

            shape.Id = file.ReadUInt(true);

            ctx.AddReference(shape);

            uint nextShapeExists = file.ReadUInt(true);
            if (nextShapeExists != 0)
            {
                shape.Next = Parse(file, ctx, parentVersion);
            }

            if (parentVersion < 0xae)
            {
                shape.Offsets = NuSerializer.ReadLegacyVarArray<NuVec>(file);
            }
            else
            {
                shape.Offsets = NuSerializer.ReadVectorArray<NuVec>(file);
            }

            if (parentVersion < 0xae)
            {
                Debug.Assert(1 == 0, "NuBlendShape section not implemented");
            }

            shape.CompressionFormat = file.ReadUInt(true);
            int bufferSize = file.ReadInt(true);
            shape.Buffer = file.ReadArray(bufferSize);
            if (bufferSize != 0)
            {
                ctx.AddReference(shape.Buffer);
            }

            shape.RunBatchTableV2 = NuSerializer.ReadVectorArray<uint>(file);

            return shape;
        }

        public void Write(RawFile file, GSerializationContext ctx, uint parentVersion)
        {
            file.WriteUInt(Id, true);

            ctx.AddReference(this);

            if (Next != null)
            {
                file.WriteUInt(1, true);
                Next.Write(file, ctx, parentVersion);
            }
            else
            {
                file.WriteUInt(0, true);
            }

            if (parentVersion < 0xae)
            {
                NuSerializer.WriteLegacyVarArray<NuVec>(file, Offsets);
            }
            else
            {
                NuSerializer.WriteVectorArray<NuVec>(file, Offsets);
            }

            if (parentVersion < 0xae)
            {

            }

            file.WriteUInt(CompressionFormat, true);

            if (Buffer != null)
            {
                file.WriteInt(Buffer.Length, true);
                file.WriteArray(Buffer);
                if (Buffer.Length != 0) // as when reading: an empty buffer takes no reference number
                    ctx.AddReference(Buffer);
            }
            else
            {
                file.WriteInt(0);
            }

            NuSerializer.WriteVectorArray<uint>(file, RunBatchTableV2);
        }
    }
}

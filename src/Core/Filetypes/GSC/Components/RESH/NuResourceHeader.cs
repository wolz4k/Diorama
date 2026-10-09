using OpenTK.Graphics.ES11;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Diorama.Core.Filetypes.GSC.Components.RESH
{
    public class NuResourceHeader : ISchemaSerializable
    {
        private uint Version;

        public NuFileTree FileTree;

        public List<NuResourceReference> References;

        private string ResourcePath;
        private NuCheckSum ResourceChecksum;

        private uint ResourceType;
        private string Stream;
        private long Transaction;
        private string Username;
        private string ProjectName;
        private byte Project;
        private string FileName;
        private byte Discipline;

        /// <summary>
        /// Gives the files this header lists new paths (<paramref name="rename"/> gets and returns them as the tree has
        /// them: lower case, backslashes), rebuilding the tree and pointing each reference at its file's new place.
        /// </summary>
        public void RenameFiles(Func<string, string> rename)
        {
            var files = FileTree.FilesInOrder();
            var newPaths = files.Select(f => rename(f.Path)).ToList();
            var tree = NuFileTree.FromPaths(newPaths, FileTree.Version);
            var bySegment = files.Select((f, i) => (f.Segment, New: newPaths[i])).ToDictionary(x => x.Segment, x => x.New);
            foreach (var reference in References)
                if (bySegment.TryGetValue((int)reference.Hash, out var path))
                    reference.Hash = (uint)tree.PathIndexes[path];
            FileTree = tree;
        }

        /// <summary>The paths of the files this header lists.</summary>
        public List<string> Files() => FileTree.FilesInOrder().Select(f => f.Path).ToList();

        public void Handle(SchemaSerializer schema, uint parentVersion)
        {
            using (schema.HandleRegion())
            {
                schema.Expect(".CC4HSERHSER");
                schema.HandleUInt(ref Version);
                if (Version > 8) // TODO: Check this
                {
                    schema.HandlePascalString(ref ResourcePath, 1);
                    schema.Handle(ref ResourceChecksum);
                }
                schema.HandleOptional(ref FileTree);
                schema.HandleSchemaVector(ref References, Version);
                if (Version > 1)
                {
                    schema.HandleUInt(ref ResourceType);
                    schema.HandlePascalString(ref Stream);
                    if (Version < 4)
                    {
                        Debug.Assert(1 == 0, "unimplemented resource header region");
                    }
                    else
                    {
                        schema.HandleLong(ref Transaction);
                    }
                    schema.HandlePascalString(ref Username);
                    if (Version < 4)
                    {
                        schema.HandlePascalString(ref ProjectName);
                    }
                    else
                    {
                        schema.HandleByte(ref Project);
                    }
                    schema.HandlePascalString(ref FileName);
                    if (Version < 4)
                    {

                    }
                    else
                    {
                        schema.HandleByte(ref Discipline);
                    }
                }
            }
        }
    }
}

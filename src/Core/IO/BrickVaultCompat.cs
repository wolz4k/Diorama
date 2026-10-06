// The public BrickVault (github.com/connorh315/BrickVault) predates the extraction
// API Diorama was written against. These extensions map that API onto the public
// one. Delete this file once the public BrickVault has GetExtractionContext.
namespace BrickVault.Types
{
    public sealed class DatExtractionContext : IDisposable
    {
        public global::BrickVault.RawFile Archive { get; }

        public DatExtractionContext(string datPath)
        {
            Archive = new global::BrickVault.RawFile(datPath);
        }

        public void Dispose() => Archive.Dispose();
    }

    public static class BrickVaultCompat
    {
        public static DatExtractionContext GetExtractionContext(this DATFile dat)
            => new DatExtractionContext(dat.FileLocation);

        public static void ExtractFile(this DATFile dat, ArchiveFile file, DatExtractionContext ctx, Stream write)
            => dat.ExtractFile(file, ctx.Archive, write);

        public static void ExtractRapid(this DATFile dat, ArchiveFile file, Stream write, global::BrickVault.RawFile archive)
            => dat.ExtractFile(file, archive, write);

        public static IEnumerable<ArchiveFile> GetFilesWithExtension(this DATFile dat, string extension)
            => dat.Files.Where(f => f.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}

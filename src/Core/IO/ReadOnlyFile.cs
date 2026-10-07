using System.IO;

namespace Diorama.Core.IO
{
    /// <summary>
    /// Opens a file only for reading. RawFile(path) opens with FileMode.OpenOrCreate and read/write access, which makes an
    /// empty file where one is missing (inside a game install, too) and stops anything else reading it meanwhile.
    /// </summary>
    public static class ReadOnlyFile
    {
        public static RawFile Open(string path)
        {
            var file = new RawFile(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            file.SetFileLocation(new FilesystemFileLocation(path));
            return file;
        }
    }
}

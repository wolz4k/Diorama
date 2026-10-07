using BrickVault.Types;
using OpenTK.Windowing.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace Diorama.Core.IO
{
    public static class FileProvider
    {
        private static IFileProvider provider;
        public static FileProviderState State { get; private set; }

        /// <summary>Whether a game folder (archives or an extracted install) is set in Settings.</summary>
        public static bool IsConfigured => provider != null;

        public static void InitializeArchives(string location)
        {
            provider = new ArchivesFileProvider(location);
            State = FileProviderState.Archives;
        }

        public static void InitializeExtracted(string location)
        {
            provider = new ExtractedFileProvider(location);
            State = FileProviderState.Extracted;
        }

        private static string NormalisePath(string location) => location.ToLower().Replace('/', '\\').TrimStart('\\');

        // Folders above the scenes opened so far, nearest first. Shared files such as the LEGO texture page
        // (/LEGOTpage/...) are looked for here when no game folder is set in Settings, or it doesn't have them.
        private static readonly List<string> looseRoots = new();

        public static void AddLooseRootsFor(string scenePath)
        {
            List<string> ancestors = new();
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(scenePath)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                ancestors.Add(dir);

            lock (looseRoots)
            {
                looseRoots.RemoveAll(ancestors.Contains);
                looseRoots.InsertRange(0, ancestors);
            }
        }

        public static RawFile GetFile(string location)
        {
            location = NormalisePath(location);

            RawFile file = provider?.GetFile(location);
            if (file != null)
                return file;

            string[] roots;
            lock (looseRoots)
                roots = looseRoots.ToArray();

            foreach (string root in roots)
            {
                string path = Path.Combine(root, location);
                if (File.Exists(path))
                {
                    // read into memory so a game install is never opened for writing
                    file = new RawFile(new MemoryStream(File.ReadAllBytes(path), false));
                    file.SetFileLocation(new FilesystemFileLocation(path));
                    return file;
                }
            }

            return null;
        }

        public static RawFile GetFileFromArchive(string archiveName, string location)
        {
            return ((ArchivesFileProvider)provider).GetFileFromArchive(archiveName, location);
        }

        public static RawFile GetFile(FileLocation location)
        {
            if (location is ArchiveFileLocation archiveFileLocation)
            {
                return ((ArchivesFileProvider)provider).GetFileFromArchive(archiveFileLocation.ArchiveName, archiveFileLocation.ArchiveFilePath);
            }
            else
            {
                return GetFile(location.FullPath);
            }
        }

        public static IEnumerable<RawFile> EnumerateFiles(params string[] extensions)
        {
            return provider.EnumerateFiles(extensions);
        }

        public static IEnumerable<FileLocation> EnumerateLocations(params string[] extensions)
        {
            return provider.EnumerateLocations(extensions);
        }

        public static FileLocation ReplaceInLocation(FileLocation location, string toReplace, string replacement)
        {
            if (location is ArchiveFileLocation archiveFileLocation)
            {
                return new ArchiveFileLocation(archiveFileLocation.ArchivePath, archiveFileLocation.ArchiveFilePath.Replace(toReplace, replacement));
            }
            else
            {
                return new FilesystemFileLocation(location.FullPath.Replace(toReplace, replacement));
            }
        }

        public static FileLocation ReplaceLocationExtension(FileLocation location, string replacement)
        {
            if (location is ArchiveFileLocation archiveFileLocation)
            {
                return new ArchiveFileLocation(archiveFileLocation.ArchivePath, Path.ChangeExtension(archiveFileLocation.ArchiveFilePath, replacement));
            }
            else
            {
                return new FilesystemFileLocation(Path.ChangeExtension(location.FullPath, replacement));
            }
        }

        public enum FileProviderState
        {
            Extracted,
            Archives
        }
    }
}

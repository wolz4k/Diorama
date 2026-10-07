namespace Diorama.Core
{
    /// <summary>
    /// Vector arrays start with a "ROTV" marker in some games and four zero bytes in others (DC Super-Villains).
    /// Reading notes which one a scene used, so saving it writes the same and the file stays byte-faithful.
    /// </summary>
    public static class VectorMarkers
    {
        [ThreadStatic] private static bool? seen;
        [ThreadStatic] private static bool? writing;

        public static void Saw(string marker)
        {
            seen ??= marker == "ROTV";
        }

        /// <summary>Starts noting markers for a new file.</summary>
        public static void Reset() => seen = null;

        /// <summary>What the file being read used: true for "ROTV", false for zeros, null if it had no vector arrays.</summary>
        public static bool? Seen => seen;

        /// <summary>The marker style to write: the scene being written's own, else the app setting.</summary>
        public static bool ShouldWrite => writing ?? AppSettings.ShouldWriteROTV;

        /// <summary>Writes with <paramref name="rotv"/>'s style until disposed (null: the app setting).</summary>
        public static IDisposable WriteAs(bool? rotv)
        {
            var previous = writing;
            writing = rotv;
            return new Restore(() => writing = previous);
        }

        private sealed class Restore(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}

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

        // Each array read keeps its own marker: a few files mix them (BUILDERFUSEBOX_CUBEMAPBG has one ROTV among zeros).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<bool>> perArray = new();

        /// <summary>Notes the marker an array was read with.</summary>
        public static void Saw(string marker, object array)
        {
            bool rotv = marker == "ROTV";
            seen ??= rotv;
            perArray.AddOrUpdate(array, new System.Runtime.CompilerServices.StrongBox<bool>(rotv));
        }

        /// <summary>The marker style to write for <paramref name="array"/>: what it was read with, else the scene's.</summary>
        public static bool ShouldWriteFor(object array) => perArray.TryGetValue(array, out var rotv) ? rotv.Value : ShouldWrite;

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

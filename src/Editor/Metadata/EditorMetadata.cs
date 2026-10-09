using Diorama.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Diorama.Editor.Metadata
{
    public class EditorMetadata
    {
        public ObservableCollection<EditorResourceReference> Resources { get; } = new();

        /// <summary>The resource list as read (see <see cref="Signature"/>); saving keeps the file's own header while it's unchanged.</summary>
        public string? LoadedSignature { get; set; }

        /// <summary>Everything the save writes about the resource list, in order, for spotting changes.</summary>
        public string Signature() => string.Join('\n', Resources.Select(r =>
            $"{r.Type}|{r.PlatformsAndClasses}|{r.FilePath?.ToLowerInvariant()}|{(r.Checksum == null ? "-" : Convert.ToHexString(r.Checksum))}"));
    }
}

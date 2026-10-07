using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Diorama.Core.Filetypes.GSC.Components
{
    public class NuVec : IVectorSerializable, ISchemaSerializable
    {
        public Vector3 Value;

        public void Deserialize(RawFile file, uint parentVersion)
        {
            Value = file.ReadVector3(true);
        }

        public void Handle(SchemaSerializer schema, uint parentVersion)
        {
            schema.HandleVector3(ref Value);
        }

        public void Serialize(RawFile file, uint parentVersion)
        {
            file.WriteVector3(Value, true);
        }
    }
}

using Colossal.Serialization.Entities;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    // IEmptySerializable keeps the ownership marker with a saved surface area.
    public struct GeneratedSurface : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }
}

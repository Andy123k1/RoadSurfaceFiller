using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    // Saved with each generated area. Native area triangulation erodes narrow
    // tapers and cannot reliably triangulate a reentrant roundabout approach.
    // Keep the triangles from our sampled road ribbon instead.
    public struct SurfaceMeshTriangle : IBufferElementData, ISerializable
    {
        public int3 Indices;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Indices.x);
            writer.Write(Indices.y);
            writer.Write(Indices.z);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Indices.x);
            reader.Read(out Indices.y);
            reader.Read(out Indices.z);
        }
    }

    public struct SurfaceSegmentSelected : IComponentData, IEmptySerializable { }
    public struct SurfaceSegmentExcluded : IComponentData, IEmptySerializable { }
    public struct SmallRoundaboutSurface : IComponentData, IEmptySerializable { }
    public struct ExactSurfaceBoundary : IComponentData, IEmptySerializable { }

    // Separate from the existing triangle serialization, so cities saved with
    // older versions still load. Limits are relative to the sampled deck.
    public struct SurfaceProjectionLimits : IComponentData, ISerializable
    {
        public float Minimum;
        public float Maximum;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Minimum);
            writer.Write(Maximum);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Minimum);
            reader.Read(out Maximum);
        }
    }
}

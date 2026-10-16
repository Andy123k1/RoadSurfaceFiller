using Game;
using Game.Rendering;

namespace SurfaceNetworkFilter
{
    // Restore geometry before native spatial indexing, then maintain the bounded
    // projection before area triangles are copied into GPU batches.
    internal sealed partial class SurfaceProjectionSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>().PrepareSurfaceRendering();
        }
    }
}

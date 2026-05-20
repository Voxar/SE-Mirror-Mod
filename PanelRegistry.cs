using System.Collections.Generic;
using Sandbox.ModAPI;
using IMyCubeBlock = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace MirrorCameraMod
{
    /// <summary>
    /// Mod's source-of-truth registry of every LCD surface running the
    /// Mirror or Camera TSS app. The Pulsar plugin reads this via cached
    /// reflection each render tick. Mod scripts mutate it from their
    /// event handlers — Add on construction / IsWorkingChanged-good /
    /// PropertiesChanged, Remove on Dispose / IsWorkingChanged-bad.
    /// </summary>
    public static class PanelRegistry
    {
        /// <summary>
        /// Contract version exposed to MirrorCameraPlugin. Bumped on
        /// breaking changes to PanelInfo, EnumeratePanels, or the
        /// Add/Update/Remove surface. Non-breaking additions don't bump.
        /// </summary>
        public const int ApiVersion = 1;

        public enum PanelMode { Mirror = 0, Camera = 1 }

        public struct PanelInfo
        {
            public IMyTextSurface Surface;
            public IMyCubeBlock Block;
            public int SurfaceIdx;
            public PanelMode Mode;
            public long CameraEntityId;  // 0 for Mirror mode
            public float Zoom;            // 1.0 for Mirror mode
            public float MaxViewDistance;
        }

        struct Key
        {
            public long BlockId;
            public int SurfaceIdx;
            public override int GetHashCode() => (int)(BlockId * 17 + SurfaceIdx);
            public override bool Equals(object o) {
                if (!(o is Key)) return false;
                var k = (Key)o;
                return k.BlockId == BlockId && k.SurfaceIdx == SurfaceIdx;
            }
        }

        // Sim-thread-only working set. SE mod scripts run on the sim thread, so
        // mutations are single-writer. The render thread never touches this.
        static readonly Dictionary<Key, PanelInfo> s_panels = new Dictionary<Key, PanelInfo>();

        // Immutable snapshot the render thread reads. Each mutation atomically
        // swaps a freshly-built array into this field. `volatile` ensures the
        // render thread sees a fully-initialized array without a lock — single
        // atomic ref read, zero contention with sim-thread mutations.
        // (System.Threading.Volatile is MDK-prohibited; the volatile keyword
        // on a reference-type field provides the same memory-ordering guarantee.)
        static volatile PanelInfo[] s_snapshot = new PanelInfo[0];

        public static void AddOrUpdate(IMyCubeBlock block, int surfaceIdx,
            IMyTextSurface surface, PanelMode mode, long cameraId, float zoom, float maxViewDistance)
        {
            if (block == null || surface == null) return;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            s_panels[k] = new PanelInfo {
                Surface = surface, Block = block, SurfaceIdx = surfaceIdx,
                Mode = mode, CameraEntityId = cameraId, Zoom = zoom, MaxViewDistance = maxViewDistance };
            RebuildSnapshot();
        }

        public static void Remove(IMyCubeBlock block, int surfaceIdx)
        {
            if (block == null) return;
            s_panels.Remove(new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx });
            RebuildSnapshot();
        }

        static void RebuildSnapshot()
        {
            var fresh = new PanelInfo[s_panels.Count];
            int i = 0;
            foreach (var v in s_panels.Values) fresh[i++] = v;
            // Field is volatile; assignment is an atomic ref swap with a
            // release barrier so other threads see a fully-populated array.
            s_snapshot = fresh;
        }

        public static IEnumerable<PanelInfo> EnumeratePanels()
        {
            // Volatile field read — render thread iterates an immutable array.
            // Never blocks the sim thread, never sees a torn dictionary state.
            return s_snapshot;
        }
    }
}

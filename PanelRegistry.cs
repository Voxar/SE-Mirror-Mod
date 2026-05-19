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

        static readonly Dictionary<Key, PanelInfo> s_panels = new Dictionary<Key, PanelInfo>();

        // Mutations come from the SIM thread (mod script event handlers).
        // Enumeration comes from the RENDER thread (plugin's DrawScene Prefix).
        // Without synchronization the render thread's enumerator throws
        // InvalidOperationException("Collection was modified") whenever a script
        // adds/removes a panel mid-iteration. Lock all access on s_panels.
        static readonly object s_lock = new object();

        public static void AddOrUpdate(IMyCubeBlock block, int surfaceIdx,
            IMyTextSurface surface, PanelMode mode, long cameraId, float zoom, float maxViewDistance)
        {
            if (block == null || surface == null) return;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            var info = new PanelInfo {
                Surface = surface, Block = block, SurfaceIdx = surfaceIdx,
                Mode = mode, CameraEntityId = cameraId, Zoom = zoom, MaxViewDistance = maxViewDistance };
            lock (s_lock) { s_panels[k] = info; }
        }

        public static void Remove(IMyCubeBlock block, int surfaceIdx)
        {
            if (block == null) return;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            lock (s_lock) { s_panels.Remove(k); }
        }

        public static IEnumerable<PanelInfo> EnumeratePanels()
        {
            // Return a defensive snapshot so the caller can iterate without
            // taking the lock for the duration of their work. PanelInfo is a
            // value type — copying the dictionary's values is cheap.
            lock (s_lock)
            {
                var snap = new PanelInfo[s_panels.Count];
                int i = 0;
                foreach (var v in s_panels.Values) snap[i++] = v;
                return snap;
            }
        }
    }
}

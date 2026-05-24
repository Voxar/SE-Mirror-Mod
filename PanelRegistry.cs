using System;
using System.Collections.Concurrent;
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
        ///
        /// <para>v4 (current): added per-surface
        /// <c>MirrorAngleDegX</c> / <c>MirrorAngleDegY</c> (yaw/pitch
        /// applied to the screen plane normal before reflection — lets
        /// the player aim a rear-view / side-view mirror without
        /// re-mounting the LCD).</para>
        /// </summary>
        public const int ApiVersion = 4;

        /// <summary>
        /// Plugin-pushed cap on the per-LCD mirror yaw/pitch slider's
        /// effective angle (degrees). The terminal slider's UI range
        /// reads this dynamically so the user can only dial in values
        /// the plugin will actually apply. Default permissive
        /// (<see cref="SurfaceSettings.MaxMirrorAngleDeg"/>) so the
        /// mod stays functional when the plugin isn't loaded or hasn't
        /// pushed a value yet. Non-breaking addition: missing setter on
        /// the plugin side just means the mod keeps the default cap.
        /// </summary>
        public static float MirrorMaxTiltDeg { get; private set; } = Settings.SurfaceSettings.MaxMirrorAngleDeg;

        /// <summary>Plugin-side setter for <see cref="MirrorMaxTiltDeg"/>.
        /// Bound by the plugin via reflection (same pattern as
        /// <see cref="SetStatus"/>). Clamped to [0, MaxMirrorAngleDeg]
        /// so a bad plugin push can't break the slider's UI.</summary>
        public static void SetMirrorMaxTiltDeg(float deg)
        {
            if (deg < 0f) deg = 0f;
            else if (deg > Settings.SurfaceSettings.MaxMirrorAngleDeg)
                deg = Settings.SurfaceSettings.MaxMirrorAngleDeg;
            MirrorMaxTiltDeg = deg;
        }

        public enum PanelMode { Mirror = 0, Camera = 1 }

        public struct PanelInfo
        {
            public IMyTextSurface Surface;
            public IMyCubeBlock   Block;
            public int            SurfaceIdx;
            public PanelMode      Mode;
            /// <summary>Camera block to render the view of.
            /// <c>null</c> for Mirror mode.</summary>
            public IMyCubeBlock   CameraBlock;
            public float          Zoom;            // 1.0 for Mirror mode
            /// <summary>Yaw applied to the screen plane normal before
            /// mirror reflection (degrees, positive = tilt toward screen
            /// Right). Mirror mode only; ignored in Camera mode.</summary>
            public float          MirrorAngleDegX;
            /// <summary>Pitch applied to the screen plane normal before
            /// mirror reflection (degrees, positive = tilt toward screen
            /// Up). Mirror mode only; ignored in Camera mode.</summary>
            public float          MirrorAngleDegY;
        }

        // IEquatable<Key> so the dictionary's lookup path stays on the
        // generic equality comparer and never boxes the struct.
        struct Key : IEquatable<Key>
        {
            public long BlockId;
            public int  SurfaceIdx;

            public bool Equals(Key other)
                => BlockId == other.BlockId && SurfaceIdx == other.SurfaceIdx;

            public override bool Equals(object o)
            {
                if (!(o is Key)) return false;
                return Equals((Key)o);
            }
            public override int GetHashCode() => (int)(BlockId * 17 + SurfaceIdx);
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

        // Per-panel status string written by the plugin (render thread) and
        // read by mod TSS scripts (sim thread). Kept OUT of PanelInfo so the
        // panel snapshot stays sim-thread-only; status is the one piece of
        // state that legitimately crosses threads, so it gets its own thread-
        // safe container. ConcurrentDictionary's lock-free read path is what
        // the mod TSS uses on every Run() tick — needs to be cheap.
        static readonly ConcurrentDictionary<Key, string> s_status =
            new ConcurrentDictionary<Key, string>();

        public static void AddOrUpdate(IMyCubeBlock block, int surfaceIdx,
            IMyTextSurface surface, PanelMode mode,
            IMyCubeBlock cameraBlock, float zoom,
            float mirrorAngleDegX, float mirrorAngleDegY)
        {
            if (block == null || surface == null) return;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            s_panels[k] = new PanelInfo {
                Surface         = surface,
                Block           = block,
                SurfaceIdx      = surfaceIdx,
                Mode            = mode,
                CameraBlock     = cameraBlock,
                Zoom            = zoom,
                MirrorAngleDegX = mirrorAngleDegX,
                MirrorAngleDegY = mirrorAngleDegY,
            };
            RebuildSnapshot();
        }

        public static void Remove(IMyCubeBlock block, int surfaceIdx)
        {
            if (block == null) return;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            s_panels.Remove(k);
            // Clear any plugin status for the panel — leaving it would
            // surface a stale "rendered" / "failed" on the next time the
            // same key registers (e.g. block rebuild).
            string ignore;
            s_status.TryRemove(k, out ignore);
            RebuildSnapshot();
        }

        /// <summary>
        /// Plugin-side status writer. Called from the render thread to
        /// signal what's happening to a panel (panel found, last render
        /// success/failure, etc.). Mod TSS scripts read this back via
        /// <see cref="GetStatus"/> and show it as the panel's subtitle.
        /// <para>Pass <c>null</c> for status to clear an entry.</para>
        /// </summary>
        public static void SetStatus(long blockId, int surfaceIdx, string status)
        {
            var k = new Key { BlockId = blockId, SurfaceIdx = surfaceIdx };
            if (status == null)
            {
                string ignore;
                s_status.TryRemove(k, out ignore);
            }
            else
            {
                s_status[k] = status;
            }
        }

        /// <summary>
        /// Sim-thread side status reader. Mod TSS scripts call this each
        /// Run() tick to pick a subtitle. Returns <c>null</c> when no
        /// status has been written (e.g. plugin not loaded, or panel just
        /// registered and not yet processed by the plugin).
        /// </summary>
        public static string GetStatus(IMyCubeBlock block, int surfaceIdx)
        {
            if (block == null) return null;
            var k = new Key { BlockId = block.EntityId, SurfaceIdx = surfaceIdx };
            string s;
            return s_status.TryGetValue(k, out s) ? s : null;
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

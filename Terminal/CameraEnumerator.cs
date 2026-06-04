using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using IMyCameraBlock = Sandbox.ModAPI.IMyCameraBlock;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace MirrorCameraMod.Terminal
{
    /// <summary>
    /// Walks the block's mechanical-grid group to gather every
    /// <see cref="IMyCameraBlock"/> the user can pick from. Shared
    /// between the terminal listbox (which renders the gathered list)
    /// and the Camera TSS (which uses <see cref="GetEffectiveCameraId"/>
    /// to pick a default before the user has touched the listbox).
    ///
    /// <para>"Mechanical group" means main grid + any subgrid linked by
    /// pistons, rotors, hinges, suspensions — every block the player
    /// would consider "part of the same construction" without separately
    /// docking. Connectors are excluded by using
    /// <see cref="GridLinkTypeEnum.Mechanical"/>.</para>
    /// </summary>
    public static class CameraEnumerator
    {
        /// <summary>Fill <paramref name="items"/> with one entry per
        /// camera and append the currently-selected item to
        /// <paramref name="selected"/>. Empty list produces a single
        /// disabled "(no cameras)" placeholder so the listbox is never
        /// visually empty.
        ///
        /// <para>If the surface has no stored camera id but at least
        /// one camera exists on the grid, the first camera is persisted
        /// as the explicit pick before the items are emitted — so the
        /// listbox's highlighted entry always matches the stored id
        /// from the user's perspective (no "you see one highlighted
        /// but storage thinks nothing is picked" mismatch). After this
        /// runs once for a fresh surface, <see cref="MirrorStorage.GetCameraId"/>
        /// always returns the same value as <see cref="GetEffectiveCameraId"/>.</para></summary>
        public static void PopulateListbox(
            IMyTerminalBlock block, int surfaceIdx,
            List<MyTerminalControlListBoxItem> items,
            List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || block.CubeGrid == null) return;

            var cameras = GatherCameras(block);
            if (cameras.Count == 0)
            {
                items.Add(new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute("(no cameras)"),
                    MyStringId.GetOrCompute(
                        "No camera blocks on this grid or any mechanically-connected subgrid."),
                    0L));
                return;
            }

            long currentId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            if (currentId == 0L)
            {
                // Auto-pick the first camera so storage matches what
                // the user sees highlighted. From this point on the
                // listbox's "selected" set is always backed by the
                // stored id rather than a fallback-only inference.
                currentId = cameras[0].EntityId;
                Settings.MirrorStorage.SetCameraId(block, surfaceIdx, currentId);
            }

            foreach (var cam in cameras)
            {
                var label = string.IsNullOrEmpty(cam.CustomName) ? "Camera" : cam.CustomName;
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute(label),
                    MyStringId.GetOrCompute("Display this camera's view."),
                    cam.EntityId);
                items.Add(item);
                if (cam.EntityId == currentId) selected.Add(item);
            }
        }

        /// <summary>Cycle the surface's selected camera by
        /// <paramref name="direction"/> (+1 for next, -1 for previous),
        /// wrapping at the list ends. Persists via
        /// <see cref="Settings.MirrorStorage.SetCameraId"/> — the next
        /// render tick picks up the new selection. No-op when no
        /// cameras exist on the mechanical group.</summary>
        public static void CycleSelectedCamera(
            IMyTerminalBlock block, int surfaceIdx, int direction)
        {
            if (block == null || direction == 0) return;
            var cameras = GatherCameras(block);
            if (cameras.Count == 0) return;

            long currentId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            int currentIdx = -1;
            for (int i = 0; i < cameras.Count; i++)
                if (cameras[i].EntityId == currentId) { currentIdx = i; break; }

            // currentIdx == -1 when nothing is selected (or the stored
            // id no longer exists). Treat as "before first" so +1 picks
            // the first camera and -1 picks the last.
            int n = cameras.Count;
            int nextIdx = currentIdx < 0
                ? (direction > 0 ? 0 : n - 1)
                : ((currentIdx + direction) % n + n) % n;

            Settings.MirrorStorage.SetCameraId(block, surfaceIdx, cameras[nextIdx].EntityId);
        }

        /// <summary>
        /// Camera id for a surface: the stored selection if non-zero,
        /// otherwise the first available camera on the mechanical group
        /// as a <b>transient</b> fallback (NOT persisted). The renderer
        /// uses this so a freshly-set Camera app shows something
        /// immediately, before the user has opened the terminal to
        /// trigger <see cref="PopulateListbox"/>'s explicit auto-pick.
        /// </summary>
        /// <remarks>
        /// Persisting the auto-pick here used to be an optimisation
        /// (skip the per-tick <see cref="GatherCameras"/> walk once a
        /// pick was stored), but it raced against block deserialisation:
        /// when the TSS first ticked, <c>entity.Storage</c> could still
        /// be null for a freshly-deserialised block, so the lazy-load
        /// in <see cref="Settings.MirrorStorage.GetCameraId"/> returned
        /// 0 even when the on-disk blob held a valid id. The auto-pick
        /// then wrote <c>cameras[0].EntityId</c> into
        /// <c>MirrorStorage.s_state</c> keyed by EntityId, and once SE
        /// finished restoring the real Storage the now-correct blob
        /// was masked by the polluted cache (subsequent <c>Get</c> hits
        /// never re-read disk). User-visible: panel reverts to "first
        /// camera" after every reload.
        /// </remarks>
        public static long GetEffectiveCameraId(IMyEntity entity, int surfaceIdx)
        {
            long stored = Settings.MirrorStorage.GetCameraId(entity, surfaceIdx);
            if (stored != 0L) return stored;

            var block = entity as IMyCubeBlock;
            if (block == null) return 0L;
            var cameras = GatherCameras(block);
            if (cameras.Count == 0) return 0L;

            return cameras[0].EntityId;
        }

        // ── Internal ────────────────────────────────────────────────────

        // Local lists per call. An earlier version reused static buffers
        // on the assumption that mod scripts run single-threaded on the
        // sim thread, but entity init during world load fans out across
        // worker threads — two CameraScript constructors initialising
        // in parallel raced on the static buffers and threw
        // "Collection was modified; enumeration operation may not
        // execute." inside the foreach, which SE surfaced as an
        // unhandled exception during load and produced the "world is
        // corrupted" error screen (intermittent: only when two
        // mirror/camera LCDs initialised on different worker threads
        // overlapped). Not a hot path — GetEffectiveCameraId short-
        // circuits once a camera id is stored, so this only runs once
        // per fresh surface.
        static List<IMyCameraBlock> GatherCameras(IMyCubeBlock block)
        {
            var cameras = new List<IMyCameraBlock>();
            if (block == null || block.CubeGrid == null) return cameras;

            var grids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, grids);

            var slims = new List<IMySlimBlock>();
            foreach (var g in grids)
                g.GetBlocks(slims, b => b.FatBlock is IMyCameraBlock);

            foreach (var slim in slims)
            {
                var cam = slim.FatBlock as IMyCameraBlock;
                if (cam != null) cameras.Add(cam);
            }
            return cameras;
        }
    }
}

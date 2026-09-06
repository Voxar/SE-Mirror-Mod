using System;
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
    /// Gathers the cameras a Camera-app surface can pick from and
    /// renders them into the terminal listbox. Two sources, selected by
    /// the surface's <see cref="Settings.SurfaceSettings.RemoteCameras"/>
    /// flag:
    ///
    /// <list type="bullet">
    ///   <item><b>Local</b>: every camera on the block's logical group —
    ///         main grid + subgrids linked by pistons, rotors, hinges,
    ///         suspensions, plus grids docked via connector. The same
    ///         set SE calls one construct and shows in a single
    ///         terminal (<see cref="GridLinkTypeEnum.Logical"/>).</item>
    ///   <item><b>Remote</b>: cameras on other constructs reachable over
    ///         the antenna network. The mod cannot run that query
    ///         (nothing antenna-related is on the mod whitelist), so it
    ///         asks the plugin through
    ///         <see cref="PanelRegistry.RemoteCameraProvider"/>. Without
    ///         the plugin the remote list is empty.</item>
    /// </list>
    ///
    /// <para>Shared between the listbox, the Next/Previous actions and
    /// the Camera TSS, which uses <see cref="GetEffectiveCameraId"/> to
    /// pick a default before the user has touched the listbox.</para>
    /// </summary>
    public static class CameraEnumerator
    {
        /// <summary>Listbox user data for a grid header row. Selecting
        /// the row picks <see cref="FirstCameraId"/>; the terminal's
        /// deferred refresh then highlights that camera.</summary>
        public sealed class GridHeader
        {
            public readonly long FirstCameraId;
            public GridHeader(long firstCameraId) { FirstCameraId = firstCameraId; }
        }

        /// <summary>One listable camera and the grid it is shown under.
        /// Local: the camera's own grid. Remote: the construct's header
        /// grid as chosen by the plugin.</summary>
        struct CameraItem
        {
            public IMyCubeGrid    Grid;
            public IMyCameraBlock Camera;
        }

        /// <summary>Fill <paramref name="items"/> with the surface's
        /// camera list and append the currently-selected item to
        /// <paramref name="selected"/>. Empty list produces a single
        /// "(no cameras)" placeholder so the listbox is never visually
        /// empty. When the list spans more than one grid, each grid's
        /// cameras sit under a header row carrying the grid's name.
        ///
        /// <para>Local mode only: if the surface has no stored camera id
        /// but at least one camera exists, the first camera is
        /// persisted as the explicit pick before the items are emitted —
        /// so the listbox's highlighted entry always matches the stored
        /// id from the user's perspective. Remote mode never auto-picks;
        /// pointing a panel at a far camera is the user's call.</para></summary>
        public static void PopulateListbox(
            IMyTerminalBlock block, int surfaceIdx,
            List<MyTerminalControlListBoxItem> items,
            List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || block.CubeGrid == null) return;

            bool remote  = Settings.MirrorStorage.GetRemoteCameras(block, surfaceIdx);
            var  cameras = GatherCameraItems(block, remote);
            if (cameras.Count == 0)
            {
                items.Add(new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute("(no cameras)"),
                    MyStringId.GetOrCompute(
                        "No camera blocks on this grid, its subgrids or docked grids."),
                    0L));
                return;
            }

            long currentId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            if (currentId == 0L && !remote)
            {
                currentId = cameras[0].Camera.EntityId;
                Settings.MirrorStorage.SetCameraId(block, surfaceIdx, currentId);
            }

            bool headers = SpansMultipleGrids(cameras);
            IMyCubeGrid lastGrid = null;
            for (int i = 0; i < cameras.Count; i++)
            {
                var it = cameras[i];
                if (headers && !ReferenceEquals(it.Grid, lastGrid))
                {
                    lastGrid = it.Grid;
                    items.Add(new MyTerminalControlListBoxItem(
                        MyStringId.GetOrCompute(GridLabel(it.Grid)),
                        MyStringId.GetOrCompute("Show the first camera on this grid."),
                        new GridHeader(it.Camera.EntityId)));
                }

                string label = CameraLabel(it.Camera);
                if (headers) label = "  " + label;
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute(label),
                    MyStringId.GetOrCompute("Display this camera's view."),
                    it.Camera.EntityId);
                items.Add(item);
                if (it.Camera.EntityId == currentId) selected.Add(item);
            }
        }

        /// <summary>Cycle the surface's selected camera by
        /// <paramref name="direction"/> (+1 for next, -1 for previous)
        /// through the list the surface's remote flag selects, in the
        /// listbox's display order, wrapping at the ends. Persists via
        /// <see cref="Settings.MirrorStorage.SetCameraId"/> — the next
        /// render tick picks up the new selection. No-op when the list
        /// is empty.</summary>
        public static void CycleSelectedCamera(
            IMyTerminalBlock block, int surfaceIdx, int direction)
        {
            if (block == null || direction == 0) return;
            bool remote  = Settings.MirrorStorage.GetRemoteCameras(block, surfaceIdx);
            var  cameras = GatherCameraItems(block, remote);
            if (cameras.Count == 0) return;

            long currentId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            int currentIdx = -1;
            for (int i = 0; i < cameras.Count; i++)
                if (cameras[i].Camera.EntityId == currentId) { currentIdx = i; break; }

            // currentIdx == -1 when nothing is selected (or the stored
            // id is not in this list). Treat as "before first" so +1
            // picks the first camera and -1 picks the last.
            int n = cameras.Count;
            int nextIdx = currentIdx < 0
                ? (direction > 0 ? 0 : n - 1)
                : ((currentIdx + direction) % n + n) % n;

            Settings.MirrorStorage.SetCameraId(block, surfaceIdx, cameras[nextIdx].Camera.EntityId);
        }

        /// <summary>
        /// Camera id for a surface: the stored selection if non-zero,
        /// otherwise the first available camera on the logical group
        /// as a <b>transient</b> fallback (NOT persisted). The renderer
        /// uses this so a freshly-set Camera app shows something
        /// immediately, before the user has opened the terminal to
        /// trigger <see cref="PopulateListbox"/>'s explicit auto-pick.
        /// Always local: a remote fallback would point a panel at a far
        /// camera without anyone choosing it.
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

        /// <summary>True when the plugin reports <paramref name="cameraId"/>
        /// among the cameras reachable from <paramref name="block"/>'s
        /// grid over the antenna network. With no plugin there is
        /// nothing to check against, so this returns true and the caller
        /// keeps the selection; the plugin is also what would render it.</summary>
        public static bool IsReachableRemote(IMyCubeBlock block, long cameraId)
        {
            var provider = PanelRegistry.RemoteCameraProvider;
            if (provider == null) return true;
            if (block == null) return false;

            var pairs = new List<long>();
            if (!TryQueryProvider(provider, block.EntityId, pairs)) return false;
            for (int i = 1; i < pairs.Count; i += 2)
                if (pairs[i] == cameraId) return true;
            return false;
        }

        // ── Internal ────────────────────────────────────────────────────

        static List<CameraItem> GatherCameraItems(IMyCubeBlock block, bool remote)
        {
            var items = new List<CameraItem>();
            if (remote) GatherRemote(block, items);
            else        GatherLocal(block, items);
            items.Sort(CompareItems);
            return items;
        }

        static void GatherLocal(IMyCubeBlock block, List<CameraItem> items)
        {
            var cameras = GatherCameras(block);
            for (int i = 0; i < cameras.Count; i++)
                items.Add(new CameraItem { Grid = cameras[i].CubeGrid, Camera = cameras[i] });
        }

        // Provider pairs → entities. A pair whose grid or camera can't
        // be found on this client (destroyed, outside MP sync range) is
        // skipped; the plugin only reports loaded entities anyway.
        static void GatherRemote(IMyCubeBlock block, List<CameraItem> items)
        {
            var provider = PanelRegistry.RemoteCameraProvider;
            if (provider == null || block == null) return;

            var pairs = new List<long>();
            if (!TryQueryProvider(provider, block.EntityId, pairs)) return;

            for (int i = 0; i + 1 < pairs.Count; i += 2)
            {
                IMyEntity gridEnt, camEnt;
                if (!MyAPIGateway.Entities.TryGetEntityById(pairs[i],     out gridEnt)) continue;
                if (!MyAPIGateway.Entities.TryGetEntityById(pairs[i + 1], out camEnt))  continue;
                var grid = gridEnt as IMyCubeGrid;
                var cam  = camEnt  as IMyCameraBlock;
                if (grid == null || cam == null) continue;
                items.Add(new CameraItem { Grid = grid, Camera = cam });
            }
        }

        // The provider runs plugin code. A throw is a plugin bug: log
        // it once (unconditional — it's an error) and treat the answer
        // as "nothing reachable" so the mod keeps working.
        static bool s_providerFaultLogged;

        static bool TryQueryProvider(Action<long, List<long>> provider, long blockId, List<long> pairs)
        {
            try { provider(blockId, pairs); return true; }
            catch (Exception ex)
            {
                if (!s_providerFaultLogged)
                {
                    s_providerFaultLogged = true;
                    MyLog.Default.WriteLine("[MirrorMod] RemoteCameraProvider threw: " + ex);
                }
                return false;
            }
        }

        // Grids by name, cameras by name within a grid. Two grids with
        // the same name fall back to entity id so each grid's cameras
        // still sit together under one header.
        static int CompareItems(CameraItem a, CameraItem b)
        {
            if (!ReferenceEquals(a.Grid, b.Grid))
            {
                int c = string.Compare(GridLabel(a.Grid), GridLabel(b.Grid), StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return a.Grid.EntityId.CompareTo(b.Grid.EntityId);
            }
            return string.Compare(CameraLabel(a.Camera), CameraLabel(b.Camera), StringComparison.OrdinalIgnoreCase);
        }

        static bool SpansMultipleGrids(List<CameraItem> items)
        {
            for (int i = 1; i < items.Count; i++)
                if (!ReferenceEquals(items[i].Grid, items[0].Grid)) return true;
            return false;
        }

        static string GridLabel(IMyCubeGrid grid)
            => !string.IsNullOrEmpty(grid.CustomName) ? grid.CustomName
             : (grid.DisplayName ?? "Grid");

        static string CameraLabel(IMyCameraBlock cam)
            => string.IsNullOrEmpty(cam.CustomName) ? "Camera" : cam.CustomName;

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
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Logical, grids);

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

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
    /// the surface's <see cref="Settings.SurfaceSettings.ShowRemoteCameras"/>
    /// flag:
    ///
    /// <list type="bullet">
    ///   <item><b>Construct</b>: every camera on the block's logical
    ///         group — main grid + subgrids linked by pistons, rotors,
    ///         hinges, suspensions, plus grids docked via connector. The
    ///         same set SE calls one construct and shows in a single
    ///         terminal (<see cref="GridLinkTypeEnum.Logical"/>).</item>
    ///   <item><b>Relayed</b>: cameras on other constructs reachable over
    ///         the antenna network. The mod cannot run that query
    ///         (nothing antenna-related is on the mod whitelist), so it
    ///         asks the plugin through
    ///         <see cref="PanelRegistry.RelayedCameraPairsProvider"/>.
    ///         Without the plugin the relayed list is empty.</item>
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
        public sealed class GridHeaderRow
        {
            public readonly long FirstCameraId;
            public GridHeaderRow(long firstCameraId) { FirstCameraId = firstCameraId; }
        }

        /// <summary>One camera in the list and the grid it is attributed
        /// to. Construct list: the camera's own grid, unused for display.
        /// Relayed list: the construct's header grid as chosen by the
        /// plugin, shown as the header row.</summary>
        struct ListedCamera
        {
            public IMyCubeGrid    HeaderGrid;
            public IMyCameraBlock Camera;
        }

        /// <summary>Fill <paramref name="items"/> with the surface's
        /// camera list and append the currently-selected item to
        /// <paramref name="selected"/>. Empty list produces a single
        /// "(no cameras)" placeholder so the listbox is never visually
        /// empty. The construct list is flat, in gather order. The
        /// relayed list is ordered by grid name then camera name, with
        /// each construct's cameras under a header row carrying the
        /// grid's name.
        ///
        /// <para>Construct list only: if the surface has no stored
        /// camera id but at least one camera exists, the first camera is
        /// persisted as the explicit pick before the items are emitted —
        /// so the listbox's highlighted entry always matches the stored
        /// id from the user's perspective (no "you see one highlighted
        /// but storage thinks nothing is picked" mismatch). After this
        /// runs once for a fresh surface, <see cref="Settings.MirrorStorage.GetCameraId"/>
        /// always returns the same value as <see cref="GetEffectiveCameraId"/>.
        /// The relayed list never auto-picks; pointing a panel at a far
        /// camera is the user's call.</para></summary>
        public static void PopulateListbox(
            IMyTerminalBlock block, int surfaceIdx,
            List<MyTerminalControlListBoxItem> items,
            List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || block.CubeGrid == null) return;

            bool showRemote    = Settings.MirrorStorage.GetShowRemoteCameras(block, surfaceIdx);
            var  listedCameras = ListCamerasForSurface(block, showRemote);
            if (listedCameras.Count == 0)
            {
                items.Add(new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute("(no cameras)"),
                    MyStringId.NullOrEmpty,
                    0L));
                return;
            }

            long selectedCameraId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            if (selectedCameraId == 0L && !showRemote)
            {
                // Auto-pick the first camera so storage matches what
                // the user sees highlighted. From this point on the
                // listbox's "selected" set is always backed by the
                // stored id rather than a fallback-only inference.
                selectedCameraId = listedCameras[0].Camera.EntityId;
                Settings.MirrorStorage.SetCameraId(block, surfaceIdx, selectedCameraId);
            }

            bool showGridHeaders = showRemote;
            IMyCubeGrid previousHeaderGrid = null;
            for (int i = 0; i < listedCameras.Count; i++)
            {
                var listed = listedCameras[i];
                if (showGridHeaders && !ReferenceEquals(listed.HeaderGrid, previousHeaderGrid))
                {
                    previousHeaderGrid = listed.HeaderGrid;
                    items.Add(new MyTerminalControlListBoxItem(
                        MyStringId.GetOrCompute(GridLabel(listed.HeaderGrid)),
                        MyStringId.NullOrEmpty,
                        new GridHeaderRow(listed.Camera.EntityId)));
                }

                string label = CameraLabel(listed.Camera);
                if (showGridHeaders) label = "  " + label;
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute(label),
                    MyStringId.NullOrEmpty,
                    listed.Camera.EntityId);
                items.Add(item);
                if (listed.Camera.EntityId == selectedCameraId) selected.Add(item);
            }
        }

        /// <summary>Cycle the surface's selected camera by
        /// <paramref name="direction"/> (+1 for next, -1 for previous)
        /// through the list the surface's Show Remote Cameras flag
        /// selects, in the listbox's display order, wrapping at the
        /// ends. Persists via <see cref="Settings.MirrorStorage.SetCameraId"/>
        /// — the next render tick picks up the new selection. No-op when
        /// the list is empty.</summary>
        public static void CycleSelectedCamera(
            IMyTerminalBlock block, int surfaceIdx, int direction)
        {
            if (block == null || direction == 0) return;
            bool showRemote    = Settings.MirrorStorage.GetShowRemoteCameras(block, surfaceIdx);
            var  listedCameras = ListCamerasForSurface(block, showRemote);
            if (listedCameras.Count == 0) return;

            long selectedCameraId = Settings.MirrorStorage.GetCameraId(block, surfaceIdx);
            int selectedIndex = -1;
            for (int i = 0; i < listedCameras.Count; i++)
                if (listedCameras[i].Camera.EntityId == selectedCameraId) { selectedIndex = i; break; }

            // selectedIndex == -1 when nothing is selected (or the stored
            // id is not in this list). Treat as "before first" so +1
            // picks the first camera and -1 picks the last.
            int count = listedCameras.Count;
            int nextIndex = selectedIndex < 0
                ? (direction > 0 ? 0 : count - 1)
                : ((selectedIndex + direction) % count + count) % count;

            Settings.MirrorStorage.SetCameraId(block, surfaceIdx, listedCameras[nextIndex].Camera.EntityId);
        }

        /// <summary>
        /// Camera id for a surface: the stored selection if non-zero,
        /// otherwise the first available camera on the logical group
        /// as a <b>transient</b> fallback (NOT persisted). The renderer
        /// uses this so a freshly-set Camera app shows something
        /// immediately, before the user has opened the terminal to
        /// trigger <see cref="PopulateListbox"/>'s explicit auto-pick.
        /// Always the construct's cameras: a relayed fallback would
        /// point a panel at a far camera without anyone choosing it.
        /// </summary>
        /// <remarks>
        /// Persisting the auto-pick here used to be an optimisation
        /// (skip the per-tick <see cref="GatherConstructCameras"/> walk
        /// once a pick was stored), but it raced against block
        /// deserialisation: when the TSS first ticked,
        /// <c>entity.Storage</c> could still be null for a
        /// freshly-deserialised block, so the lazy-load in
        /// <see cref="Settings.MirrorStorage.GetCameraId"/> returned 0
        /// even when the on-disk blob held a valid id. The auto-pick
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
            var cameras = GatherConstructCameras(block);
            if (cameras.Count == 0) return 0L;

            return cameras[0].EntityId;
        }

        /// <summary>True when the plugin reports <paramref name="cameraId"/>
        /// among the cameras relayed to <paramref name="panelBlock"/>'s
        /// grid over the antenna network. With no plugin there is
        /// nothing to check against, so this returns true and the caller
        /// keeps the selection; the plugin is also what would render it.</summary>
        public static bool IsCameraRelayedTo(IMyCubeBlock panelBlock, long cameraId)
        {
            var provider = PanelRegistry.RelayedCameraPairsProvider;
            if (provider == null) return true;
            if (panelBlock == null) return false;

            var gridCameraPairs = new List<long>();
            if (!TryAskPluginForRelayedCameras(provider, panelBlock.EntityId, gridCameraPairs)) return false;
            for (int i = 1; i < gridCameraPairs.Count; i += 2)
                if (gridCameraPairs[i] == cameraId) return true;
            return false;
        }

        // ── Internal ────────────────────────────────────────────────────

        static List<ListedCamera> ListCamerasForSurface(IMyCubeBlock block, bool showRemote)
        {
            var listedCameras = new List<ListedCamera>();
            if (showRemote)
            {
                AddRelayedCameras(block, listedCameras);
                listedCameras.Sort(CompareListedCameras);
            }
            else
            {
                // Gather order, unsorted — the construct list's order
                // has always been this and nobody asked for another.
                AddConstructCameras(block, listedCameras);
            }
            return listedCameras;
        }

        static void AddConstructCameras(IMyCubeBlock block, List<ListedCamera> listedCameras)
        {
            var cameras = GatherConstructCameras(block);
            for (int i = 0; i < cameras.Count; i++)
                listedCameras.Add(new ListedCamera { HeaderGrid = cameras[i].CubeGrid, Camera = cameras[i] });
        }

        // Plugin pairs → entities. A pair whose grid or camera can't be
        // found on this client (destroyed, outside MP sync range) is
        // skipped; the plugin only reports loaded entities anyway.
        static void AddRelayedCameras(IMyCubeBlock block, List<ListedCamera> listedCameras)
        {
            var provider = PanelRegistry.RelayedCameraPairsProvider;
            if (provider == null || block == null) return;

            var gridCameraPairs = new List<long>();
            if (!TryAskPluginForRelayedCameras(provider, block.EntityId, gridCameraPairs)) return;

            for (int i = 0; i + 1 < gridCameraPairs.Count; i += 2)
            {
                IMyEntity gridEntity, cameraEntity;
                if (!MyAPIGateway.Entities.TryGetEntityById(gridCameraPairs[i],     out gridEntity))   continue;
                if (!MyAPIGateway.Entities.TryGetEntityById(gridCameraPairs[i + 1], out cameraEntity)) continue;
                var headerGrid = gridEntity   as IMyCubeGrid;
                var camera     = cameraEntity as IMyCameraBlock;
                if (headerGrid == null || camera == null) continue;
                listedCameras.Add(new ListedCamera { HeaderGrid = headerGrid, Camera = camera });
            }
        }

        // The provider runs plugin code. A throw is a plugin bug: log
        // it once (unconditional — it's an error) and treat the answer
        // as "nothing relayed" so the mod keeps working.
        static bool s_pluginFaultLogged;

        static bool TryAskPluginForRelayedCameras(
            Action<long, List<long>> provider, long panelBlockId, List<long> gridCameraPairs)
        {
            try { provider(panelBlockId, gridCameraPairs); return true; }
            catch (Exception ex)
            {
                if (!s_pluginFaultLogged)
                {
                    s_pluginFaultLogged = true;
                    MyLog.Default.WriteLine("[MirrorMod] RelayedCameraPairsProvider threw: " + ex);
                }
                return false;
            }
        }

        // Grids by name, cameras by name within a grid. Two grids with
        // the same name fall back to entity id so each grid's cameras
        // still sit together under one header.
        static int CompareListedCameras(ListedCamera x, ListedCamera y)
        {
            if (!ReferenceEquals(x.HeaderGrid, y.HeaderGrid))
            {
                int byGridName = string.Compare(
                    GridLabel(x.HeaderGrid), GridLabel(y.HeaderGrid), StringComparison.OrdinalIgnoreCase);
                if (byGridName != 0) return byGridName;
                return x.HeaderGrid.EntityId.CompareTo(y.HeaderGrid.EntityId);
            }
            return string.Compare(CameraLabel(x.Camera), CameraLabel(y.Camera), StringComparison.OrdinalIgnoreCase);
        }

        static string GridLabel(IMyCubeGrid grid)
            => !string.IsNullOrEmpty(grid.CustomName) ? grid.CustomName
             : (grid.DisplayName ?? "Grid");

        static string CameraLabel(IMyCameraBlock camera)
            => string.IsNullOrEmpty(camera.CustomName) ? "Camera" : camera.CustomName;

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
        static List<IMyCameraBlock> GatherConstructCameras(IMyCubeBlock block)
        {
            var cameras = new List<IMyCameraBlock>();
            if (block == null || block.CubeGrid == null) return cameras;

            var grids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Logical, grids);

            var slims = new List<IMySlimBlock>();
            foreach (var grid in grids)
                grid.GetBlocks(slims, slim => slim.FatBlock is IMyCameraBlock);

            foreach (var slim in slims)
            {
                var camera = slim.FatBlock as IMyCameraBlock;
                if (camera != null) cameras.Add(camera);
            }
            return cameras;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;
using IMyCameraBlock = Sandbox.ModAPI.IMyCameraBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Mirror
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MirrorSession : MySessionComponentBase
    {
        // Matches the guid in Content/Data/EntityComponents.sbc — must stay in sync
        // or the per-panel camera selection won't persist across world save/reload.
        public static readonly Guid StorageGuid = new Guid("63e4c22f-37b6-4c26-a486-6abd634fc504");
        public const string MirrorScriptId = "Mirror";
        public const string CameraScriptId = "Camera";
        const string ControlId = "Mirror.CameraSource";

        // Channel for paint requests sent from mod TSS to the helper.
        // Payload format: long[] { panelEntityId, cameraEntityId, surfaceIdx, zoomX1000 }.
        // Shorter payloads remain valid (the helper defaults missing
        // surfaceIdx to 0 and missing zoom to 1.0× = 1000). Zoom is
        // encoded as fixed-point milli-zoom so the channel stays long[].
        public const long PaintRequestChannel = 0x4D6972726F724331L; // "MirrorC1" — arbitrary stable channel id

        // Camera zoom range — slider goes 1.0× (no zoom) up to MaxZoom.
        // FOV is divided by zoom: at 8× the FOV is one-eighth of the
        // configured camera FOV, giving a strong telephoto.
        public const float MinZoom = 1.0f;
        public const float MaxZoom = 15.0f;
        // Per-surface render-range slider. Helper skips the panel render
        // when the main-view camera is farther than this from the LCD.
        // Matches the bounds + default the CameraLCD-Remastered plugin
        // uses (10..500m, default 40m), which is the proven setting
        // across the community.
        public const float MinRange = 10f;
        public const float MaxRange = 500f;
        public const float DefaultRange = 40f;

        private static bool s_hooked;
        // Single listbox + slider shared across all blocks. They track
        // the surface the user has highlighted in the standard
        // "LCD Panels" listbox so the controls switch focus as the user
        // clicks different surfaces — same UX as SE's native per-surface
        // controls (Content, Script, ScriptForegroundColor, etc.).
        private static IMyTerminalControlListbox s_cameraListbox;
        private static IMyTerminalControlSlider s_zoomSlider;
        private static IMyTerminalControlSlider s_rangeSlider;

        // Use CustomControlGetter, NOT AddControl. Calling AddControl from
        // LoadData causes MyTerminalControlFactory.m_controls[typeof(MyTextPanel)]
        // to be populated before SE has lazy-initialized the LCD's standard
        // controls. MyTextPanel.CreateTerminalControls then short-circuits on
        // AreControlsCreated<MyTextPanel>() == true and never adds any of the
        // defaults — wiping the entire LCD terminal UI.
        public override void LoadData()
        {
            if (s_hooked) return;
            s_hooked = true;
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlGetter;
        }

        protected override void UnloadData()
        {
            if (!s_hooked) return;
            MyAPIGateway.TerminalControls.CustomControlGetter -= OnCustomControlGetter;
            s_cameraListbox = null;
            s_zoomSlider = null;
            s_rangeSlider = null;
            s_hooked = false;
        }

        private static void OnCustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            // Any block exposing one or more text surfaces qualifies — covers
            // single-surface text panels, cockpits, programmable blocks,
            // custom turret controllers, etc.
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return;
            if (s_cameraListbox == null) s_cameraListbox = CreateListbox();
            if (s_zoomSlider == null) s_zoomSlider = CreateZoomSlider();
            if (s_rangeSlider == null) s_rangeSlider = CreateRangeSlider();

            // Insert directly after the "App" script picker so our
            // controls slot in *before* the script color controls and
            // sit visually grouped under the selected script.
            //
            // Search from the END of the list, not the front: MyTextPanel
            // has TWO copies of the App section (one from the LCD
            // component path inherited via base.CreateTerminalControls
            // before Title/Rotate are added, another from a later
            // registration). The *visible* App section is the LAST one
            // — the duplicate at the top is suppressed by SE. Multi-
            // surface blocks like MyCockpit only have one copy so
            // either direction works there.
            //
            // Anchor priority: Script → ScriptBackgroundColor →
            // ScriptForegroundColor → Content → append.
            int insertAt = -1;
            for (int i = controls.Count - 1; i >= 0; i--)
            {
                if (controls[i].Id == "Script") { insertAt = i + 1; break; }
            }
            if (insertAt < 0)
            {
                for (int i = controls.Count - 1; i >= 0; i--)
                {
                    if (controls[i].Id == "ScriptBackgroundColor") { insertAt = i + 1; break; }
                }
            }
            if (insertAt < 0)
            {
                for (int i = controls.Count - 1; i >= 0; i--)
                {
                    if (controls[i].Id == "ScriptForegroundColor") { insertAt = i + 1; break; }
                }
            }
            if (insertAt < 0)
            {
                for (int i = controls.Count - 1; i >= 0; i--)
                {
                    if (controls[i].Id == "Content") { insertAt = i + 1; break; }
                }
            }
            if (insertAt < 0) insertAt = controls.Count;
            controls.Insert(insertAt, s_cameraListbox);
            controls.Insert(insertAt + 1, s_zoomSlider);
            controls.Insert(insertAt + 2, s_rangeSlider);
        }

        private static IMyTerminalControlListbox CreateListbox()
        {
            // Single shared control. Visible/ListContent/ItemSelected all
            // dispatch to the block's currently-highlighted surface index
            // (via MyMultiTextPanelComponent.SelectedPanelIndex on multi-
            // surface blocks, surface 0 for single-surface blocks).
            var lb = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyTerminalBlock>(ControlId);
            lb.Title = MyStringId.GetOrCompute("Camera Source");
            lb.Tooltip = MyStringId.GetOrCompute(
                "Select a camera on this grid (or a mechanically connected subgrid) to display its view. " +
                "Choose Mirror to reflect the world across this screen.");
            lb.Multiselect = false;
            lb.VisibleRowsCount = 8;
            lb.Visible = b => {
                var p = b as IMyTextSurfaceProvider;
                if (p == null) return false;
                int idx = GetActiveSurfaceIndex(b);
                if (idx < 0 || idx >= p.SurfaceCount) return false;
                var surf = p.GetSurface(idx);
                return surf != null && surf.Script == CameraScriptId;
            };
            lb.ListContent = (b, items, selected) => PopulateCameras(b, GetActiveSurfaceIndex(b), items, selected);
            lb.ItemSelected = (b, sel) => OnCameraSelected(b, GetActiveSurfaceIndex(b), sel);
            return lb;
        }

        private static IMyTerminalControlSlider CreateZoomSlider()
        {
            // Only meaningful when a camera block is the source; for
            // Mirror mode the FOV is computed from screen geometry so
            // zoom doesn't apply. Visible predicate hides the slider
            // unless the Camera script is active AND a camera
            // (camId != 0) is selected.
            var sl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ControlId + ".Zoom");
            sl.Title = MyStringId.GetOrCompute("Camera Zoom");
            sl.Tooltip = MyStringId.GetOrCompute(
                "Zoom factor for the selected camera view. 1.0× is the camera's natural FOV; " +
                "higher values narrow the FOV for a telephoto effect.");
            sl.SetLimits(MinZoom, MaxZoom);
            sl.Getter = b => GetSelectedZoom(b, GetActiveSurfaceIndex(b));
            sl.Setter = (b, v) => SetSelectedZoom(b, GetActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = GetSelectedZoom(b, GetActiveSurfaceIndex(b));
                sb.Append(v.ToString("0.0", CultureInfo.InvariantCulture)).Append('×');
            };
            sl.Visible = b => {
                var p = b as IMyTextSurfaceProvider;
                if (p == null) return false;
                int idx = GetActiveSurfaceIndex(b);
                if (idx < 0 || idx >= p.SurfaceCount) return false;
                var surf = p.GetSurface(idx);
                if (surf == null || surf.Script != CameraScriptId) return false;
                return GetSelectedCameraId(b, idx) != 0L;
            };
            sl.Enabled = b => true;
            return sl;
        }

        private static IMyTerminalControlSlider CreateRangeSlider()
        {
            // Distance from the main-view camera at which the helper
            // stops rendering this panel — beyond this, the panel just
            // shows its splash. Defaults and bounds match the
            // CameraLCD-Remastered Pulsar plugin (10–500m, 40m default).
            var sl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ControlId + ".Range");
            sl.Title = MyStringId.GetOrCompute("Render Range");
            sl.Tooltip = MyStringId.GetOrCompute(
                "Stops rendering this screen when the player is farther than this from it (meters). " +
                "Lower values save GPU time at long distances when the screen is unreadable anyway.");
            sl.SetLimits(MinRange, MaxRange);
            sl.Getter = b => GetSelectedRange(b, GetActiveSurfaceIndex(b));
            sl.Setter = (b, v) => SetSelectedRange(b, GetActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = GetSelectedRange(b, GetActiveSurfaceIndex(b));
                sb.Append(((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)).Append('m');
            };
            // Visible whenever Camera View is active on the surface —
            // applies to both mirror and camera modes (long-range
            // cutoffs make sense for both).
            sl.Visible = IsCameraViewActiveOnSelectedSurface;
            sl.Enabled = b => true;
            return sl;
        }

        // Resolve the surface index the user is currently editing in the
        // terminal. For multi-surface blocks, SE tracks it in
        // MyMultiTextPanelComponent.SelectedPanelIndex (updated whenever
        // the user clicks an entry in the standard "LCD Panels" listbox).
        // Blocks owning a MyMultiTextPanelComponent implement
        // IMyMultiTextPanelComponentOwner — accessible directly because
        // the interface and the component type are both public in
        // Sandbox.Game.EntityComponents. For single-surface blocks
        // (or blocks that aren't owners), surface 0 is the only option.
        private static int GetActiveSurfaceIndex(IMyTerminalBlock block)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 1) return 0;
            var owner = block as IMyMultiTextPanelComponentOwner;
            var multi = owner?.MultiTextPanel;
            if (multi == null) return 0;
            int idx = multi.SelectedPanelIndex;
            return (idx >= 0 && idx < provider.SurfaceCount) ? idx : 0;
        }

        private static bool IsCameraViewActiveOnSelectedSurface(IMyTerminalBlock block)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null) return false;
            int idx = GetActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return false;
            var surface = provider.GetSurface(idx);
            return surface != null
                && (surface.Script == MirrorScriptId || surface.Script == CameraScriptId);
        }

        private static void PopulateCameras(IMyTerminalBlock block, int surfaceIdx, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || block.CubeGrid == null) return;

            long currentId = GetSelectedCameraId(block, surfaceIdx);

            // Gather all camera blocks on the mechanical-grid group.
            var groups = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, groups);
            var slims = new List<IMySlimBlock>();
            foreach (var g in groups)
            {
                g.GetBlocks(slims, b => b.FatBlock is IMyCameraBlock);
            }

            // No cameras → show a single disabled "(no cameras)" placeholder
            // so the listbox is never empty and the user understands why.
            if (slims.Count == 0)
            {
                var placeholder = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute("(no cameras)"),
                    MyStringId.GetOrCompute("No camera blocks on this grid or any mechanically-connected subgrid."),
                    0L);
                items.Add(placeholder);
                return;
            }

            // Default to first available when no camera is selected yet.
            // We do NOT mutate storage here — that happens on user interaction
            // (OnCameraSelected) or when CameraScript reads the effective id.
            long defaultId = (slims[0].FatBlock as IMyCameraBlock)?.EntityId ?? 0L;
            long highlight = (currentId != 0L) ? currentId : defaultId;

            foreach (var slim in slims)
            {
                var cam = slim.FatBlock as IMyCameraBlock;
                if (cam == null) continue;
                var label = string.IsNullOrEmpty(cam.CustomName) ? "Camera" : cam.CustomName;
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute(label),
                    MyStringId.GetOrCompute("Display this camera's view."),
                    cam.EntityId);
                items.Add(item);
                if (cam.EntityId == highlight) selected.Add(item);
            }
        }

        /// <summary>
        /// Returns the EFFECTIVE camera id for a surface: the stored selection
        /// if non-zero, otherwise the first available camera on the grid (and
        /// its subgrids). Used by CameraScript so a freshly-selected Camera app
        /// renders something instead of "Camera offline" when the user hasn't
        /// touched the listbox yet.
        /// </summary>
        public static long GetEffectiveCameraId(IMyEntity entity, int surfaceIdx)
        {
            long stored = GetSelectedCameraId(entity, surfaceIdx);
            if (stored != 0L) return stored;

            var block = entity as IMyCubeBlock;
            if (block == null || block.CubeGrid == null) return 0L;
            var groups = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, groups);
            var slims = new List<IMySlimBlock>();
            foreach (var g in groups)
                g.GetBlocks(slims, b => b.FatBlock is IMyCameraBlock);
            foreach (var slim in slims)
            {
                var cam = slim.FatBlock as IMyCameraBlock;
                if (cam != null) return cam.EntityId;
            }
            return 0L;
        }

        private static void OnCameraSelected(IMyTerminalBlock block, int surfaceIdx, List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || selected == null || selected.Count == 0) return;
            var picked = selected[0];
            long id = 0L;
            if (picked.UserData is long) id = (long)picked.UserData;
            SetSelectedCameraId(block, surfaceIdx, id);
            // The zoom slider's Visible predicate gates on "is a camera
            // selected for the current surface". SE only re-evaluates
            // Visible predicates when explicitly asked, so without this
            // forced redraw the slider wouldn't appear/disappear until
            // the user opened a different terminal control. UpdateVisual
            // re-runs the predicate and re-renders the control inline.
            try { s_zoomSlider?.UpdateVisual(); } catch { }
        }

        // Storage format on the entity's MyModStorageComponent[StorageGuid]:
        //   - Legacy: a single decimal long → applies only to surface 0
        //   - v2:     "0:123;1:456;..." (camId only)
        //   - v3:     "0:123*2.5;1:456;..." (camId*zoom)
        //   - v4:     "0:123*2.5*60;1:456;..." (camId*zoom*range)
        // Writes use v4. Older formats parse correctly (missing trailing
        // tokens fall through to defaults).
        struct SurfaceSettings
        {
            public long CameraId;
            public float Zoom;
            public float Range;
            public bool IsDefault => CameraId == 0L && Zoom == 1.0f && Range == DefaultRange;
        }

        static SurfaceSettings ParseEntry(string entry)
        {
            var r = new SurfaceSettings { Zoom = 1.0f, Range = DefaultRange };
            if (string.IsNullOrEmpty(entry)) return r;
            var parts = entry.Split('*');
            long camId;
            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out camId))
                r.CameraId = camId;
            if (parts.Length >= 2)
            {
                float z;
                if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    r.Zoom = ClampZoom(z);
            }
            if (parts.Length >= 3)
            {
                float rg;
                if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rg))
                    r.Range = ClampRange(rg);
            }
            return r;
        }

        static string FormatEntry(SurfaceSettings s)
        {
            // Emit suffixes only when non-default so saved strings stay
            // compact. A range token requires a zoom token (positional),
            // so when range diverges we always emit zoom too.
            var camStr = s.CameraId.ToString(CultureInfo.InvariantCulture);
            bool zoomDefault = Math.Abs(s.Zoom - 1.0f) < 0.001f;
            bool rangeDefault = Math.Abs(s.Range - DefaultRange) < 0.001f;
            if (zoomDefault && rangeDefault) return camStr;
            var zoomStr = s.Zoom.ToString("0.###", CultureInfo.InvariantCulture);
            if (rangeDefault) return camStr + "*" + zoomStr;
            return camStr + "*" + zoomStr + "*" + ((int)Math.Round(s.Range)).ToString(CultureInfo.InvariantCulture);
        }

        static float ClampZoom(float v)
        {
            if (v < MinZoom) return MinZoom;
            if (v > MaxZoom) return MaxZoom;
            return v;
        }

        static float ClampRange(float v)
        {
            if (v < MinRange) return MinRange;
            if (v > MaxRange) return MaxRange;
            return v;
        }

        static Dictionary<int, SurfaceSettings> ReadAllSettings(IMyEntity entity)
        {
            var map = new Dictionary<int, SurfaceSettings>();
            if (entity == null || entity.Storage == null) return map;
            string s;
            if (!entity.Storage.TryGetValue(StorageGuid, out s) || string.IsNullOrEmpty(s)) return map;

            if (s.IndexOf(':') < 0)
            {
                // Legacy single-long form. Surface 0 only.
                long legacy;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacy))
                    map[0] = new SurfaceSettings { CameraId = legacy, Zoom = 1.0f, Range = DefaultRange };
                return map;
            }

            foreach (var part in s.Split(';'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1) continue;
                int idx;
                if (!int.TryParse(part.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) continue;
                map[idx] = ParseEntry(part.Substring(colon + 1));
            }
            return map;
        }

        static void WriteAllSettings(IMyEntity entity, Dictionary<int, SurfaceSettings> map)
        {
            if (entity == null) return;
            // Prune defaults so a default-state surface doesn't bloat storage.
            var keys = new List<int>(map.Keys);
            foreach (var k in keys) if (map[k].IsDefault) map.Remove(k);

            if (map.Count == 0)
            {
                if (entity.Storage != null) entity.Storage.RemoveValue(StorageGuid);
                return;
            }

            if (entity.Storage == null) entity.Storage = new MyModStorageComponent();
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(':').Append(FormatEntry(kv.Value));
            }
            entity.Storage[StorageGuid] = sb.ToString();
        }

        public static long GetSelectedCameraId(IMyEntity entity, int surfaceIdx)
        {
            SurfaceSettings s;
            return ReadAllSettings(entity).TryGetValue(surfaceIdx, out s) ? s.CameraId : 0L;
        }

        public static void SetSelectedCameraId(IMyEntity entity, int surfaceIdx, long id)
        {
            var map = ReadAllSettings(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = new SurfaceSettings { Zoom = 1.0f, Range = DefaultRange };
            cur.CameraId = id;
            map[surfaceIdx] = cur;
            WriteAllSettings(entity, map);
        }

        public static float GetSelectedZoom(IMyEntity entity, int surfaceIdx)
        {
            SurfaceSettings s;
            return ReadAllSettings(entity).TryGetValue(surfaceIdx, out s) ? ClampZoom(s.Zoom) : 1.0f;
        }

        public static void SetSelectedZoom(IMyEntity entity, int surfaceIdx, float zoom)
        {
            var map = ReadAllSettings(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = new SurfaceSettings { Zoom = 1.0f, Range = DefaultRange };
            cur.Zoom = ClampZoom(zoom);
            map[surfaceIdx] = cur;
            WriteAllSettings(entity, map);
        }

        public static float GetSelectedRange(IMyEntity entity, int surfaceIdx)
        {
            SurfaceSettings s;
            return ReadAllSettings(entity).TryGetValue(surfaceIdx, out s) ? ClampRange(s.Range) : DefaultRange;
        }

        public static void SetSelectedRange(IMyEntity entity, int surfaceIdx, float range)
        {
            var map = ReadAllSettings(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = new SurfaceSettings { Zoom = 1.0f, Range = DefaultRange };
            cur.Range = ClampRange(range);
            map[surfaceIdx] = cur;
            WriteAllSettings(entity, map);
        }

        // Used by the listbox callback to dispatch by IMyTerminalBlock.
        public static long GetSelectedCameraId(IMyTerminalBlock block, int surfaceIdx)
        {
            return GetSelectedCameraId(block as IMyEntity, surfaceIdx);
        }

        public static float GetSelectedZoom(IMyTerminalBlock block, int surfaceIdx)
        {
            return GetSelectedZoom(block as IMyEntity, surfaceIdx);
        }

        public static void SetSelectedZoom(IMyTerminalBlock block, int surfaceIdx, float zoom)
        {
            SetSelectedZoom(block as IMyEntity, surfaceIdx, zoom);
        }

        public static float GetSelectedRange(IMyTerminalBlock block, int surfaceIdx)
        {
            return GetSelectedRange(block as IMyEntity, surfaceIdx);
        }

        public static void SetSelectedRange(IMyTerminalBlock block, int surfaceIdx, float range)
        {
            SetSelectedRange(block as IMyEntity, surfaceIdx, range);
        }
    }
}

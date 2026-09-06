using System.Collections.Generic;
using MirrorCameraMod.Settings;
using MirrorCameraMod.Terminal;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using IMyTextSurface       = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock         = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTerminalBlock     = Sandbox.ModAPI.IMyTerminalBlock;
using IMyCameraBlock       = Sandbox.ModAPI.IMyCameraBlock;
using IMyCockpit           = Sandbox.ModAPI.IMyCockpit;
using IMyProgrammableBlock = Sandbox.ModAPI.IMyProgrammableBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// LCD app that registers its surface as a Camera panel showing the
    /// view from a chosen camera block. All lifecycle plumbing is on
    /// <see cref="PanelTss"/>; this class supplies camera-mode
    /// registration arguments and the splash title/subtitle.
    ///
    /// <para>The source camera state is resolved via
    /// <see cref="MirrorSession"/> each sync: a missing/non-working
    /// source camera makes the registration return false (panel
    /// removed from PanelRegistry) and the splash subtitle reads
    /// "Camera offline" instead of the plugin status.</para>
    ///
    /// <para>Owns the <b>Camera Source</b> listbox, the <b>Override
    /// Camera Zoom</b> checkbox (default off; flipping it on reveals
    /// the per-screen <b>Camera View Zoom</b> slider and routes the
    /// renderer to its value instead of the camera block's own zoom),
    /// the <b>Camera View Zoom</b> slider, plus the hidden
    /// <see cref="CameraIdPropertyId"/> property exposing the selected
    /// camera's entity id to programmable-block scripts. Registered on
    /// every terminal-block type that hosts a text-surface app (text
    /// panels, cockpits, programmable blocks), gated by
    /// <see cref="IsCameraSurface"/>.
    /// <see cref="LcdAppTerminalControls"/> reorders the visible
    /// controls to land directly under the "Script" listbox and emits
    /// toolbar actions only for single-surface blocks (multi-surface
    /// blocks rely on the camera-block zoom slider instead).</para>
    /// </summary>
    [MyTextSurfaceScript(MirrorSession.CameraScriptId, "Camera")]
    public class CameraScript : PanelTss
    {
        public const string RemoteCamerasId       = "Mirror.RemoteCameras";
        public const string ListboxId             = "Mirror.Camera.List";
        public const string ZoomId                = "Mirror.Zoom";
        public const string OverrideCameraZoomId  = "Mirror.OverrideCameraZoom";
        public const string CameraIdPropertyId    = "Mirror.Camera";

        IMyCameraBlock m_cameraBlock;       // last sync's resolved camera (null when offline)
        string         m_title = "Camera";  // last sync's resolved camera CustomName

        public CameraScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title => m_title;

        protected override string Subtitle
            => m_cameraBlock == null ? "Camera offline" : base.Subtitle;

        protected override bool TryBuildRegistration(out PanelRegistration reg)
        {
            reg = default(PanelRegistration);

            // Always refresh drawing state, even when we'll fail the
            // gate — the subtitle needs m_cameraBlock / m_title
            // regardless of whether we register.
            float zoom; string title;
            var cam = ResolveCameraState(out zoom, out title);
            m_cameraBlock = cam;
            m_title       = title;

            if (!IsBlockGoodState() || cam == null) return false;

            reg = new PanelRegistration
            {
                Mode        = PanelRegistry.PanelMode.Camera,
                CameraBlock = cam as IMyCubeBlock,
                Zoom        = zoom,
            };
            return true;
        }

        /// <summary>
        /// Reads current camera selection / zoom from
        /// <see cref="MirrorSession"/>'s per-entity storage. Returns
        /// the resolved <see cref="IMyCameraBlock"/> when a camera is
        /// selected, its block is working AND it sits on this panel's
        /// construct; <c>null</c> otherwise.
        ///
        /// <para>Zoom resolution: if <c>OverrideCameraZoom</c> is true
        /// on this surface, takes the per-screen override
        /// <see cref="MirrorStorage.GetZoom"/>; otherwise (the
        /// default) takes the camera block's own zoom from
        /// <see cref="MirrorStorage.GetCameraOwnZoom"/>.</para>
        /// </summary>
        IMyCameraBlock ResolveCameraState(out float zoom, out string title)
        {
            zoom = 1f; title = "Camera";

            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            if (entity == null) return null;

            // Last name seen for the stored id, so the splash still
            // names the camera when the block can't be found at all
            // (destroyed while away, outside MP sync range).
            string storedName = MirrorStorage.GetCameraName(entity, idx);
            if (!string.IsNullOrEmpty(storedName)) title = storedName;

            long camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            if (camId == 0L) return null;

            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt))
                return null;

            var cam = camEnt as IMyCameraBlock;
            if (cam == null) return null;

            // Title as soon as the block is known, so the offline splash
            // (unpowered, disabled, undocked) still names the camera.
            // Also refresh the remembered name: a rename that happened
            // while the camera was unresolved lands here the moment it
            // resolves again. SetCameraName is a no-op when unchanged;
            // on change it re-enters SyncRegistration once via the
            // storage notify, where the second call is the no-op.
            if (!string.IsNullOrEmpty(cam.CustomName))
            {
                title = cam.CustomName;
                MirrorStorage.SetCameraName(entity, idx, cam.CustomName);
            }

            var func = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func == null || !func.IsWorking) return null;

            // The stored id stays valid after an undock / grid split, so
            // the entity lookup alone would keep streaming a camera that
            // is no longer part of this construct. Re-check every
            // resolve; the id itself is kept so a re-dock restores the
            // feed without re-selecting. With Remote Cameras on, a
            // camera off the construct is still fine while the plugin
            // reports it reachable over the antenna network — so
            // flipping the checkbox never drops a local selection, and
            // a remote camera that leaves antenna reach goes offline
            // within one Update100.
            var panelBlock = m_block as VRage.Game.ModAPI.IMyCubeBlock;
            if (panelBlock == null) return null;
            if (!IsOnSameConstruct(panelBlock.CubeGrid, cam.CubeGrid))
            {
                if (!MirrorStorage.GetRemoteCameras(entity, idx)) return null;
                if (!CameraEnumerator.IsReachableRemote(panelBlock, camId)) return null;
            }

            zoom = MirrorStorage.GetOverrideCameraZoom(entity, idx)
                ? MirrorSession.GetSelectedZoom(entity, idx)
                : MirrorStorage.GetCameraOwnZoom(camEnt);
            return cam;
        }

        /// <summary>True when both grids belong to the same logical
        /// group (rotors, pistons, wheels, connectors) — the set
        /// <see cref="CameraEnumerator"/> lists from. Compares the
        /// engine's per-group data object by reference, so no list is
        /// allocated. <c>IMyCubeGrid.IsSameConstructAs</c> is NOT used:
        /// it compares the mechanical group and would call a docked
        /// grid foreign. A grid with no links has no group object;
        /// then only the grid itself matches.</summary>
        static bool IsOnSameConstruct(VRage.Game.ModAPI.IMyCubeGrid a, VRage.Game.ModAPI.IMyCubeGrid b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            var ga = a.GetGridGroup(VRage.Game.ModAPI.GridLinkTypeEnum.Logical);
            return ga != null
                && ReferenceEquals(ga, b.GetGridGroup(VRage.Game.ModAPI.GridLinkTypeEnum.Logical));
        }

        // ── Terminal controls ─────────────────────────────────────────

        // Per-block-type registration gate. Each entry is one of the
        // text-surface-host block interfaces (IMyTextPanel,
        // IMyCockpit, IMyProgrammableBlock). Registration MUST happen
        // from a binder bound to that specific block type so it fires
        // AFTER the block's BeforeGameLogicInit has run SE's native
        // CreateTerminalControls (instance override).
        //
        // The earlier shape — registering all three types from any
        // binder's first fire — races SE's per-instance lazy init:
        // AddControl<IMyTextPanel> called BEFORE the first MyTextPanel
        // was constructed populates MyTerminalControlFactory.m_controls
        // [MyTextPanel] with an empty BlockData, then the first
        // MyTextPanel block's BeforeGameLogicInit sees
        // AreControlsCreated<MyTextPanel>()==true and SKIPS its native
        // CreateTerminalControls — Title textbox / Content combobox
        // never get added.
        //
        // MyTerminalControlFactory.EnsureControlsAreCreated reflects
        // for STATIC CreateTerminalControls only; MyTextPanel's is an
        // instance override, so threading TBlock through CreateControl
        // does NOT pre-trigger it. Per-binder registration is the only
        // race-free path that doesn't require Harmony.
        static readonly HashSet<System.Type> s_registeredBlockTypes = new HashSet<System.Type>();
        static List<IMyTerminalAction> s_actions;
        // Held so the listbox / checkbox callbacks can call
        // UpdateVisual on the slider — picking a camera or flipping
        // the checkbox changes the slider's gating predicate, but SE
        // only re-evaluates Visible on explicit UpdateVisual.
        // Any one block-type instance suffices.
        static IMyTerminalControlSlider s_zoom;

        public static void RegisterFor<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            if (!s_registeredBlockTypes.Add(typeof(TBlock))) return;
            RegisterPerSurfaceProvider<TBlock>();
            EnsureActionsBuilt();
        }

        static void EnsureActionsBuilt()
        {
            if (s_actions != null) return;
            if (s_zoom == null) return;

            // Actions are gated to single-surface blocks (see
            // LcdAppTerminalControls.OnCustomActionGetter). s_zoom may
            // be the slider from any TBlock that registered first —
            // safe because its Getter/Setter delegate to MirrorStorage
            // which takes the block parameter and is type-agnostic.
            s_actions = new List<IMyTerminalAction>();
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Increase", "Increase Camera View Zoom", "Increase",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) + 1f),
                SurfaceSettings.ZoomFormat, SurfaceSettings.ZoomUnit));
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Decrease", "Decrease Camera View Zoom", "Decrease",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) - 1f),
                SurfaceSettings.ZoomFormat, SurfaceSettings.ZoomUnit));
            s_actions.Add(BuildCycleAction("Mirror.Camera.Next",     "Next Camera",     "Increase", direction: +1));
            s_actions.Add(BuildCycleAction("Mirror.Camera.Previous", "Previous Camera", "Decrease", direction: -1));
        }

        static void RegisterPerSurfaceProvider<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var slider   = CreateZoomSlider<TBlock>();
            var checkbox = CreateOverrideCameraZoomCheckbox<TBlock>();
            var listbox  = CreateListbox<TBlock>();
            var remote   = CreateRemoteCamerasCheckbox<TBlock>();

            // Hidden long-valued property — no UI, only here so PB
            // scripts can GetValueLong / SetValueLong by id.
            var prop = MyAPIGateway.TerminalControls
                .CreateProperty<long, TBlock>(CameraIdPropertyId);
            prop.Getter = b => MirrorStorage.GetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            prop.Setter = (b, v) => MirrorStorage.SetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);

            // AddControl order is the relative order the dispatcher
            // preserves when it relocates these under the Script list.
            MyAPIGateway.TerminalControls.AddControl<TBlock>(remote);
            MyAPIGateway.TerminalControls.AddControl<TBlock>(listbox);
            MyAPIGateway.TerminalControls.AddControl<TBlock>(checkbox);
            MyAPIGateway.TerminalControls.AddControl<TBlock>(slider);
            MyAPIGateway.TerminalControls.AddControl<TBlock>(prop);

            if (s_zoom == null) s_zoom = slider;
        }

        public static IReadOnlyList<IMyTerminalAction> GetCustomActions() => s_actions;

        // Force SE's terminal to re-evaluate every control's Visible
        // predicate. UpdateVisual on a single control only refreshes
        // its displayed value, not its place in the layout — so a
        // mod-side storage write that flips a Visible result must
        // route through the block's PropertiesChanged event for the
        // slider to appear / disappear without the user closing the
        // terminal.
        //
        // RaisePropertiesChanged is not on the mod whitelist (nor is
        // Reflection). Workaround: toggle a synced terminal property
        // twice. Every Sync&lt;&gt; .Value assignment routes through
        // MyTerminalBlock's SyncType.PropertyChanged handler, which
        // calls RaisePropertiesChanged internally. ShowInToolbarConfig
        // is the cleanest carrier — it only affects whether the block
        // appears in OTHER blocks' toolbar-action picker, so the
        // user-facing flicker is invisible while the terminal is open.
        // Net state of ShowInToolbarConfig is unchanged after the
        // pair of writes; minor cost is two sync packets per click.
        static void RefreshTerminalLayout(IMyTerminalBlock block)
        {
            if (block == null) return;
            try
            {
                bool prev = block.ShowInToolbarConfig;
                block.ShowInToolbarConfig = !prev;
                block.ShowInToolbarConfig = prev;
            }
            catch { }
        }

        // Active-surface-running-Camera-script gate. Used as the
        // Visible predicate on every Camera control so they appear
        // only when the user has the Camera app selected on the
        // currently-edited surface.
        internal static bool IsCameraSurface(IMyTerminalBlock block)
            => LcdAppTerminalControls.GetActiveSurfaceScriptId(block) == MirrorSession.CameraScriptId;

        // Builds a Next-/Previous-camera toolbar action. Uses
        // ActiveSurfaceIndex(b) — correct for single-surface text
        // panels where ActiveSurfaceIndex is always 0. The dispatcher
        // already gates these actions to SurfaceCount==1.
        static IMyTerminalAction BuildCycleAction(string id, string name, string icon, int direction)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            action.Name = new System.Text.StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled = b => true;
            action.Action  = b =>
            {
                CameraEnumerator.CycleSelectedCamera(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), direction);
                RefreshTerminalLayout(b);
            };
            action.Writer = (b, sb) =>
            {
                long camId = MirrorStorage.GetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                if (camId == 0L) { sb.Append("--"); return; }
                IMyEntity ent;
                if (!MyAPIGateway.Entities.TryGetEntityById(camId, out ent)) { sb.Append("--"); return; }
                var cam = ent as IMyCameraBlock;
                if (cam == null) { sb.Append("--"); return; }
                string label = string.IsNullOrEmpty(cam.CustomName) ? "Camera" : cam.CustomName;
                const int maxChars = 10;
                if (label.Length > maxChars) sb.Append(label, 0, maxChars);
                else sb.Append(label);
            };
            return action;
        }

        static IMyTerminalControlListbox CreateListbox<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var lb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlListbox, TBlock>(ListboxId);
            lb.Title   = MyStringId.GetOrCompute("Camera Source");
            lb.Tooltip = MyStringId.GetOrCompute("Camera to display.");
            lb.Multiselect      = false;
            lb.VisibleRowsCount = 8;
            lb.Visible = IsCameraSurface;
            lb.ListContent  = (b, items, selected) =>
                CameraEnumerator.PopulateListbox(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), items, selected);
            lb.ItemSelected = (b, sel) =>
            {
                if (b == null || sel == null || sel.Count == 0) return;
                // Camera row: its id. Grid header row: the first camera
                // under it. Placeholder: 0 (clears a stale selection).
                // The layout refresh below re-populates the listbox on
                // the next frame with the stored camera highlighted, so
                // picking a header visibly moves the selection.
                object ud = sel[0].UserData;
                long id = 0L;
                if (ud is long) id = (long)ud;
                else if (ud is CameraEnumerator.GridHeader) id = ((CameraEnumerator.GridHeader)ud).FirstCameraId;
                MirrorStorage.SetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), id);
                RefreshTerminalLayout(b);
            };
            return lb;
        }

        static IMyTerminalControlCheckbox CreateRemoteCamerasCheckbox<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var cb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCheckbox, TBlock>(RemoteCamerasId);
            cb.Title   = MyStringId.GetOrCompute("Remote Cameras");
            cb.Tooltip = MyStringId.GetOrCompute(
                "List cameras reachable over the antenna network instead of this construct's cameras.");
            cb.Visible = IsCameraSurface;
            cb.Enabled = b => true;
            cb.Getter  = b => MirrorStorage.GetRemoteCameras(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            cb.Setter  = (b, v) =>
            {
                MirrorStorage.SetRemoteCameras(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
                // Re-populates the Camera Source list on the next frame.
                RefreshTerminalLayout(b);
            };
            return cb;
        }

        static IMyTerminalControlCheckbox CreateOverrideCameraZoomCheckbox<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var cb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCheckbox, TBlock>(OverrideCameraZoomId);
            cb.Title   = MyStringId.GetOrCompute("Override Camera Zoom");
            cb.Tooltip = MyStringId.GetOrCompute(
                "Override the camera block's zoom for this screen. " +
                "When selected, the Camera View Zoom slider becomes visible and its value is used at render time.");
            cb.Visible = IsCameraSurface;
            cb.Enabled = b => true;
            cb.Getter  = b => MirrorStorage.GetOverrideCameraZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            cb.Setter  = (b, v) =>
            {
                MirrorStorage.SetOverrideCameraZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
                RefreshTerminalLayout(b);
            };
            return cb;
        }

        static IMyTerminalControlSlider CreateZoomSlider<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, TBlock>(ZoomId);
            sl.Title   = MyStringId.GetOrCompute("Camera View Zoom");
            sl.Tooltip = MyStringId.GetOrCompute("Per-screen zoom override for the selected camera.");
            sl.SetLimits(SurfaceSettings.MinZoom, SurfaceSettings.MaxZoom);
            sl.Getter = b => MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => MirrorStorage.SetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString(SurfaceSettings.ZoomFormat, System.Globalization.CultureInfo.InvariantCulture)).Append(SurfaceSettings.ZoomUnit);
            };
            // Visible only when the user explicitly opted into the
            // per-screen override AND a camera is selected on a Camera-
            // app surface. Otherwise hidden; the camera block's own
            // zoom drives the render.
            sl.Visible = b =>
            {
                if (!IsCameraSurface(b)) return false;
                int idx = LcdAppTerminalControls.ActiveSurfaceIndex(b);
                if (MirrorStorage.GetCameraId(b, idx) == 0L) return false;
                if (!MirrorStorage.GetOverrideCameraZoom(b, idx)) return false;
                return true;
            };
            sl.Enabled = b => true;
            return sl;
        }
    }
}

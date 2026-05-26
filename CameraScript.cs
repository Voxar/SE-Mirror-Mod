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
        /// selected AND its block is working; <c>null</c> otherwise.
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

            long camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            if (camId == 0L) return null;

            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt))
                return null;

            var cam = camEnt as IMyCameraBlock;
            if (cam == null) return null;
            if (!string.IsNullOrEmpty(cam.CustomName)) title = cam.CustomName;

            zoom = MirrorStorage.GetOverrideCameraZoom(entity, idx)
                ? MirrorSession.GetSelectedZoom(entity, idx)
                : MirrorStorage.GetCameraOwnZoom(camEnt);

            var func = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func == null || !func.IsWorking) return null;

            return cam;
        }

        // ── Terminal controls ─────────────────────────────────────────

        static bool s_registered;
        static List<IMyTerminalAction> s_actions;
        // Held so the listbox / checkbox callbacks can call
        // UpdateVisual on the slider — picking a camera or flipping
        // the checkbox changes the slider's gating predicate, but SE
        // only re-evaluates Visible on explicit UpdateVisual.
        // Any one block-type instance suffices.
        static IMyTerminalControlSlider s_zoom;

        const string ZoomFormat = "0.0";
        const char   ZoomUnit   = '×';

        public static void RegisterTerminalControls()
        {
            if (s_registered) return;
            s_registered = true;

            // Per-block-type AddControl on every terminal-block type
            // that hosts a text-surface app. Each gets its own
            // listbox / slider / checkbox / property instance —
            // sharing a single instance across types would let the
            // ItemSelected closure refresh only one type's terminal
            // at a time.
            RegisterPerSurfaceProvider<IMyTextPanel>();
            RegisterPerSurfaceProvider<IMyCockpit>();
            RegisterPerSurfaceProvider<IMyProgrammableBlock>();

            // Actions are gated to single-surface blocks (see
            // LcdAppTerminalControls.OnCustomActionGetter) and use the
            // generic non-indexed names, since the only block class
            // emitting them is a text panel where surface index is
            // always 0.
            s_actions = new List<IMyTerminalAction>();
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Increase", "Increase Camera View Zoom", "Increase",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) + 1f),
                ZoomFormat, ZoomUnit));
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Decrease", "Decrease Camera View Zoom", "Decrease",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) - 1f),
                ZoomFormat, ZoomUnit));
            s_actions.Add(BuildCycleAction("Mirror.Camera.Next",     "Next Camera",     "Increase", direction: +1));
            s_actions.Add(BuildCycleAction("Mirror.Camera.Previous", "Previous Camera", "Decrease", direction: -1));
        }

        static void RegisterPerSurfaceProvider<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var slider   = CreateZoomSlider();
            var checkbox = CreateOverrideCameraZoomCheckbox();
            var listbox  = CreateListbox();

            // Hidden long-valued property — no UI, only here so PB
            // scripts can GetValueLong / SetValueLong by id.
            var prop = MyAPIGateway.TerminalControls
                .CreateProperty<long, TBlock>(CameraIdPropertyId);
            prop.Getter = b => MirrorStorage.GetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            prop.Setter = (b, v) => MirrorStorage.SetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);

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
        {
            if (block == null) return false;
            var provider = block as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return false;
            int idx = LcdAppTerminalControls.ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return false;
            var surf = provider.GetSurface(idx);
            if (surf == null) return false;
            if (surf.ContentType != VRage.Game.GUI.TextPanel.ContentType.SCRIPT) return false;
            return surf.Script == MirrorSession.CameraScriptId;
        }

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

        static IMyTerminalControlListbox CreateListbox()
        {
            var lb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlListbox, IMyTerminalBlock>(ListboxId);
            lb.Title   = MyStringId.GetOrCompute("Camera Source");
            lb.Tooltip = MyStringId.GetOrCompute("Camera on this grid to display.");
            lb.Multiselect      = false;
            lb.VisibleRowsCount = 8;
            lb.Visible = IsCameraSurface;
            lb.ListContent  = (b, items, selected) =>
                CameraEnumerator.PopulateListbox(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), items, selected);
            lb.ItemSelected = (b, sel) =>
            {
                if (b == null || sel == null || sel.Count == 0) return;
                long id = sel[0].UserData is long ? (long)sel[0].UserData : 0L;
                MirrorStorage.SetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), id);
                RefreshTerminalLayout(b);
            };
            return lb;
        }

        static IMyTerminalControlCheckbox CreateOverrideCameraZoomCheckbox()
        {
            var cb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlCheckbox, IMyTerminalBlock>(OverrideCameraZoomId);
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

        static IMyTerminalControlSlider CreateZoomSlider()
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ZoomId);
            sl.Title   = MyStringId.GetOrCompute("Camera View Zoom");
            sl.Tooltip = MyStringId.GetOrCompute("Per-screen zoom override for the selected camera.");
            sl.SetLimits(SurfaceSettings.MinZoom, SurfaceSettings.MaxZoom);
            sl.Getter = b => MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => MirrorStorage.SetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString(ZoomFormat, System.Globalization.CultureInfo.InvariantCulture)).Append(ZoomUnit);
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

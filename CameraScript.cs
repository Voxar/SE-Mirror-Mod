using System.Collections.Generic;
using MirrorCameraMod.Settings;
using MirrorCameraMod.Terminal;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using IMyTextSurface   = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock     = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;
using IMyCameraBlock   = Sandbox.ModAPI.IMyCameraBlock;

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
    /// <para>Owns the <b>Camera Source</b> listbox and the <b>Camera
    /// Zoom</b> slider, both registered block-level via
    /// <see cref="RegisterTerminalControls"/> and gated by
    /// <see cref="IsCameraSurface"/>. <see cref="LcdAppTerminalControls"/>
    /// reorders them to land directly under the "Script" listbox.</para>
    /// </summary>
    [MyTextSurfaceScript(MirrorSession.CameraScriptId, "Camera")]
    public class CameraScript : PanelTss
    {
        public const string ListboxId = "Mirror.Camera";
        public const string ZoomId    = "Mirror.Zoom";

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
        /// </summary>
        IMyCameraBlock ResolveCameraState(out float zoom, out string title)
        {
            zoom = 1f; title = "Camera";

            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            if (entity == null) return null;

            // Effective id (stored if set, else first camera on grid) so
            // a freshly-selected Camera app renders without the user
            // having to open the listbox and pick.
            long camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            zoom  = MirrorSession.GetSelectedZoom(entity, idx);
            if (camId == 0L) return null;

            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt))
                return null;

            var cam = camEnt as IMyCameraBlock;
            if (cam == null) return null;
            if (!string.IsNullOrEmpty(cam.CustomName)) title = cam.CustomName;

            var func = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func == null || !func.IsWorking) return null;

            return cam;
        }

        // ── Terminal controls ─────────────────────────────────────────

        static bool s_registered;
        static List<IMyTerminalAction> s_actions;
        // Held so the listbox ItemSelected callback can call
        // UpdateVisual on it — picking a camera changes the slider's
        // gating predicate, but SE only re-evaluates Visible on
        // explicit UpdateVisual.
        static IMyTerminalControlSlider s_zoom;

        const string ZoomFormat = "0.0";
        const char   ZoomUnit   = '×';

        public static void RegisterTerminalControls()
        {
            if (s_registered) return;
            s_registered = true;
            var listbox = CreateListbox();
            s_zoom = CreateZoomSlider();
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(listbox);
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(s_zoom);

            // Only zoom is action-bindable. Listbox selection is naturally
            // a one-shot UI interaction; no Increase/Decrease semantics.
            // Actions stay script-gated through LcdAppTerminalControls'
            // CustomActionGetter (vs AddAction, which has no Visible
            // predicate) so they only appear in toolbar binding UI when
            // the Camera app is the active script on the edited surface.
            s_actions = new List<IMyTerminalAction>();
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Increase", "Increase Camera Zoom", "Increase",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) + 0.5f),
                ZoomFormat, ZoomUnit));
            s_actions.Add(SliderHelpers.BuildSliderAction(
                "Mirror.Zoom.Decrease", "Decrease Camera Zoom", "Decrease",
                s_zoom, b => SliderHelpers.Clamp(s_zoom, b, s_zoom.Getter(b) - 0.5f),
                ZoomFormat, ZoomUnit));
        }

        public static IReadOnlyList<IMyTerminalAction> GetCustomActions() => s_actions;

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
                // Force the zoom slider to re-evaluate its Visible
                // predicate — without this, picking a camera doesn't
                // make the zoom slider appear until the terminal is
                // closed and reopened. SE only re-checks Visible on
                // explicit UpdateVisual or when controls list re-pulls.
                try { s_zoom?.UpdateVisual(); } catch { }
            };
            return lb;
        }

        static IMyTerminalControlSlider CreateZoomSlider()
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ZoomId);
            sl.Title   = MyStringId.GetOrCompute("Camera Zoom");
            sl.Tooltip = MyStringId.GetOrCompute("Zoom factor for the selected camera.");
            sl.SetLimits(SurfaceSettings.MinZoom, SurfaceSettings.MaxZoom);
            sl.Getter = b => MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => MirrorStorage.SetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString(ZoomFormat, System.Globalization.CultureInfo.InvariantCulture)).Append(ZoomUnit);
            };
            // CameraEnumerator.PopulateListbox auto-persists the first
            // camera on first enumeration when nothing is stored, so by
            // the time the user can see the zoom slider, GetCameraId is
            // already non-zero whenever a camera exists. Direct check
            // suffices — no need for the effective-id fallback here.
            sl.Visible = b => IsCameraSurface(b)
                           && MirrorStorage.GetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b)) != 0L;
            sl.Enabled = b => true;
            return sl;
        }
    }
}

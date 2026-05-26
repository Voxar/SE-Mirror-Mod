using System.Collections.Generic;
using System.Globalization;
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
    /// removed from PanelRegistry) and flips <see cref="m_sourceOk"/>
    /// so the splash subtitle reads "Camera offline" instead of the
    /// plugin status.</para>
    ///
    /// <para>This class also owns the <b>Camera Source</b> listbox and
    /// the <b>Camera Zoom</b> slider. <see cref="LcdAppTerminalControls"/>'s
    /// <c>CustomControlGetter</c> dispatcher calls
    /// <see cref="AppendCustomControls"/> for any block whose active
    /// surface is running this script.</para>
    /// </summary>
    [MyTextSurfaceScript(MirrorSession.CameraScriptId, "Camera")]
    public class CameraScript : PanelTss
    {
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
        //
        // Lazy-init singletons. The listbox and slider are created on
        // first AppendCustomControls call (after a surface picks our
        // script, so SE's terminal subsystem is fully up). Reused for
        // every block — Getter/Setter dispatch to the active surface
        // index via LcdAppTerminalControls.ActiveSurfaceIndex.

        static List<IMyTerminalControl> s_controls;
        static List<IMyTerminalAction>  s_actions;
        // Tracked separately so the listbox ItemSelected callback can
        // call UpdateVisual on it to re-evaluate Visible after camera
        // selection changes the slider's gating condition.
        static IMyTerminalControlSlider s_zoom;

        /// <summary>Return this script's controls (Source listbox +
        /// Zoom slider). Called by <see cref="LcdAppTerminalControls"/>'s
        /// <c>CustomControlGetter</c> dispatcher when the active surface
        /// on the queried block is running this script. The dispatcher
        /// inserts the returned controls at the right position in the
        /// terminal list (before the LCD color pickers).</summary>
        public static IReadOnlyList<IMyTerminalControl> GetCustomControls()
        {
            EnsureBuilt();
            return s_controls;
        }

        /// <summary>Return this script's toolbar actions (Increase /
        /// Decrease / Reset for the zoom slider). Called by
        /// <see cref="LcdAppTerminalControls"/>'s
        /// <c>CustomActionGetter</c> dispatcher.</summary>
        public static IReadOnlyList<IMyTerminalAction> GetCustomActions()
        {
            EnsureBuilt();
            return s_actions;
        }

        static void EnsureBuilt()
        {
            if (s_controls != null) return;
            var listbox = CreateListbox();
            s_zoom = CreateZoomSlider();
            s_controls = new List<IMyTerminalControl> { listbox, s_zoom };
            s_actions  = new List<IMyTerminalAction>();
            // Only zoom is action-bindable. Listbox selection is naturally
            // a one-shot UI interaction; no Increase/Decrease semantics.
            s_actions.Add(BuildSliderAction("Mirror.Zoom.Increase", "Increase Camera Zoom",
                "Increase", s_zoom, b => Clamp(s_zoom, b, s_zoom.Getter(b) + 0.5f)));
            s_actions.Add(BuildSliderAction("Mirror.Zoom.Decrease", "Decrease Camera Zoom",
                "Decrease", s_zoom, b => Clamp(s_zoom, b, s_zoom.Getter(b) - 0.5f)));
        }


        static IMyTerminalAction BuildSliderAction(string id, string name, string icon,
                                                   IMyTerminalControlSlider sl,
                                                   System.Func<IMyTerminalBlock, float> compute)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            action.Name = new System.Text.StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled = b => true;
            action.Action  = b => sl.Setter(b, compute(b));
            action.Writer  = (b, sb) => sb.Append(sl.Getter(b).ToString("0.0", CultureInfo.InvariantCulture)).Append('×');
            return action;
        }

        static float Clamp(IMyTerminalControlSlider sl, IMyTerminalBlock b, float v)
        {
            var prop = (Sandbox.ModAPI.Interfaces.ITerminalProperty<float>)sl;
            float min = prop.GetMinimum(b);
            float max = prop.GetMaximum(b);
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        static IMyTerminalControlListbox CreateListbox()
        {
            var lb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlListbox, IMyTerminalBlock>("Mirror.Camera");
            lb.Title   = MyStringId.GetOrCompute("Camera Source");
            lb.Tooltip = MyStringId.GetOrCompute("Camera on this grid to display.");
            lb.Multiselect      = false;
            lb.VisibleRowsCount = 8;
            lb.Visible = b => true;  // dispatcher already filtered by active script
            lb.ListContent  = (b, items, selected) =>
                Terminal.CameraEnumerator.PopulateListbox(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), items, selected);
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
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>("Mirror.Zoom");
            sl.Title   = MyStringId.GetOrCompute("Camera Zoom");
            sl.Tooltip = MyStringId.GetOrCompute("Zoom factor for the selected camera.");
            sl.SetLimits(SurfaceSettings.MinZoom, SurfaceSettings.MaxZoom);
            sl.Getter = b => MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => MirrorStorage.SetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = MirrorStorage.GetZoom(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString("0.0", CultureInfo.InvariantCulture)).Append('×');
            };
            // CameraEnumerator.PopulateListbox auto-persists the first
            // camera on first enumeration when nothing is stored, so by
            // the time the user can see the zoom slider, GetCameraId is
            // already non-zero whenever a camera exists. Direct check
            // suffices — no need for the effective-id fallback here.
            sl.Visible = b => MirrorStorage.GetCameraId(b, LcdAppTerminalControls.ActiveSurfaceIndex(b)) != 0L;
            sl.Enabled = b => true;
            return sl;
        }
    }
}

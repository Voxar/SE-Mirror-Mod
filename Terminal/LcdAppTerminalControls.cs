using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;
using IMyTerminalBlock       = Sandbox.ModAPI.IMyTerminalBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;

namespace MirrorCameraMod.Terminal
{
    /// <summary>
    /// Adds the terminal controls and toolbar actions for blocks
    /// running either of this mod's two LCD apps (Mirror, Camera):
    /// Camera Source listbox, Camera Zoom slider, Mirror Yaw / Pitch
    /// sliders, plus an Increase / Decrease / Reset toolbar action per
    /// slider. One instance of each control is shared across every
    /// block; the controls dispatch per-block via the block's
    /// currently-highlighted surface index (multi-surface blocks track
    /// this on <see cref="MyMultiTextPanelComponent"/>; single-surface
    /// blocks always resolve to surface 0).
    ///
    /// <para>Controls are inserted via
    /// <c>MyAPIGateway.TerminalControls.CustomControlGetter</c>, NOT
    /// via <c>AddControl</c>. Calling <c>AddControl</c> from
    /// <c>LoadData</c> causes <c>MyTerminalControlFactory.m_controls</c>
    /// to be populated for <c>MyTextPanel</c> before SE has lazy-
    /// initialised the LCD's standard controls. The LCD then short-
    /// circuits on <c>AreControlsCreated&lt;MyTextPanel&gt;()</c> and never
    /// adds the defaults — wiping the entire LCD terminal UI.</para>
    /// </summary>
    public sealed class LcdAppTerminalControls
    {
        const string ControlId = "Mirror.CameraSource";

        // Anchor priority for the insertion point. Controls slot in
        // immediately after the LAST occurrence of the first id in this
        // list that exists. The terminal often has a duplicate "App"
        // section at the top that's suppressed in the UI — searching
        // from the end picks the visible one.
        static readonly string[] AnchorPriority =
            { "Script", "ScriptBackgroundColor", "ScriptForegroundColor", "Content" };

        bool _hooked;

        // Single shared controls. Visible/ListContent/Getter/Setter on
        // each dispatches to the surface the user has highlighted in
        // the standard LCD Panels listbox.
        IMyTerminalControlListbox _listbox;
        IMyTerminalControlSlider  _zoomSlider;
        IMyTerminalControlSlider  _mirrorYawSlider;
        IMyTerminalControlSlider  _mirrorPitchSlider;

        public void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlGetter;
        }

        public void Unhook()
        {
            if (!_hooked) return;
            MyAPIGateway.TerminalControls.CustomControlGetter -= OnCustomControlGetter;
            _listbox = null;
            _zoomSlider = null;
            _mirrorYawSlider = null;
            _mirrorPitchSlider = null;
            _hooked = false;
        }

        // ── Insertion ──────────────────────────────────────────────────

        void OnCustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            // Any block exposing one or more text surfaces qualifies —
            // single-surface text panels, cockpits, programmable blocks,
            // custom turret controllers, etc.
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return;

            if (_listbox            == null) _listbox            = CreateListbox();
            if (_zoomSlider         == null) _zoomSlider         = CreateZoomSlider();
            if (_mirrorYawSlider    == null) _mirrorYawSlider    = CreateMirrorAngleSlider(yaw: true);
            if (_mirrorPitchSlider  == null) _mirrorPitchSlider  = CreateMirrorAngleSlider(yaw: false);

            int insertAt = FindInsertionIndex(controls);
            controls.Insert(insertAt,     _listbox);
            controls.Insert(insertAt + 1, _zoomSlider);
            controls.Insert(insertAt + 2, _mirrorYawSlider);
            controls.Insert(insertAt + 3, _mirrorPitchSlider);
        }

        static int FindInsertionIndex(List<IMyTerminalControl> controls)
        {
            foreach (var id in AnchorPriority)
            {
                for (int i = controls.Count - 1; i >= 0; i--)
                    if (controls[i].Id == id) return i + 1;
            }
            return controls.Count;
        }

        // ── Surface-index resolver ─────────────────────────────────────

        // Resolve the surface the user is currently editing. Multi-
        // surface blocks expose this via
        // MyMultiTextPanelComponent.SelectedPanelIndex (updated when
        // the user clicks an entry in the standard LCD Panels listbox);
        // blocks owning a MyMultiTextPanelComponent implement
        // IMyMultiTextPanelComponentOwner. Single-surface blocks or
        // non-owners always resolve to 0.
        static int ActiveSurfaceIndex(IMyTerminalBlock block)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 1) return 0;
            var owner = block as IMyMultiTextPanelComponentOwner;
            var multi = owner?.MultiTextPanel;
            if (multi == null) return 0;
            int idx = multi.SelectedPanelIndex;
            return (idx >= 0 && idx < provider.SurfaceCount) ? idx : 0;
        }

        // ── Control factories ──────────────────────────────────────────

        IMyTerminalControlListbox CreateListbox()
        {
            var lb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlListbox, IMyTerminalBlock>(ControlId);
            lb.Title   = MyStringId.GetOrCompute("Camera Source");
            lb.Tooltip = MyStringId.GetOrCompute(
                "Select a camera on this grid (or a mechanically connected subgrid) to display its view. " +
                "Choose Mirror to reflect the world across this screen.");
            lb.Multiselect      = false;
            lb.VisibleRowsCount = 8;
            lb.Visible = b => IsScriptActive(b, MirrorSession.CameraScriptId);
            lb.ListContent  = (b, items, selected) =>
                CameraEnumerator.PopulateListbox(b, ActiveSurfaceIndex(b), items, selected);
            lb.ItemSelected = (b, sel) => OnCameraSelected(b, ActiveSurfaceIndex(b), sel);
            return lb;
        }

        IMyTerminalControlSlider CreateZoomSlider()
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ControlId + ".Zoom");
            sl.Title   = MyStringId.GetOrCompute("Camera Zoom");
            sl.Tooltip = MyStringId.GetOrCompute(
                "Zoom factor for the selected camera view. 1.0× is the camera's natural FOV; " +
                "higher values narrow the FOV for a telephoto effect.");
            sl.SetLimits(Settings.SurfaceSettings.MinZoom, Settings.SurfaceSettings.MaxZoom);
            sl.Getter = b => Settings.MirrorStorage.GetZoom(b, ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => Settings.MirrorStorage.SetZoom(b, ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = Settings.MirrorStorage.GetZoom(b, ActiveSurfaceIndex(b));
                sb.Append(v.ToString("0.0", CultureInfo.InvariantCulture)).Append('×');
            };
            // Zoom only meaningful when a camera is selected. Mirror
            // mode FOV comes from screen geometry, not from a camera
            // block, so the slider hides itself there.
            sl.Visible = b =>
                IsScriptActive(b, MirrorSession.CameraScriptId)
                && Settings.MirrorStorage.GetCameraId(b, ActiveSurfaceIndex(b)) != 0L;
            sl.Enabled = b => true;
            RegisterSliderActions(
                ControlId + ".Zoom", "Camera Zoom", sl,
                step: 0.5f, resetValue: 1f, format: v => v.ToString("0.0", CultureInfo.InvariantCulture) + "x",
                visibleWhen: b => IsScriptActive(b, MirrorSession.CameraScriptId)
                              && Settings.MirrorStorage.GetCameraId(b, ActiveSurfaceIndex(b)) != 0L);
            return sl;
        }

        // Both mirror-angle sliders share the same shape: identical
        // limits, identical formatting, only the axis (yaw vs pitch)
        // and getter/setter target differ. One factory keeps the visible
        // diff to the two function pointers.
        IMyTerminalControlSlider CreateMirrorAngleSlider(bool yaw)
        {
            var id    = ControlId + (yaw ? ".MirrorYaw"   : ".MirrorPitch");
            var title = yaw ? "Mirror Yaw"   : "Mirror Pitch";
            var tip   = yaw
                ? "Tilt the mirror's reflection LEFT/RIGHT around the screen's vertical axis. " +
                  "Use to aim a rear-view or side-view mirror at the angle you want without re-mounting the LCD block."
                : "Tilt the mirror's reflection UP/DOWN around the screen's horizontal axis. " +
                  "Use to adjust what vertical slice of the world the mirror shows.";

            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(id);
            sl.Title   = MyStringId.GetOrCompute(title);
            sl.Tooltip = MyStringId.GetOrCompute(tip);
            // Range tracks the plugin's current MirrorMaxTiltDeg cap
            // (= PanelRegistry.MirrorMaxTiltDeg, pushed by the plugin
            // on each sync). When the plugin isn't loaded or hasn't
            // pushed a value yet, this falls back to the absolute
            // ±MaxMirrorAngleDeg default set on the registry. Per-block
            // getters because the dynamic-SetLimits overload signature
            // requires per-block functions, even though the value is
            // global.
            sl.SetLimits(
                _ => -PanelRegistry.MirrorMaxTiltDeg,
                _ => +PanelRegistry.MirrorMaxTiltDeg);

            sl.Getter = b => yaw
                ? Settings.MirrorStorage.GetMirrorAngleX(b, ActiveSurfaceIndex(b))
                : Settings.MirrorStorage.GetMirrorAngleY(b, ActiveSurfaceIndex(b));
            sl.Setter = (b, v) =>
            {
                int idx = ActiveSurfaceIndex(b);
                if (yaw) Settings.MirrorStorage.SetMirrorAngleX(b, idx, v);
                else     Settings.MirrorStorage.SetMirrorAngleY(b, idx, v);
            };
            sl.Writer = (b, sb) =>
            {
                float v = yaw
                    ? Settings.MirrorStorage.GetMirrorAngleX(b, ActiveSurfaceIndex(b))
                    : Settings.MirrorStorage.GetMirrorAngleY(b, ActiveSurfaceIndex(b));
                sb.Append(v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)).Append('°');
            };
            // Mirror angles only relevant in Mirror mode. Camera mode
            // uses the camera block's orientation directly.
            sl.Visible = b => IsScriptActive(b, MirrorSession.MirrorScriptId);
            sl.Enabled = b => true;
            RegisterSliderActions(
                id, title, sl,
                step: 5f, resetValue: 0f,
                format: v => v.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "°",
                visibleWhen: b => IsScriptActive(b, MirrorSession.MirrorScriptId));
            return sl;
        }

        // ── Toolbar actions ────────────────────────────────────────────
        //
        // Right-click reset on the slider knob would normally come from
        // the concrete MyTerminalControlSlider<TBlock>.DefaultValue
        // property in Sandbox.Game.Gui; the mod-API surface doesn't
        // expose it and MDK2 prohibits reflection paths
        // (System.Reflection.BindingFlags, PropertyInfo.SetValue, ...),
        // so the reset is exposed as a toolbar Action instead — same
        // outcome, one click further.

        // For each slider we add three toolbar-assignable actions —
        // Increase, Decrease, and Reset — wired through
        // <see cref="IMyTerminalControlSlider.Setter"/>/Getter so the
        // action path goes through the same storage write as a manual
        // slider drag. `visibleWhen` mirrors the slider's Visible
        // predicate so the actions only enable on blocks the slider
        // itself would show up on.
        void RegisterSliderActions(string baseId, string baseName, IMyTerminalControlSlider sl,
                                   float step, float resetValue,
                                   Func<float, string> format,
                                   Func<IMyTerminalBlock, bool> visibleWhen)
        {
            AddSliderAction(baseId + ".Increase", "Increase " + baseName, "Action_Increase",
                step, sl, format, visibleWhen);
            AddSliderAction(baseId + ".Decrease", "Decrease " + baseName, "Action_Decrease",
                -step, sl, format, visibleWhen);
            AddSliderResetAction(baseId + ".Reset", "Reset " + baseName, "Cancel",
                resetValue, sl, format, visibleWhen);
        }

        void AddSliderAction(string id, string name, string icon,
                             float delta, IMyTerminalControlSlider sl,
                             Func<float, string> format,
                             Func<IMyTerminalBlock, bool> visibleWhen)
        {
            var action = MyAPIGateway.TerminalControls
                .CreateAction<IMyTerminalBlock>(id);
            action.Name = new StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled        = visibleWhen;
            // GetMinimum/GetMaximum live on ITerminalProperty<float>
            // (typed on IMyCubeBlock), the slider inherits but the
            // direct invocation on IMyTerminalControlSlider doesn't
            // resolve — cast through that interface for the bounds.
            var prop = (Sandbox.ModAPI.Interfaces.ITerminalProperty<float>)sl;
            action.Action = b =>
            {
                if (!visibleWhen(b)) return;
                float current = sl.Getter(b);
                float min     = prop.GetMinimum(b);
                float max     = prop.GetMaximum(b);
                float next    = current + delta;
                if (next < min) next = min;
                if (next > max) next = max;
                sl.Setter(b, next);
            };
            action.Writer = (b, sb) =>
            {
                if (!visibleWhen(b)) return;
                sb.Append(format(sl.Getter(b)));
            };
            MyAPIGateway.TerminalControls.AddAction<IMyTerminalBlock>(action);
        }

        void AddSliderResetAction(string id, string name, string icon,
                                  float resetValue, IMyTerminalControlSlider sl,
                                  Func<float, string> format,
                                  Func<IMyTerminalBlock, bool> visibleWhen)
        {
            var action = MyAPIGateway.TerminalControls
                .CreateAction<IMyTerminalBlock>(id);
            action.Name = new StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled        = visibleWhen;
            action.Action = b =>
            {
                if (!visibleWhen(b)) return;
                sl.Setter(b, resetValue);
            };
            action.Writer = (b, sb) =>
            {
                if (!visibleWhen(b)) return;
                sb.Append(format(sl.Getter(b)));
            };
            MyAPIGateway.TerminalControls.AddAction<IMyTerminalBlock>(action);
        }

        // ── Helpers ────────────────────────────────────────────────────

        void OnCameraSelected(IMyTerminalBlock block, int surfaceIdx,
                              List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || selected == null || selected.Count == 0) return;
            var picked = selected[0];
            long id = picked.UserData is long ? (long)picked.UserData : 0L;
            Settings.MirrorStorage.SetCameraId(block, surfaceIdx, id);
            // The zoom slider's Visible predicate gates on "is a camera
            // selected". SE only re-evaluates Visible when explicitly
            // asked, so without this forced redraw the slider wouldn't
            // appear/disappear until the user opened a different
            // terminal control.
            try { _zoomSlider?.UpdateVisual(); } catch { }
        }

        static bool IsScriptActive(IMyTerminalBlock block, string scriptId)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null) return false;
            int idx = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return false;
            var surf = provider.GetSurface(idx);
            return surf != null && surf.Script == scriptId;
        }

        static bool IsAnyMirrorScriptActive(IMyTerminalBlock block)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null) return false;
            int idx = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return false;
            var surf = provider.GetSurface(idx);
            return surf != null
                && (surf.Script == MirrorSession.MirrorScriptId
                 || surf.Script == MirrorSession.CameraScriptId);
        }
    }
}

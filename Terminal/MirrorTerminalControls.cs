using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Builds and registers the three terminal controls this mod adds
    /// to LCD-capable blocks: a "Camera Source" listbox, a "Camera
    /// Zoom" slider, and a "Render Range" slider. One instance of each
    /// is shared across every block; the controls dispatch per-block
    /// via the block's currently-highlighted surface index (multi-
    /// surface blocks track this on <see cref="MyMultiTextPanelComponent"/>;
    /// single-surface blocks always resolve to surface 0).
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
    public sealed class MirrorTerminalControls
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
        IMyTerminalControlSlider  _rangeSlider;

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
            _rangeSlider = null;
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

            if (_listbox     == null) _listbox     = CreateListbox();
            if (_zoomSlider  == null) _zoomSlider  = CreateZoomSlider();
            if (_rangeSlider == null) _rangeSlider = CreateRangeSlider();

            int insertAt = FindInsertionIndex(controls);
            controls.Insert(insertAt,     _listbox);
            controls.Insert(insertAt + 1, _zoomSlider);
            controls.Insert(insertAt + 2, _rangeSlider);
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
            return sl;
        }

        IMyTerminalControlSlider CreateRangeSlider()
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(ControlId + ".Range");
            sl.Title   = MyStringId.GetOrCompute("Render Range");
            sl.Tooltip = MyStringId.GetOrCompute(
                "Stops rendering this screen when the player is farther than this from it (meters). " +
                "Lower values save GPU time at long distances when the screen is unreadable anyway.");
            sl.SetLimits(Settings.SurfaceSettings.MinRange, Settings.SurfaceSettings.MaxRange);
            sl.Getter = b => Settings.MirrorStorage.GetRange(b, ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => Settings.MirrorStorage.SetRange(b, ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = Settings.MirrorStorage.GetRange(b, ActiveSurfaceIndex(b));
                sb.Append(((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)).Append('m');
            };
            // Range applies to both Mirror and Camera scripts — both
            // benefit from a long-distance cutoff.
            sl.Visible = b => IsAnyMirrorScriptActive(b);
            sl.Enabled = b => true;
            return sl;
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

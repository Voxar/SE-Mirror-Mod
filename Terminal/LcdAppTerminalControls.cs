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
    /// Adds the terminal controls, properties, and toolbar actions for
    /// blocks running either of this mod's two LCD apps (Mirror, Camera):
    /// Camera Source listbox, Camera Zoom slider, Mirror Yaw / Pitch
    /// sliders, plus an Increase / Decrease / Reset toolbar action per
    /// slider, plus invisible programmatic properties for PB scripts and
    /// tools like Build Vision.
    ///
    /// <para><b>Registration timing.</b> Per the THDigi SE-ModScript-Examples
    /// pattern (GyroTerminalControls.cs comment: <i>"Should only be
    /// retrieved/edited/added after the block type fully spawned because
    /// of game bugs"</i>), registration runs on first
    /// <see cref="PanelTss"/> instantiation for each host block type,
    /// not at <c>LoadData</c>. By then, the block type's lazy
    /// <c>CreateTerminalControls</c> override has already populated the
    /// engine's default controls (Title, Content, Show on HUD, …) — our
    /// <see cref="MyAPIGateway.TerminalControls.AddControl{TBlock}"/>
    /// just appends instead of pre-empting the lazy init and wiping
    /// every default LCD UI control. (Calling AddControl from LoadData
    /// triggers <c>AreControlsCreated&lt;MyTextPanel&gt;</c> via
    /// <c>InitializeControls</c>, which makes the lazy init bail.)</para>
    ///
    /// <para><b>Per-type registration.</b>
    /// <see cref="MyAPIGateway.TerminalControls.AddControl{TBlock}"/>
    /// requires a specific block interface (TBlock must be assignable
    /// from <see cref="IMyTerminalBlock"/>; <see cref="IMyTextSurfaceProvider"/>
    /// is rejected because it doesn't inherit
    /// <see cref="IMyTerminalBlock"/>). Each block type that hosts our
    /// scripts (text panels, cockpits, programmable blocks, custom
    /// turret controllers, …) gets its own registration via the
    /// strongly-typed <see cref="RegisterFor{TBlock}"/>. Each type
    /// registers exactly once thanks to the per-T
    /// <c>RegistrationState&lt;TBlock&gt;.Done</c> sentinel.</para>
    ///
    /// <para><b>Per-block visibility.</b> Each control has a
    /// <c>Visible</c> predicate that gates on whether the active surface
    /// is actually running one of this mod's scripts — so a generic
    /// cockpit terminal doesn't get the mirror tilt sliders unless a
    /// surface is set to Mirror.</para>
    /// </summary>
    public static class LcdAppTerminalControls
    {
        const string IdPrefix = "Mirror.CameraSource";

        // Per-type sentinel. RegisterFor<T> uses RegistrationState<T> as
        // the closure; the boolean is per-instantiated-T thanks to .NET
        // generic type specialization. No locking needed — TSS construction
        // is main-thread.
        static class RegistrationState<TBlock> { public static bool Done; }

        /// <summary>
        /// Register every control / property / action for one block type.
        /// Idempotent — second call for the same TBlock is a no-op.
        /// Called by <see cref="PanelTss"/> on first instantiation for a
        /// host block of type TBlock (= when the user picks our mod's
        /// script on a surface of a block of that type).
        /// </summary>
        public static void RegisterFor<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            if (RegistrationState<TBlock>.Done) return;
            RegistrationState<TBlock>.Done = true;

            AddCameraSourceListbox<TBlock>();
            AddCameraZoomSlider<TBlock>();
            AddMirrorAngleSlider<TBlock>(yaw: true);
            AddMirrorAngleSlider<TBlock>(yaw: false);
        }

        // ── Surface-index resolver ─────────────────────────────────────

        // Resolve the surface the user is currently editing. Multi-
        // surface blocks expose this via
        // MyMultiTextPanelComponent.SelectedPanelIndex (updated when the
        // user clicks an entry in the standard LCD Panels listbox);
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

        // ── Script-active predicates ───────────────────────────────────

        static bool IsScriptActive(IMyTerminalBlock block, string scriptId)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null) return false;
            int idx = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return false;
            var surf = provider.GetSurface(idx);
            return surf != null && surf.Script == scriptId;
        }

        // ── Listbox: Camera Source ─────────────────────────────────────

        static void AddCameraSourceListbox<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var lb = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlListbox, TBlock>(IdPrefix);
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
            MyAPIGateway.TerminalControls.AddControl<TBlock>(lb);
        }

        static void OnCameraSelected(IMyTerminalBlock block, int surfaceIdx,
                                     List<MyTerminalControlListBoxItem> selected)
        {
            if (block == null || selected == null || selected.Count == 0) return;
            var picked = selected[0];
            long id = picked.UserData is long ? (long)picked.UserData : 0L;
            Settings.MirrorStorage.SetCameraId(block, surfaceIdx, id);
        }

        // ── Slider: Camera Zoom ────────────────────────────────────────

        static void AddCameraZoomSlider<TBlock>() where TBlock : class, IMyTerminalBlock
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, TBlock>(IdPrefix + ".Zoom");
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
            // Visible only when Camera script is active AND a camera has
            // been picked — Mirror mode doesn't use zoom.
            sl.Visible = b =>
                IsScriptActive(b, MirrorSession.CameraScriptId)
                && Settings.MirrorStorage.GetCameraId(b, ActiveSurfaceIndex(b)) != 0L;
            sl.Enabled = b => true;
            MyAPIGateway.TerminalControls.AddControl<TBlock>(sl);

            // Programmatic property + toolbar actions for the same value.
            AddSliderProperty<TBlock>(IdPrefix + ".Zoom.Value",
                getter: b => Settings.MirrorStorage.GetZoom(b, ActiveSurfaceIndex(b)),
                setter: (b, v) => Settings.MirrorStorage.SetZoom(b, ActiveSurfaceIndex(b), v));

            RegisterSliderActions<TBlock>(IdPrefix + ".Zoom", "Camera Zoom", sl,
                step: 0.5f, resetValue: 1f,
                format: v => v.ToString("0.0", CultureInfo.InvariantCulture) + "x",
                visibleWhen: b => IsScriptActive(b, MirrorSession.CameraScriptId)
                              && Settings.MirrorStorage.GetCameraId(b, ActiveSurfaceIndex(b)) != 0L);
        }

        // ── Sliders: Mirror Yaw / Mirror Pitch ─────────────────────────

        // Both mirror-angle sliders share the same shape: identical
        // limits, identical formatting, only the axis (yaw vs pitch)
        // and getter/setter target differ. One factory keeps the visible
        // diff to the two function pointers.
        static void AddMirrorAngleSlider<TBlock>(bool yaw) where TBlock : class, IMyTerminalBlock
        {
            var id    = IdPrefix + (yaw ? ".MirrorYaw"   : ".MirrorPitch");
            var title = yaw ? "Mirror Yaw"   : "Mirror Pitch";
            var tip   = yaw
                ? "Tilt the mirror's reflection LEFT/RIGHT around the screen's vertical axis. " +
                  "Use to aim a rear-view or side-view mirror at the angle you want without re-mounting the LCD block."
                : "Tilt the mirror's reflection UP/DOWN around the screen's horizontal axis. " +
                  "Use to adjust what vertical slice of the world the mirror shows.";

            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, TBlock>(id);
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
            MyAPIGateway.TerminalControls.AddControl<TBlock>(sl);

            // Programmatic property mirror — PB scripts and Build Vision
            // read/write the angle through this id.
            AddSliderProperty<TBlock>(id + ".Value",
                getter: sl.Getter,
                setter: sl.Setter);

            RegisterSliderActions<TBlock>(id, title, sl,
                step: 5f, resetValue: 0f,
                format: v => v.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "°",
                visibleWhen: b => IsScriptActive(b, MirrorSession.MirrorScriptId));
        }

        // ── Programmatic property (invisible) ──────────────────────────

        // Dedicated ITerminalProperty<float> that wraps the same getter/
        // setter as the visible slider. Slider IS a property (sliders
        // implement ITerminalProperty<float>), but registering a
        // separate property gives PB scripts a stable id distinct from
        // the visible-control id and matches the Nanobot / Build Vision
        // pattern: visible controls live in the AddControl-registered
        // type list, programmatic properties live alongside them and
        // are picked up by IMyTerminalBlock.GetProperties() / GetProperty().
        static void AddSliderProperty<TBlock>(string id,
            Func<IMyTerminalBlock, float> getter,
            Action<IMyTerminalBlock, float> setter) where TBlock : class, IMyTerminalBlock
        {
            var prop = MyAPIGateway.TerminalControls.CreateProperty<float, TBlock>(id);
            prop.SupportsMultipleBlocks = false;
            prop.Getter = getter;
            prop.Setter = setter;
            MyAPIGateway.TerminalControls.AddControl<TBlock>(prop);
        }

        // ── Toolbar actions: Increase / Decrease / Reset ───────────────
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
        static void RegisterSliderActions<TBlock>(string baseId, string baseName,
                                                  IMyTerminalControlSlider sl,
                                                  float step, float resetValue,
                                                  Func<float, string> format,
                                                  Func<IMyTerminalBlock, bool> visibleWhen)
            where TBlock : class, IMyTerminalBlock
        {
            AddSliderAction<TBlock>(baseId + ".Increase", "Increase " + baseName, "Action_Increase",
                step, sl, format, visibleWhen);
            AddSliderAction<TBlock>(baseId + ".Decrease", "Decrease " + baseName, "Action_Decrease",
                -step, sl, format, visibleWhen);
            AddSliderResetAction<TBlock>(baseId + ".Reset", "Reset " + baseName, "Cancel",
                resetValue, sl, format, visibleWhen);
        }

        static void AddSliderAction<TBlock>(string id, string name, string icon,
                                            float delta, IMyTerminalControlSlider sl,
                                            Func<float, string> format,
                                            Func<IMyTerminalBlock, bool> visibleWhen)
            where TBlock : class, IMyTerminalBlock
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<TBlock>(id);
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
            MyAPIGateway.TerminalControls.AddAction<TBlock>(action);
        }

        static void AddSliderResetAction<TBlock>(string id, string name, string icon,
                                                 float resetValue, IMyTerminalControlSlider sl,
                                                 Func<float, string> format,
                                                 Func<IMyTerminalBlock, bool> visibleWhen)
            where TBlock : class, IMyTerminalBlock
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<TBlock>(id);
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
            MyAPIGateway.TerminalControls.AddAction<TBlock>(action);
        }
    }
}

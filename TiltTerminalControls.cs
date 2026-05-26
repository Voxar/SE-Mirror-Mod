using System;
using System.Collections.Generic;
using System.Globalization;
using MirrorCameraMod.Settings;
using MirrorCameraMod.Terminal;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// Block-level Yaw / Pitch / Roll tilt sliders + actions for text
    /// panels. Affects LCD geometry independently of which script (if
    /// any) the surface is running, so it lives next to
    /// <see cref="MirrorMeshTilt{T}"/> rather than inside an LCD-app
    /// script. Visibility gated by <see cref="IsTiltEligible"/>, the
    /// shared source of truth also used by
    /// <see cref="MirrorMeshTilt{T}.ResolveAxes"/>.
    /// </summary>
    public static class TiltTerminalControls
    {
        static bool s_registered;

        public static void RegisterTerminalControls()
        {
            if (s_registered) return;
            s_registered = true;

            // Visual divider — without it the sliders sit flush against
            // FontColor/BackgroundColor and read as built-ins.
            var sep = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSeparator, IMyTextPanel>("Mirror.Separator");
            sep.Visible = IsTiltEligible;
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(sep);

            var yaw = CreateAngleSlider("Mirror.Yaw", "Yaw",
                "Tilt the screen left/right.",
                (b, idx) => MirrorStorage.GetMirrorAngleX(b, idx),
                (b, idx, v) => MirrorStorage.SetMirrorAngleX(b, idx, v));
            var pitch = CreateAngleSlider("Mirror.Pitch", "Pitch",
                "Tilt the screen up/down.",
                (b, idx) => MirrorStorage.GetMirrorAngleY(b, idx),
                (b, idx, v) => MirrorStorage.SetMirrorAngleY(b, idx, v));
            var roll = CreateAngleSlider("Mirror.Roll", "Roll",
                "Rotate the screen around its normal axis.",
                (b, idx) => MirrorStorage.GetMirrorAngleZ(b, idx),
                (b, idx, v) => MirrorStorage.SetMirrorAngleZ(b, idx, v));
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(yaw);
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(pitch);
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(roll);

            AddSliderActions("Mirror.Yaw",   "Yaw",   yaw,   step: 5f);
            AddSliderActions("Mirror.Pitch", "Pitch", pitch, step: 5f);
            AddSliderActions("Mirror.Roll",  "Roll",  roll,  step: 5f);
        }

        const string AngleFormat = "+0;-0;0";
        const char   AngleUnit   = '°';

        static void AddSliderActions(string baseId, string baseName,
                                     IMyTerminalControlSlider sl, float step)
        {
            MyAPIGateway.TerminalControls.AddAction<IMyTextPanel>(SliderHelpers.BuildSliderAction(
                baseId + ".Increase", "Increase " + baseName, "Increase",
                sl, b => SliderHelpers.Clamp(sl, b, sl.Getter(b) + step), AngleFormat, AngleUnit));
            MyAPIGateway.TerminalControls.AddAction<IMyTextPanel>(SliderHelpers.BuildSliderAction(
                baseId + ".Decrease", "Decrease " + baseName, "Decrease",
                sl, b => SliderHelpers.Clamp(sl, b, sl.Getter(b) - step), AngleFormat, AngleUnit));
        }

        static IMyTerminalControlSlider CreateAngleSlider(
            string id, string title, string tip,
            Func<IMyTerminalBlock, int, float> get,
            Action<IMyTerminalBlock, int, float> set)
        {
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(id);
            sl.Title   = MyStringId.GetOrCompute(title);
            sl.Tooltip = MyStringId.GetOrCompute(tip);
            // Per-block getters because the dynamic-SetLimits overload
            // signature requires per-block functions, even though the
            // value is the same global cap for every block.
            sl.SetLimits(
                _ => -PanelRegistry.MirrorMaxTiltDeg,
                _ => +PanelRegistry.MirrorMaxTiltDeg);
            sl.Getter = b => get(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) => set(b, LcdAppTerminalControls.ActiveSurfaceIndex(b), v);
            sl.Writer = (b, sb) =>
            {
                float v = get(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)).Append('°');
            };
            sl.Visible = IsTiltEligible;
            sl.Enabled = b => true;
            return sl;
        }

        // ── Tilt eligibility ──────────────────────────────────────────
        //
        // Mirrors the plugin's MirrorCameraPlugin.Render.ModelTiltApplier
        // IsEligibleForMeshTilt rules:
        //   1. NeverEligible blacklist (HoloLCD — projector / overlay)
        //   2. AlwaysEligible whitelist (Corner LCD top/bottom variants)
        //   3. block's local AABB depth on its narrowest axis
        //      < ThinDepthFractionOfGrid * gridSize
        // Single source of truth: MirrorMeshTilt.ResolveAxes calls this
        // to keep the slider gate and the tilt math in lockstep.

        static readonly HashSet<string> AlwaysEligibleSubtypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LargeBlockCorner_LCD_1",
                "LargeBlockCorner_LCD_2",
                "SmallBlockCorner_LCD_1",
                "SmallBlockCorner_LCD_2",
            };

        static readonly HashSet<string> NeverEligibleSubtypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "HoloLCDLarge",
                "HoloLCDSmall",
            };

        const float ThinDepthFractionOfGrid = 0.4f;

        public static bool IsTiltEligible(IMyTerminalBlock block)
        {
            if (block == null) return false;
            string subtype = block.BlockDefinition.SubtypeName;
            if (subtype != null && NeverEligibleSubtypes.Contains(subtype)) return false;
            if (subtype != null && AlwaysEligibleSubtypes.Contains(subtype)) return true;

            var cubeBlock = block as VRage.Game.ModAPI.IMyCubeBlock;
            var grid = cubeBlock?.CubeGrid;
            if (grid == null) return false;

            var aabb = block.PositionComp.LocalAABB;
            float dx = aabb.Max.X - aabb.Min.X;
            float dy = aabb.Max.Y - aabb.Min.Y;
            float dz = aabb.Max.Z - aabb.Min.Z;
            float minDepth = Math.Min(dx, Math.Min(dy, dz));

            return minDepth < grid.GridSize * ThinDepthFractionOfGrid;
        }
    }
}

using System;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace MirrorCameraMod.Terminal
{
    /// <summary>
    /// Helpers shared between any class registering slider-driven
    /// terminal actions on text-panel blocks (Camera zoom, mirror
    /// yaw/pitch/roll).
    /// </summary>
    internal static class SliderHelpers
    {
        /// <summary>Build an Increase / Decrease style toolbar action
        /// whose Action runs through the given slider's Setter (so the
        /// action path goes through the same storage write a manual
        /// drag would). <paramref name="format"/> is the .NET numeric
        /// format string used by the action's Writer (the small text
        /// overlay on the toolbar slot); <paramref name="unit"/> is
        /// appended to the formatted number.</summary>
        public static IMyTerminalAction BuildSliderAction(
            string id, string name, string icon,
            IMyTerminalControlSlider sl,
            Func<IMyTerminalBlock, float> compute,
            string format, char unit)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            action.Name = new StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled = b => true;
            action.Action  = b => sl.Setter(b, compute(b));
            action.Writer  = (b, sb) => sb.Append(sl.Getter(b).ToString(format, CultureInfo.InvariantCulture)).Append(unit);
            return action;
        }

        /// <summary>Clamp <paramref name="v"/> to the slider's
        /// per-block min/max range, going through the
        /// <c>ITerminalProperty&lt;float&gt;</c> interface so the
        /// SetLimits-provided functions are honoured.</summary>
        public static float Clamp(IMyTerminalControlSlider sl, IMyTerminalBlock b, float v)
        {
            var prop = (Sandbox.ModAPI.Interfaces.ITerminalProperty<float>)sl;
            float min = prop.GetMinimum(b);
            float max = prop.GetMaximum(b);
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}

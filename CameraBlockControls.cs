using MirrorCameraMod.Settings;
using MirrorCameraMod.Terminal;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;
using IMyCameraBlock   = Sandbox.ModAPI.IMyCameraBlock;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;
using IMyTerminalAction = Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction;

namespace MirrorCameraMod
{
    /// <summary>
    /// Terminal-controls registration for camera blocks themselves.
    /// Exposes:
    /// <list type="bullet">
    ///   <item>A visible per-camera <b>Zoom</b> slider whose range is
    ///         derived from the block definition's <c>MinFov</c> /
    ///         <c>MaxFov</c>. The value is stored per-camera in
    ///         <see cref="MirrorStorage.SetCameraOwnZoom"/> and consumed
    ///         by <see cref="CameraScript"/> when
    ///         <c>OverrideCameraZoom</c> is off for the displaying
    ///         screen (the default).</item>
    ///   <item>Toolbar Increase / Decrease Camera Zoom actions.</item>
    ///   <item>Hidden float properties for programmable-block access:
    ///         <see cref="FovPropertyId"/> (read/write FoV in radians),
    ///         <see cref="MinFovPropertyId"/> and
    ///         <see cref="MaxFovPropertyId"/> (read-only, from the block
    ///         definition). FoV and Zoom address the same underlying
    ///         <c>CameraOwnZoom</c> state via <c>fov = MaxFov / zoom</c>.</item>
    /// </list>
    /// </summary>
    public static class CameraBlockControls
    {
        public const string ZoomId            = "Mirror.CameraZoom";
        public const string FovPropertyId     = "Mirror.CameraFov";
        public const string MinFovPropertyId  = "Mirror.CameraMinFov";
        public const string MaxFovPropertyId  = "Mirror.CameraMaxFov";

        static bool s_registered;

        public static void RegisterTerminalControls()
        {
            if (s_registered) return;
            s_registered = true;

            // ── UI: per-camera Zoom slider ────────────────────────────
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyCameraBlock>(ZoomId);
            sl.Title   = MyStringId.GetOrCompute("Zoom");
            sl.Tooltip = MyStringId.GetOrCompute("Zoom for screens showing this camera.");
            sl.SetLimits(_ => 1f, MaxZoomFor);
            sl.Getter = b => MirrorStorage.GetCameraOwnZoom(b);
            sl.Setter = (b, v) =>
            {
                float max = MaxZoomFor(b);
                if (v < 1f) v = 1f;
                else if (v > max) v = max;
                MirrorStorage.SetCameraOwnZoom(b, v);
            };
            sl.Writer = (b, sb) =>
                sb.Append(MirrorStorage.GetCameraOwnZoom(b)
                    .ToString(SurfaceSettings.ZoomFormat, System.Globalization.CultureInfo.InvariantCulture))
                  .Append(SurfaceSettings.ZoomUnit);
            sl.Enabled = b => true;
            sl.Visible = b => true;
            MyAPIGateway.TerminalControls.AddControl<IMyCameraBlock>(sl);

            // ── Toolbar actions ───────────────────────────────────────
            MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(
                BuildZoomStepAction("Mirror.CameraZoom.Increase", "Increase Camera Zoom",
                    "Increase", step: +1f));
            MyAPIGateway.TerminalControls.AddAction<IMyCameraBlock>(
                BuildZoomStepAction("Mirror.CameraZoom.Decrease", "Decrease Camera Zoom",
                    "Decrease", step: -1f));

            // ── PB-accessible properties ──────────────────────────────
            // FoV addresses the same state as the Zoom slider via the
            // identity `fov = MaxFov / zoom`. Exposing both lets PB
            // authors work in whichever unit suits their math.
            var fov = MyAPIGateway.TerminalControls
                .CreateProperty<float, IMyCameraBlock>(FovPropertyId);
            fov.Getter = GetFovRadians;
            fov.Setter = SetFovRadians;
            MyAPIGateway.TerminalControls.AddControl<IMyCameraBlock>(fov);

            var minFov = MyAPIGateway.TerminalControls
                .CreateProperty<float, IMyCameraBlock>(MinFovPropertyId);
            minFov.Getter = MinFovFor;
            minFov.Setter = (b, v) => { /* read-only */ };
            MyAPIGateway.TerminalControls.AddControl<IMyCameraBlock>(minFov);

            var maxFov = MyAPIGateway.TerminalControls
                .CreateProperty<float, IMyCameraBlock>(MaxFovPropertyId);
            maxFov.Getter = MaxFovFor;
            maxFov.Setter = (b, v) => { /* read-only */ };
            MyAPIGateway.TerminalControls.AddControl<IMyCameraBlock>(maxFov);
        }

        static IMyTerminalAction BuildZoomStepAction(string id, string name, string icon, float step)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyCameraBlock>(id);
            action.Name = new System.Text.StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled = b => true;
            action.Action  = b =>
            {
                float v = MirrorStorage.GetCameraOwnZoom(b) + step;
                float max = MaxZoomFor(b);
                if (v < 1f) v = 1f;
                else if (v > max) v = max;
                MirrorStorage.SetCameraOwnZoom(b, v);
            };
            action.Writer = (b, sb) =>
                sb.Append(MirrorStorage.GetCameraOwnZoom(b)
                    .ToString(SurfaceSettings.ZoomFormat, System.Globalization.CultureInfo.InvariantCulture))
                  .Append(SurfaceSettings.ZoomUnit);
            return action;
        }

        // FoV in radians, read from the camera's stored zoom factor.
        // Falls back to MaxFov when zoom is non-positive — the storage
        // floor is 1× so that branch is defensive only.
        static float GetFovRadians(IMyTerminalBlock block)
        {
            float maxFov = MaxFovFor(block);
            float zoom = MirrorStorage.GetCameraOwnZoom(block);
            return zoom <= 0f ? maxFov : maxFov / zoom;
        }

        // Sets zoom from a desired FoV in radians. Clamps to the
        // block definition's [MinFov, MaxFov]. Writing through Zoom
        // keeps a single source of truth — the per-camera slider, the
        // Mirror.CameraFov PB property, and the LCD render path all
        // see the same value.
        static void SetFovRadians(IMyTerminalBlock block, float fov)
        {
            float min, max;
            GetFovBounds(block, out min, out max);
            if (fov < min) fov = min;
            else if (fov > max) fov = max;
            MirrorStorage.SetCameraOwnZoom(block, max / fov);
        }

        // Fallbacks chosen to keep PB-facing values harmless when a
        // camera ships with a missing or malformed definition — 0.1 rad
        // (~5.7°) for min and 1.2 rad (~68.8°) for max are SE's stock
        // camera defaults.
        const float FallbackMinFov = 0.1f;
        const float FallbackMaxFov = 1.2f;

        // Single definition lookup feeds MinFovFor / MaxFovFor / MaxZoomFor.
        static void GetFovBounds(IMyTerminalBlock block, out float min, out float max)
        {
            min = FallbackMinFov;
            max = FallbackMaxFov;
            if (block == null) return;
            MyCameraBlockDefinition def;
            if (!MyDefinitionManager.Static.TryGetDefinition<MyCameraBlockDefinition>(
                    block.BlockDefinition, out def) || def == null) return;
            if (def.MinFov > 0f) min = def.MinFov;
            if (def.MaxFov > 0f) max = def.MaxFov;
        }

        // Max zoom factor for a camera = MaxFov / MinFov from its
        // definition. Falls back to 1× if the FoV range is degenerate
        // — slider becomes a no-op rather than throwing.
        static float MaxZoomFor(IMyTerminalBlock block)
        {
            float min, max;
            GetFovBounds(block, out min, out max);
            if (min <= 0f || max <= 0f) return 1f;
            float ratio = max / min;
            return ratio < 1f ? 1f : ratio;
        }

        static float MinFovFor(IMyTerminalBlock block)
        {
            float min, max;
            GetFovBounds(block, out min, out max);
            return min;
        }

        static float MaxFovFor(IMyTerminalBlock block)
        {
            float min, max;
            GetFovBounds(block, out min, out max);
            return max;
        }
    }
}

using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using MirrorCameraMod.Terminal;

namespace MirrorCameraMod
{
    /// <summary>
    /// Session component: owns the lifetime of the per-block terminal
    /// controls this mod adds (Camera Source listbox + Camera Zoom,
    /// Mirror Yaw and Mirror Pitch sliders, plus toolbar actions for
    /// each slider). Hooks into SE's <c>CustomControlGetter</c> on
    /// <see cref="LoadData"/> and unhooks on <see cref="UnloadData"/>.
    /// All actual UI plumbing lives in <see cref="LcdAppTerminalControls"/>;
    /// per-surface state lives in <see cref="Settings.MirrorStorage"/>;
    /// camera enumeration lives in <see cref="CameraEnumerator"/>.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MirrorSession : MySessionComponentBase
    {
        public const string MirrorScriptId = "Mirror.voxar";
        public const string CameraScriptId = "Camera.voxar";

        readonly LcdAppTerminalControls _controls = new LcdAppTerminalControls();

        public override void LoadData()    => _controls.Hook();
        protected override void UnloadData() => _controls.Unhook();

        // ── Facade for non-terminal callers ─────────────────────────────

        /// <summary>Forwarder: see
        /// <see cref="CameraEnumerator.GetEffectiveCameraId"/>.</summary>
        public static long GetEffectiveCameraId(IMyEntity entity, int surfaceIdx)
            => CameraEnumerator.GetEffectiveCameraId(entity, surfaceIdx);

        /// <summary>Forwarder: see
        /// <see cref="Settings.SurfaceSettings.DefaultRange"/>.</summary>
        public static float DefaultRange => Settings.SurfaceSettings.DefaultRange;

        /// <summary>Forwarder: see
        /// <see cref="Settings.MirrorStorage.GetRange"/>.</summary>
        public static float GetSelectedRange(IMyEntity entity, int surfaceIdx)
            => Settings.MirrorStorage.GetRange(entity, surfaceIdx);

        /// <summary>Forwarder: see
        /// <see cref="Settings.MirrorStorage.GetZoom"/>.</summary>
        public static float GetSelectedZoom(IMyEntity entity, int surfaceIdx)
            => Settings.MirrorStorage.GetZoom(entity, surfaceIdx);
    }
}

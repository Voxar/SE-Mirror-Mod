using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using MirrorCameraMod.Terminal;

namespace MirrorCameraMod
{
    /// <summary>
    /// Session component: owns the lifetime of the per-block terminal
    /// controls this mod adds (Camera Source listbox + Camera Zoom and
    /// Render Range sliders). Hooks into SE's <c>CustomControlGetter</c>
    /// on <see cref="LoadData"/> and unhooks on <see cref="UnloadData"/>.
    /// All actual UI plumbing lives in <see cref="MirrorTerminalControls"/>;
    /// per-surface state lives in <see cref="Settings.MirrorStorage"/>;
    /// camera enumeration lives in <see cref="CameraEnumerator"/>.
    ///
    /// <para>Static script-id strings + facade accessors used to live
    /// here in a monolithic 540-line implementation. The remaining
    /// constants here are the script ids (referenced by the TSS
    /// attributes on <c>MirrorScript</c> / <c>CameraScript</c>) and a
    /// thin forwarder to <see cref="CameraEnumerator.GetEffectiveCameraId"/>
    /// for the camera resolver in the plugin to use through the bridge.</para>
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MirrorSession : MySessionComponentBase
    {
        public const string MirrorScriptId = "Mirror.voxar";
        public const string CameraScriptId = "Camera.voxar";

        readonly MirrorTerminalControls _controls = new MirrorTerminalControls();

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

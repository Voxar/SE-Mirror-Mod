using MirrorCameraMod.Terminal;
using VRage.Game.Components;
using VRage.ModAPI;

namespace MirrorCameraMod
{
    /// <summary>
    /// Session component: holds the script-id constants and the
    /// non-terminal facade methods other parts of the mod use to query
    /// per-surface state. Terminal-control registration is NOT done
    /// here — it lives in <see cref="Terminal.LcdAppTerminalControls"/>
    /// and is triggered from <see cref="PanelTss"/>'s constructor on
    /// the first instance per host block type. Per-surface state lives
    /// in <see cref="Settings.MirrorStorage"/>; camera enumeration
    /// lives in <see cref="CameraEnumerator"/>.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MirrorSession : MySessionComponentBase
    {
        public const string MirrorScriptId = "Mirror.voxar";
        public const string CameraScriptId = "Camera.voxar";

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

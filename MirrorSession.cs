using MirrorCameraMod.Network;
using MirrorCameraMod.Terminal;
using VRage.Game.Components;
using VRage.ModAPI;

namespace MirrorCameraMod
{
    /// <summary>
    /// Session component: owns the
    /// <see cref="LcdAppTerminalControls"/> dispatcher (subscribes to
    /// SE's <c>CustomControlGetter</c> at load, unsubscribes at unload),
    /// holds the script-id constants, and exposes facade methods other
    /// parts of the mod use to query per-surface state. Each script
    /// (<see cref="MirrorScript"/>, <see cref="CameraScript"/>) owns
    /// its own terminal controls and supplies an
    /// <c>AppendCustomControls</c> static method the dispatcher calls
    /// when that script is active on a block's surface. Per-surface
    /// state lives in <see cref="Settings.MirrorStorage"/>; camera
    /// enumeration lives in <see cref="CameraEnumerator"/>.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MirrorSession : MySessionComponentBase
    {
        public const string MirrorScriptId = "Mirror.voxar";
        public const string CameraScriptId = "Camera.voxar";

        readonly LcdAppTerminalControls _controls = new LcdAppTerminalControls();

        public override void LoadData()
        {
            _controls.Hook();
            SettingsNetwork.Register();
            // Client-only: pull the server's current state. The server
            // already has authoritative data (loaded lazily from each
            // block's storage on first access); clients can't trust the
            // local block storage because MyModStorageComponent isn't
            // synced — only the server's persistence survives.
            SettingsNetwork.RequestFullSyncIfClient();
        }

        protected override void UnloadData()
        {
            _controls.Unhook();
            SettingsNetwork.Unregister();
            // Defensive: in .NET Framework the mod assembly persists
            // across world reloads. Drop all panel + settings state so
            // session-N entity references can't bleed into session-N+1
            // if any TSS failed to Dispose.
            PanelRegistry.Clear();
            Settings.MirrorStorage.Clear();
        }

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

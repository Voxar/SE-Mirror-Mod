using ProtoBuf;

namespace MirrorCameraMod.Settings
{
    /// <summary>
    /// Per-surface settings — camera selection, zoom, render range, and
    /// the per-LCD mirror yaw/pitch. The in-memory authoritative copy
    /// lives in <see cref="MirrorStorage"/>; replicated across MP by
    /// <see cref="Network.SettingsNetwork"/>; persisted server-side
    /// via per-block <see cref="VRage.Game.Components.MyModStorageComponent"/>.
    ///
    /// <para>ProtoContract: new fields get new <c>ProtoMember</c> tags
    /// — older saves still parse, the new field reads default. Don't
    /// re-number existing tags, don't change a tag's type. That's the
    /// versioning story; do not add a parallel string/manual scheme.</para>
    /// </summary>
    [ProtoContract]
    public class SurfaceSettings
    {
        // ── Public constants (unchanged) ────────────────────────────────
        // Range/zoom limits and the default range. Consumers (sliders,
        // camera resolver) read these rather than redefining magic numbers.

        public const float MinZoom      = 1.0f;
        public const float MaxZoom      = 20.0f;

        public const float MinRange     = 10f;
        public const float MaxRange     = 500f;
        public const float DefaultRange = 40f;

        public const float MinMirrorAngleDeg = -45f;
        public const float MaxMirrorAngleDeg = +45f;

        // Shared display format for any zoom slider / action / writer.
        // Keeps the LCD-side view zoom and the per-camera zoom rendered
        // identically — same digit count, same "×" suffix everywhere.
        public const string ZoomFormat = "0.0";
        public const char   ZoomUnit   = '×';

        // ── Wire fields ─────────────────────────────────────────────────

        [ProtoMember(1)] public long  CameraId;
        [ProtoMember(2)] public float Zoom            = 1.0f;
        [ProtoMember(3)] public float Range           = DefaultRange;
        [ProtoMember(4)] public float MirrorAngleDegX;   // yaw   — tilt around screen Up
        [ProtoMember(5)] public float MirrorAngleDegY;   // pitch — tilt around screen Right
        [ProtoMember(6)] public float MirrorAngleDegZ;   // roll  — tilt around screen Normal
        // Per-screen override flag. False (the default) routes the
        // renderer to the camera block's CameraOwnZoom and hides the
        // per-screen Zoom slider. True surfaces the per-screen Zoom
        // slider and uses its value at render time, overriding the
        // camera block's setting just for this screen.
        [ProtoMember(7)] public bool  OverrideCameraZoom = false;
        // Stored on a camera block's surface-0 entry. Independent of
        // Zoom (different clamp range — Zoom is 1..20 for LCD view
        // override, CameraOwnZoom maps to the camera definition's
        // MaxFov/MinFov ratio which can be much larger).
        [ProtoMember(8)] public float CameraOwnZoom    = 1.0f;
        // Last CustomName seen on the CameraId block, written whenever
        // the camera resolves with a different name. Splash title
        // fallback for when the block can't be found (destroyed, not in
        // sync range). Cleared when CameraId changes. Null = unknown.
        [ProtoMember(9)] public string CameraName;
        // Camera Source list shows constructs reachable over the antenna
        // network instead of this construct's cameras. The selection is
        // kept either way; see CameraScript.ResolveCameraState for how
        // it gates validity.
        [ProtoMember(10)] public bool RemoteCameras = false;

        // ── Clamp helpers ───────────────────────────────────────────────

        public static float ClampZoom(float v)
            => v < MinZoom ? MinZoom : (v > MaxZoom ? MaxZoom : v);

        public static float ClampRange(float v)
            => v < MinRange ? MinRange : (v > MaxRange ? MaxRange : v);

        public static float ClampMirrorAngle(float v)
            => v < MinMirrorAngleDeg ? MinMirrorAngleDeg
             : (v > MaxMirrorAngleDeg ? MaxMirrorAngleDeg : v);

        public SurfaceSettings Clone()
            => new SurfaceSettings
            {
                CameraId        = CameraId,
                Zoom            = Zoom,
                Range           = Range,
                MirrorAngleDegX = MirrorAngleDegX,
                MirrorAngleDegY = MirrorAngleDegY,
                MirrorAngleDegZ = MirrorAngleDegZ,
                OverrideCameraZoom = OverrideCameraZoom,
                CameraOwnZoom   = CameraOwnZoom,
                CameraName      = CameraName,
                RemoteCameras   = RemoteCameras,
            };
    }
}

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

        // ── Wire fields ─────────────────────────────────────────────────

        [ProtoMember(1)] public long  CameraId;
        [ProtoMember(2)] public float Zoom            = 1.0f;
        [ProtoMember(3)] public float Range           = DefaultRange;
        [ProtoMember(4)] public float MirrorAngleDegX;   // yaw   — tilt around screen Up
        [ProtoMember(5)] public float MirrorAngleDegY;   // pitch — tilt around screen Right
        [ProtoMember(6)] public float MirrorAngleDegZ;   // roll  — tilt around screen Normal

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
            };
    }
}

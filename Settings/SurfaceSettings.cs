using System;
using System.Globalization;

namespace MirrorCameraMod.Settings
{
    /// <summary>
    /// Per-surface camera/zoom/range selection that's persisted on the
    /// owning entity's <see cref="VRage.Game.Components.MyModStorageComponent"/>.
    /// Pure data: the struct + its <see cref="Parse"/> / <see cref="Format"/>
    /// pair fully describe the wire format. No SE API references — easy
    /// to read and to unit-test if we ever want to.
    ///
    /// <para>Range/zoom limits and the default range are the public
    /// constants below; consumers (sliders, the camera resolver) read
    /// them rather than redefining magic numbers.</para>
    /// </summary>
    public struct SurfaceSettings
    {
        // Camera zoom range — slider goes 1.0× (no zoom) up to MaxZoom.
        // FOV is divided by zoom: at 20× the FOV is one-twentieth of
        // the configured camera FOV, giving a very strong telephoto.
        public const float MinZoom = 1.0f;
        public const float MaxZoom = 20.0f;

        // Per-surface render-range slider. Helper skips the panel render
        // when the main-view camera is farther than this from the LCD.
        // Matches the bounds + default the CameraLCD-Remastered plugin
        // uses (10..500m, default 40m), which is the proven setting
        // across the community.
        public const float MinRange     = 10f;
        public const float MaxRange     = 500f;
        public const float DefaultRange = 40f;

        // Mirror-only: per-surface yaw / pitch tilt applied to the
        // screen plane's normal before reflection. Lets the player aim a
        // rear-view / side-view mirror to see what they need without
        // re-mounting the LCD block. Range chosen wide enough to cover
        // typical vehicle-mirror angles without inviting the player to
        // point the mirror at the wall behind it.
        public const float MinMirrorAngleDeg = -45f;
        public const float MaxMirrorAngleDeg = +45f;

        public long  CameraId;
        public float Zoom;
        public float Range;
        public float MirrorAngleDegX;   // yaw   — tilt around screen Up
        public float MirrorAngleDegY;   // pitch — tilt around screen Right

        /// <summary>True when this surface holds the implicit default
        /// state. Storage prunes default-state surfaces so an unused
        /// surface doesn't bloat the serialized blob.</summary>
        public bool IsDefault
            => CameraId == 0L
            && Math.Abs(Zoom  - 1.0f)         < 0.001f
            && Math.Abs(Range - DefaultRange) < 0.001f
            && Math.Abs(MirrorAngleDegX)      < 0.01f
            && Math.Abs(MirrorAngleDegY)      < 0.01f;

        public static SurfaceSettings Defaults =>
            new SurfaceSettings { CameraId = 0L, Zoom = 1.0f, Range = DefaultRange,
                                  MirrorAngleDegX = 0f, MirrorAngleDegY = 0f };

        public static float ClampZoom(float v)
            => v < MinZoom ? MinZoom : (v > MaxZoom ? MaxZoom : v);

        public static float ClampRange(float v)
            => v < MinRange ? MinRange : (v > MaxRange ? MaxRange : v);

        public static float ClampMirrorAngle(float v)
            => v < MinMirrorAngleDeg ? MinMirrorAngleDeg
             : (v > MaxMirrorAngleDeg ? MaxMirrorAngleDeg : v);

        /// <summary>
        /// Parse one surface entry: <c>camId[*zoom[*range[*angX[*angY]]]]</c>.
        /// Tokens are positional and optional from right to left. Missing
        /// trailing tokens use the defaults so older format versions
        /// (angle-less, range-less, zoom-less) still load cleanly.
        /// </summary>
        public static SurfaceSettings Parse(string entry)
        {
            var r = Defaults;
            if (string.IsNullOrEmpty(entry)) return r;

            var parts = entry.Split('*');
            long camId;
            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out camId))
                r.CameraId = camId;
            if (parts.Length >= 2)
            {
                float z;
                if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    r.Zoom = ClampZoom(z);
            }
            if (parts.Length >= 3)
            {
                float rg;
                if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rg))
                    r.Range = ClampRange(rg);
            }
            if (parts.Length >= 4)
            {
                float ax;
                if (float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out ax))
                    r.MirrorAngleDegX = ClampMirrorAngle(ax);
            }
            if (parts.Length >= 5)
            {
                float ay;
                if (float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out ay))
                    r.MirrorAngleDegY = ClampMirrorAngle(ay);
            }
            return r;
        }

        /// <summary>
        /// Serialize a surface entry. Omits trailing tokens whose value
        /// matches the default so the blob stays compact when only the
        /// camera id is non-default. Tokens are positional, so an angle
        /// token requires every preceding token (zoom, range) to also be
        /// emitted; we re-emit defaults for those in that case.
        /// </summary>
        public string Format()
        {
            var camStr = CameraId.ToString(CultureInfo.InvariantCulture);
            bool zoomDefault   = Math.Abs(Zoom  - 1.0f)         < 0.001f;
            bool rangeDefault  = Math.Abs(Range - DefaultRange) < 0.001f;
            bool angleXDefault = Math.Abs(MirrorAngleDegX)      < 0.01f;
            bool angleYDefault = Math.Abs(MirrorAngleDegY)      < 0.01f;

            if (zoomDefault && rangeDefault && angleXDefault && angleYDefault) return camStr;

            var zoomStr  = Zoom.ToString("0.###", CultureInfo.InvariantCulture);
            if (rangeDefault && angleXDefault && angleYDefault)
                return camStr + "*" + zoomStr;

            var rangeStr = ((int)Math.Round(Range)).ToString(CultureInfo.InvariantCulture);
            if (angleXDefault && angleYDefault)
                return camStr + "*" + zoomStr + "*" + rangeStr;

            var angleXStr = MirrorAngleDegX.ToString("0.#", CultureInfo.InvariantCulture);
            if (angleYDefault)
                return camStr + "*" + zoomStr + "*" + rangeStr + "*" + angleXStr;

            var angleYStr = MirrorAngleDegY.ToString("0.#", CultureInfo.InvariantCulture);
            return camStr + "*" + zoomStr + "*" + rangeStr + "*" + angleXStr + "*" + angleYStr;
        }
    }
}

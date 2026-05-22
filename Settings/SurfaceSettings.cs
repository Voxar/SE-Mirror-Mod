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
        // FOV is divided by zoom: at 8× the FOV is one-eighth of the
        // configured camera FOV, giving a strong telephoto.
        public const float MinZoom = 1.0f;
        public const float MaxZoom = 15.0f;

        // Per-surface render-range slider. Helper skips the panel render
        // when the main-view camera is farther than this from the LCD.
        // Matches the bounds + default the CameraLCD-Remastered plugin
        // uses (10..500m, default 40m), which is the proven setting
        // across the community.
        public const float MinRange     = 10f;
        public const float MaxRange     = 500f;
        public const float DefaultRange = 40f;

        public long  CameraId;
        public float Zoom;
        public float Range;

        /// <summary>True when this surface holds the implicit default
        /// state. Storage prunes default-state surfaces so an unused
        /// surface doesn't bloat the serialized blob.</summary>
        public bool IsDefault
            => CameraId == 0L
            && Math.Abs(Zoom - 1.0f) < 0.001f
            && Math.Abs(Range - DefaultRange) < 0.001f;

        public static SurfaceSettings Defaults =>
            new SurfaceSettings { CameraId = 0L, Zoom = 1.0f, Range = DefaultRange };

        public static float ClampZoom(float v)
            => v < MinZoom ? MinZoom : (v > MaxZoom ? MaxZoom : v);

        public static float ClampRange(float v)
            => v < MinRange ? MinRange : (v > MaxRange ? MaxRange : v);

        /// <summary>
        /// Parse one surface entry: <c>camId[*zoom[*range]]</c>. Tokens
        /// are positional and optional from right to left. Missing
        /// trailing tokens use the defaults so older format versions
        /// (zoom-less or range-less) still load cleanly.
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
            return r;
        }

        /// <summary>
        /// Serialize a surface entry. Omits trailing tokens whose value
        /// matches the default so the blob stays compact when only the
        /// camera id is non-default. A range token requires a preceding
        /// zoom token (positional), so when range diverges we always
        /// emit zoom too.
        /// </summary>
        public string Format()
        {
            var camStr = CameraId.ToString(CultureInfo.InvariantCulture);
            bool zoomDefault  = Math.Abs(Zoom  - 1.0f)         < 0.001f;
            bool rangeDefault = Math.Abs(Range - DefaultRange) < 0.001f;
            if (zoomDefault && rangeDefault) return camStr;
            var zoomStr = Zoom.ToString("0.###", CultureInfo.InvariantCulture);
            if (rangeDefault) return camStr + "*" + zoomStr;
            return camStr + "*" + zoomStr
                + "*" + ((int)Math.Round(Range)).ToString(CultureInfo.InvariantCulture);
        }
    }
}

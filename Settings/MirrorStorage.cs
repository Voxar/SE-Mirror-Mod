using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.EntityComponents;
using VRage.ModAPI;

namespace MirrorCameraMod.Settings
{
    /// <summary>
    /// Reads and writes per-surface <see cref="SurfaceSettings"/> on an
    /// entity's <see cref="MyModStorageComponent"/>. The serialized blob
    /// lives under a single GUID keyed off <see cref="StorageGuid"/> —
    /// matches the entry in <c>Content/Data/EntityComponents.sbc</c>
    /// (the two MUST stay in sync, or per-panel selections silently
    /// fail to persist across save/reload).
    ///
    /// <para><b>Wire format</b> (semicolon-separated surfaces, colon-
    /// separated index:entry):</para>
    /// <list type="bullet">
    ///   <item>Legacy v1: a single decimal long → applies only to surface 0.</item>
    ///   <item>v2: <c>"0:123;1:456;..."</c> (camId only).</item>
    ///   <item>v3: <c>"0:123*2.5;1:456;..."</c> (camId*zoom).</item>
    ///   <item>v4 (current): <c>"0:123*2.5*60;1:456;..."</c> (camId*zoom*range).</item>
    /// </list>
    /// <para>Writes always use v4. Older formats parse correctly because
    /// missing trailing tokens fall through to <see cref="SurfaceSettings.Defaults"/>.</para>
    /// </summary>
    public static class MirrorStorage
    {
        /// <summary>GUID for the storage entry. MUST match the
        /// <c>EntityComponents.sbc</c> definition so SE serializes the
        /// component to the save file. Changing this orphans every
        /// pre-existing player's saved selections.</summary>
        public static readonly Guid StorageGuid =
            new Guid("63e4c22f-37b6-4c26-a486-6abd634fc504");

        // ── Per-surface accessors ───────────────────────────────────────

        public static long GetCameraId(IMyEntity entity, int surfaceIdx)
            => GetEntry(entity, surfaceIdx).CameraId;

        public static void SetCameraId(IMyEntity entity, int surfaceIdx, long id)
        {
            var map = ReadAll(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = SurfaceSettings.Defaults;
            cur.CameraId = id;
            map[surfaceIdx] = cur;
            WriteAll(entity, map);
        }

        public static float GetZoom(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampZoom(GetEntry(entity, surfaceIdx).Zoom);

        public static void SetZoom(IMyEntity entity, int surfaceIdx, float zoom)
        {
            var map = ReadAll(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = SurfaceSettings.Defaults;
            cur.Zoom = SurfaceSettings.ClampZoom(zoom);
            map[surfaceIdx] = cur;
            WriteAll(entity, map);
        }

        public static float GetRange(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampRange(GetEntry(entity, surfaceIdx).Range);

        public static void SetRange(IMyEntity entity, int surfaceIdx, float range)
        {
            var map = ReadAll(entity);
            SurfaceSettings cur;
            if (!map.TryGetValue(surfaceIdx, out cur)) cur = SurfaceSettings.Defaults;
            cur.Range = SurfaceSettings.ClampRange(range);
            map[surfaceIdx] = cur;
            WriteAll(entity, map);
        }

        // ── Read / write the whole blob ─────────────────────────────────

        static SurfaceSettings GetEntry(IMyEntity entity, int surfaceIdx)
        {
            SurfaceSettings s;
            return ReadAll(entity).TryGetValue(surfaceIdx, out s) ? s : SurfaceSettings.Defaults;
        }

        static Dictionary<int, SurfaceSettings> ReadAll(IMyEntity entity)
        {
            var map = new Dictionary<int, SurfaceSettings>();
            if (entity == null || entity.Storage == null) return map;

            string blob;
            if (!entity.Storage.TryGetValue(StorageGuid, out blob) || string.IsNullOrEmpty(blob))
                return map;

            if (blob.IndexOf(':') < 0)
            {
                // Legacy v1: single decimal long, surface 0 only.
                long legacy;
                if (long.TryParse(blob, NumberStyles.Integer, CultureInfo.InvariantCulture, out legacy))
                {
                    var d = SurfaceSettings.Defaults;
                    d.CameraId = legacy;
                    map[0] = d;
                }
                return map;
            }

            foreach (var part in blob.Split(';'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1) continue;
                int idx;
                if (!int.TryParse(part.Substring(0, colon),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                    continue;
                map[idx] = SurfaceSettings.Parse(part.Substring(colon + 1));
            }
            return map;
        }

        static void WriteAll(IMyEntity entity, Dictionary<int, SurfaceSettings> map)
        {
            if (entity == null) return;

            // Prune default surfaces so the blob shrinks back to nothing
            // when the user resets a panel by hand.
            var keys = new List<int>(map.Keys);
            foreach (var k in keys) if (map[k].IsDefault) map.Remove(k);

            if (map.Count == 0)
            {
                if (entity.Storage != null) entity.Storage.RemoveValue(StorageGuid);
                return;
            }

            if (entity.Storage == null) entity.Storage = new MyModStorageComponent();
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture))
                  .Append(':').Append(kv.Value.Format());
            }
            entity.Storage[StorageGuid] = sb.ToString();
        }
    }
}

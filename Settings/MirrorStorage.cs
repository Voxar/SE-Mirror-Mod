using System;
using System.Collections.Generic;
using MirrorCameraMod.Network;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace MirrorCameraMod.Settings
{
    /// <summary>
    /// Per-surface settings store. In-memory authoritative dict on the
    /// server; client mirrors populated via <see cref="SettingsNetwork"/>.
    /// Persistence is server-side: ProtoBuf-binary blob (base64-encoded)
    /// in each block's <see cref="MyModStorageComponent"/> under
    /// <see cref="StorageGuid"/>, so settings travel with blueprints and
    /// survive save/reload.
    ///
    /// <para><b>Why not just <see cref="MyModStorageComponent"/>?</b>
    /// The storage component itself is documented "not synced" — writes
    /// on one client never reach others. The whole reason the mod is
    /// separate from the plugin is that the mod IS the sync layer. This
    /// class uses the storage component for PERSISTENCE only, on the
    /// server, and uses <see cref="SettingsNetwork"/> for replication.</para>
    ///
    /// <para><b>API surface (unchanged)</b>: Get* returns the current
    /// value (clamp-safe), Set* updates the in-memory state and pushes
    /// the change to the server (or broadcasts if we ARE the server).
    /// Each Set also writes through to MyModStorageComponent on the
    /// server, and notifies the local <see cref="PanelTss"/> so the
    /// plugin sees the new value immediately on this client.</para>
    /// </summary>
    public static class MirrorStorage
    {
        /// <summary>GUID for the persistence entry. MUST match the
        /// <c>EntityComponents.sbc</c> definition so SE serialises the
        /// component to the save file. Changing this orphans every
        /// pre-existing player's saved selections.</summary>
        public static readonly Guid StorageGuid =
            new Guid("63e4c22f-37b6-4c26-a486-6abd634fc504");

        // In-memory state. Server: authoritative. Client: mirror,
        // populated by SettingsNetwork (full-sync on join + per-edit
        // updates from server). Key: (blockId << 4) | (surfaceIdx & 0xF).
        static readonly Dictionary<long, SurfaceSettings> s_state =
            new Dictionary<long, SurfaceSettings>();

        // s_stateLock guards every read AND write of s_state. World
        // load fans entity init across worker threads, and
        // MirrorSession.OnEntityAdded -> TryLoadEntity -> s_state[...]
        // = ... fires on those workers. Without this lock, two concurrent
        // writes can corrupt the Dictionary mid-resize and surface as
        // a TargetInvocationException that SE reports as "world is
        // corrupted." Same pattern as PanelTss.s_byKey / s_byLock.
        static readonly object s_stateLock = new object();

        /// <summary>Packed (blockId, surfaceIdx) dictionary key shared
        /// across <see cref="MirrorStorage"/>, <see cref="PanelTss"/>,
        /// and <see cref="Network.SettingsNetwork"/>. surfaceIdx never
        /// exceeds 15 on any vanilla LCD; packing into one long avoids
        /// the per-lookup tuple allocation a Dictionary&lt;(long,int)&gt;
        /// would force.</summary>
        internal static long MakeKey(long blockId, int surfaceIdx)
            => (blockId << 4) | (long)(surfaceIdx & 0xF);

        // ── Read API ────────────────────────────────────────────────────

        public static long  GetCameraId(IMyEntity entity, int surfaceIdx)
            => Get(entity, surfaceIdx).CameraId;

        public static float GetZoom(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampZoom(Get(entity, surfaceIdx).Zoom);

        public static float GetRange(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampRange(Get(entity, surfaceIdx).Range);

        public static float GetMirrorAngleX(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampMirrorAngle(Get(entity, surfaceIdx).MirrorAngleDegX);

        public static float GetMirrorAngleY(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampMirrorAngle(Get(entity, surfaceIdx).MirrorAngleDegY);

        public static float GetMirrorAngleZ(IMyEntity entity, int surfaceIdx)
            => SurfaceSettings.ClampMirrorAngle(Get(entity, surfaceIdx).MirrorAngleDegZ);

        public static bool  GetOverrideCameraZoom(IMyEntity entity, int surfaceIdx)
            => Get(entity, surfaceIdx).OverrideCameraZoom;

        /// <summary>Last name seen on the selected camera; null when
        /// unknown. See <see cref="SurfaceSettings.CameraName"/>.</summary>
        public static string GetCameraName(IMyEntity entity, int surfaceIdx)
            => Get(entity, surfaceIdx).CameraName;

        /// <summary>Per-camera zoom factor (1× = camera's MaxFov, no
        /// zoom; higher values narrow the FoV toward MinFov). Always
        /// stored on the camera block's surface-0 entry — the camera
        /// has no surfaces, so 0 is just a stable slot.</summary>
        public static float GetCameraOwnZoom(IMyEntity cameraEntity)
        {
            float v = Get(cameraEntity, 0).CameraOwnZoom;
            return v < 1f ? 1f : v;   // ground at 1×; no upper cap (per-camera derived)
        }

        /// <summary>Returns the current settings for the given surface.
        /// Never null — defaults are returned if no entry exists.
        /// Server first-touch checks the entity's storage component
        /// (lazy load), so blocks loaded from a save populate on demand.</summary>
        static SurfaceSettings Get(IMyEntity entity, int surfaceIdx)
        {
            if (entity == null) return new SurfaceSettings();
            long key = MakeKey(entity.EntityId, surfaceIdx);

            SurfaceSettings s;
            lock (s_stateLock)
            {
                if (s_state.TryGetValue(key, out s)) return s;
            }

            // Server: lazy-load from per-block storage if this is the
            // first read after a save. After this all surfaces on the
            // block are cached; subsequent reads hit the dict directly.
            if (IsServer && TryLoadAllSurfacesFromStorage(entity))
            {
                lock (s_stateLock)
                {
                    if (s_state.TryGetValue(key, out s)) return s;
                }
            }
            return new SurfaceSettings();
        }

        // ── Write API ───────────────────────────────────────────────────
        //
        // Each Set* updates the local in-memory copy, then triggers the
        // network push (server broadcasts; client→server→broadcast).
        // Server also writes through to MyModStorageComponent so changes
        // persist with the save. PanelTss.NotifyStorageChanged fires
        // locally so the plugin reading via PanelRegistry sees the new
        // value within one sim tick — no waiting for the network round
        // trip on the editing client.

        public static void SetCameraId(IMyEntity entity, int surfaceIdx, long id)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            if (cur.CameraId == id) return;
            cur.CameraId = id;
            // The remembered name belongs to the previous camera; drop
            // it so a selection that never resolves can't show it.
            cur.CameraName = null;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        /// <summary>Remember the selected camera's current name. Called
        /// on every successful resolve; equality-skip keeps steady state
        /// write-free, so only renames reach storage and the network.</summary>
        public static void SetCameraName(IMyEntity entity, int surfaceIdx, string name)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            if (cur.CameraName == name) return;
            cur.CameraName = name;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetZoom(IMyEntity entity, int surfaceIdx, float zoom)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            float clamped = SurfaceSettings.ClampZoom(zoom);
            if (cur.Zoom == clamped) return;
            cur.Zoom = clamped;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetRange(IMyEntity entity, int surfaceIdx, float range)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            float clamped = SurfaceSettings.ClampRange(range);
            if (cur.Range == clamped) return;
            cur.Range = clamped;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetMirrorAngleX(IMyEntity entity, int surfaceIdx, float deg)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            float clamped = SurfaceSettings.ClampMirrorAngle(deg);
            if (cur.MirrorAngleDegX == clamped) return;
            cur.MirrorAngleDegX = clamped;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetMirrorAngleY(IMyEntity entity, int surfaceIdx, float deg)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            float clamped = SurfaceSettings.ClampMirrorAngle(deg);
            if (cur.MirrorAngleDegY == clamped) return;
            cur.MirrorAngleDegY = clamped;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetMirrorAngleZ(IMyEntity entity, int surfaceIdx, float deg)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            float clamped = SurfaceSettings.ClampMirrorAngle(deg);
            if (cur.MirrorAngleDegZ == clamped) return;
            cur.MirrorAngleDegZ = clamped;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetOverrideCameraZoom(IMyEntity entity, int surfaceIdx, bool v)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            if (cur.OverrideCameraZoom == v) return;
            cur.OverrideCameraZoom = v;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetCameraOwnZoom(IMyEntity cameraEntity, float zoom)
        {
            var cur = TakeForMutation(cameraEntity, 0);
            if (cur == null) return;
            // No upper clamp here — the per-block slider's SetLimits
            // already caps it to the camera definition's MaxFov/MinFov
            // ratio. Ground at 1× so the value can't underflow.
            float clamped = zoom < 1f ? 1f : zoom;
            if (cur.CameraOwnZoom == clamped) return;
            cur.CameraOwnZoom = clamped;
            CommitAndPublish(cameraEntity, 0, cur);
        }

        /// <summary>Fetch (or create) the SurfaceSettings instance for a
        /// (block, surface), ready for in-place mutation. Returns null if
        /// the entity is null (caller bails). On the server we also do
        /// the lazy-load-from-storage check so the first edit after a
        /// save merges with existing data instead of clobbering it.</summary>
        static SurfaceSettings TakeForMutation(IMyEntity entity, int surfaceIdx)
        {
            if (entity == null) return null;
            long key = MakeKey(entity.EntityId, surfaceIdx);

            SurfaceSettings cur;
            lock (s_stateLock)
            {
                if (s_state.TryGetValue(key, out cur)) return cur;
            }

            if (IsServer && TryLoadAllSurfacesFromStorage(entity))
            {
                lock (s_stateLock)
                {
                    if (s_state.TryGetValue(key, out cur)) return cur;
                }
            }

            // No existing state — start from defaults.
            return new SurfaceSettings();
        }

        static void CommitAndPublish(IMyEntity entity, int surfaceIdx, SurfaceSettings cur)
        {
            long key = MakeKey(entity.EntityId, surfaceIdx);
            lock (s_stateLock) { s_state[key] = cur; }

            // Persist on server so the next world load sees the change.
            if (IsServer) PersistToStorage(entity);

            // Replicate to other peers. Server broadcasts, client sends
            // to server (which broadcasts to everyone else).
            SettingsNetwork.SendUpdate(entity.EntityId, surfaceIdx, cur);

            // Local TSS notification — instant feedback for the user
            // who's editing, without waiting for any network round-trip.
            PanelTss.NotifyStorageChanged(entity.EntityId, surfaceIdx);
        }

        // ── Inbound from network ────────────────────────────────────────

        /// <summary>Called by <see cref="SettingsNetwork"/> when a remote
        /// peer's edit (or the server's full-sync) arrives. Replaces the
        /// local entry, persists if server, and triggers the local TSS
        /// re-sync so the plugin picks the new value up.</summary>
        public static void ApplyRemote(long blockId, int surfaceIdx, SurfaceSettings data)
        {
            if (data == null) return;
            long key = MakeKey(blockId, surfaceIdx);
            lock (s_stateLock) { s_state[key] = data; }

            if (IsServer)
            {
                IMyEntity ent;
                if (MyAPIGateway.Entities.TryGetEntityById(blockId, out ent))
                    PersistToStorage(ent);
            }

            PanelTss.NotifyStorageChanged(blockId, surfaceIdx);
        }

        /// <summary>Snapshot of every in-memory entry, used by the server
        /// to satisfy a client's FullSyncRequest. Allocates each call —
        /// only invoked on player join, so cost is negligible.</summary>
        internal static List<SurfaceEntry> SnapshotAll()
        {
            lock (s_stateLock)
            {
                var list = new List<SurfaceEntry>(s_state.Count);
                foreach (var kv in s_state)
                {
                    list.Add(new SurfaceEntry
                    {
                        BlockId    = kv.Key >> 4,
                        SurfaceIdx = (int)(kv.Key & 0xF),
                        Settings   = kv.Value,
                    });
                }
                return list;
            }
        }

        /// <summary>Drop all in-memory state. Called on session unload to
        /// keep session-1 references from bleeding into session-2 — the
        /// mod assembly can't be unloaded in .NET Framework so the
        /// static dict is the only thing that persists between worlds.</summary>
        public static void Clear()
        {
            lock (s_stateLock) { s_state.Clear(); }
        }

        // ── Persistence (server only) ───────────────────────────────────

        static bool IsServer
            => MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer;

                /// <summary>Server-only: eager-load this entity's persisted
        /// surface entries into the in-memory dict. Called by
        /// <see cref="MirrorSession"/>'s OnEntityAdd hook so the server
        /// has a complete <see cref="SnapshotAll"/> ready to ship to any
        /// connecting client, without having to wait for each block's
        /// TSS to trigger a lazy load.</summary>
        public static bool TryLoadEntity(IMyEntity entity)
        {
            if (!IsServer) return false;
            return TryLoadAllSurfacesFromStorage(entity);
        }

        /// <summary>Load every surface entry for this block from its
        /// storage component into <see cref="s_state"/>. Returns true if
        /// any entry was loaded. No-op (and false) if the block has no
        /// storage or the blob is malformed.</summary>
        static bool TryLoadAllSurfacesFromStorage(IMyEntity entity)
        {
            if (entity == null || entity.Storage == null) return false;
            string blob;
            if (!entity.Storage.TryGetValue(StorageGuid, out blob)
                || string.IsNullOrEmpty(blob)) return false;

            BlockSurfacesData data;
            try
            {
                byte[] bytes = Convert.FromBase64String(blob);
                data = MyAPIGateway.Utilities.SerializeFromBinary<BlockSurfacesData>(bytes);
            }
            catch (Exception ex)
            {
                // Bad / legacy / corrupt blob: wipe it from the block's
                // storage so neither this lazy-load nor any future one
                // sees it again. One log line per entity, then gone.
                MyLog.Default.WriteLine("[MirrorMod] Storage blob (block " + entity.EntityId + ") unreadable, removing: " + ex.Message);
                try { entity.Storage.RemoveValue(StorageGuid); }
                catch { /* defensive: removal isn't critical, the in-memory state will reflect defaults */ }
                return false;
            }
            if (data == null || data.Surfaces == null || data.Surfaces.Count == 0) return false;

            long blockId = entity.EntityId;
            lock (s_stateLock)
            {
                foreach (var entry in data.Surfaces)
                {
                    if (entry == null || entry.Settings == null) continue;
                    s_state[MakeKey(blockId, entry.SurfaceIdx)] = entry.Settings;
                }
            }
            return true;
        }

        /// <summary>Write every in-memory surface entry for this block
        /// back to its storage component. Server-only — clients never
        /// persist (they receive sync from the server).</summary>
        static void PersistToStorage(IMyEntity entity)
        {
            if (entity == null) return;

            try
            {
                long blockId = entity.EntityId;

                // Collect all surfaces for this block. ProtoBuf doesn't
                // need a separate "dirty" set — we always re-serialise
                // the full set for one block; per-block storage blobs
                // are tiny (a few dozen bytes each).
                var data = new BlockSurfacesData();
                lock (s_stateLock)
                {
                    foreach (var kv in s_state)
                    {
                        if ((kv.Key >> 4) != blockId) continue;
                        data.Surfaces.Add(new BlockSurfaceEntry
                        {
                            SurfaceIdx = (int)(kv.Key & 0xF),
                            Settings   = kv.Value,
                        });
                    }
                }

                if (data.Surfaces.Count == 0)
                {
                    if (entity.Storage != null) entity.Storage.RemoveValue(StorageGuid);
                    return;
                }

                // "Only use set accessor if value is null" — per the doc
                // on IMyEntity.Storage.
                if (entity.Storage == null) entity.Storage = new MyModStorageComponent();

                byte[] bytes = MyAPIGateway.Utilities.SerializeToBinary(data);
                entity.Storage[StorageGuid] = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] PersistToStorage (block " + entity.EntityId + ") failed: " + ex);
            }
        }
    }
}

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

        static long MakeKey(long blockId, int surfaceIdx)
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

        /// <summary>Returns the current settings for the given surface.
        /// Never null — defaults are returned if no entry exists.
        /// Server first-touch checks the entity's storage component
        /// (lazy load), so blocks loaded from a save populate on demand.</summary>
        static SurfaceSettings Get(IMyEntity entity, int surfaceIdx)
        {
            if (entity == null) return new SurfaceSettings();
            long key = MakeKey(entity.EntityId, surfaceIdx);

            SurfaceSettings s;
            if (s_state.TryGetValue(key, out s)) return s;

            // Server: lazy-load from per-block storage if this is the
            // first read after a save. After this all surfaces on the
            // block are cached; subsequent reads hit the dict directly.
            if (IsServer && TryLoadAllSurfacesFromStorage(entity))
            {
                if (s_state.TryGetValue(key, out s)) return s;
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
            cur.CameraId = id;
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetZoom(IMyEntity entity, int surfaceIdx, float zoom)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            cur.Zoom = SurfaceSettings.ClampZoom(zoom);
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetRange(IMyEntity entity, int surfaceIdx, float range)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            cur.Range = SurfaceSettings.ClampRange(range);
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetMirrorAngleX(IMyEntity entity, int surfaceIdx, float deg)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            cur.MirrorAngleDegX = SurfaceSettings.ClampMirrorAngle(deg);
            CommitAndPublish(entity, surfaceIdx, cur);
        }

        public static void SetMirrorAngleY(IMyEntity entity, int surfaceIdx, float deg)
        {
            var cur = TakeForMutation(entity, surfaceIdx);
            if (cur == null) return;
            cur.MirrorAngleDegY = SurfaceSettings.ClampMirrorAngle(deg);
            CommitAndPublish(entity, surfaceIdx, cur);
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
            if (s_state.TryGetValue(key, out cur)) return cur;

            if (IsServer && TryLoadAllSurfacesFromStorage(entity)
                && s_state.TryGetValue(key, out cur))
                return cur;

            // No existing state — start from defaults.
            return new SurfaceSettings();
        }

        static void CommitAndPublish(IMyEntity entity, int surfaceIdx, SurfaceSettings cur)
        {
            long key = MakeKey(entity.EntityId, surfaceIdx);
            s_state[key] = cur;

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
            s_state[key] = data;

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

        /// <summary>Drop all in-memory state. Called on session unload to
        /// keep session-1 references from bleeding into session-2 — the
        /// mod assembly can't be unloaded in .NET Framework so the
        /// static dict is the only thing that persists between worlds.</summary>
        public static void Clear() => s_state.Clear();

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
                MyLog.Default.WriteLine("[MirrorMod] Storage load (block " + entity.EntityId + ") failed: " + ex);
                return false;
            }
            if (data == null || data.Surfaces == null || data.Surfaces.Count == 0) return false;

            long blockId = entity.EntityId;
            foreach (var entry in data.Surfaces)
            {
                if (entry == null || entry.Settings == null) continue;
                s_state[MakeKey(blockId, entry.SurfaceIdx)] = entry.Settings;
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
                foreach (var kv in s_state)
                {
                    if ((kv.Key >> 4) != blockId) continue;
                    data.Surfaces.Add(new BlockSurfaceEntry
                    {
                        SurfaceIdx = (int)(kv.Key & 0xF),
                        Settings   = kv.Value,
                    });
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

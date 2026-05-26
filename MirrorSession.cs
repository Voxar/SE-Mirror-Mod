using System;
using System.Collections.Generic;
using MirrorCameraMod.Network;
using MirrorCameraMod.Terminal;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace MirrorCameraMod
{
    /// <summary>
    /// Session component. Three responsibilities:
    ///
    /// <list type="bullet">
    ///   <item><b>Terminal controls dispatcher</b>: hooks
    ///         <see cref="LcdAppTerminalControls"/> at load.</item>
    ///   <item><b>MP sync layer</b>: registers
    ///         <see cref="SettingsNetwork"/>; on client load, asks the
    ///         server for the current state; on server, eagerly loads
    ///         every existing block's persisted settings into memory
    ///         (so the snapshot we ship to joining clients is complete
    ///         even when no TSS has triggered a lazy load yet) and
    ///         proactively pushes a full sync to each new player as
    ///         they connect.</item>
    ///   <item><b>Facade</b> for non-terminal callers
    ///         (<see cref="GetEffectiveCameraId"/>, range/zoom getters).</item>
    /// </list>
    ///
    /// <para>Update tick is <c>AfterSimulation</c> so the server-side
    /// player-connect poll runs once per sim tick. Cheap: one
    /// <c>GetPlayers</c> call + a hash-set compare per tick when no
    /// players are connecting; effectively zero work in steady state.</para>
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class MirrorSession : MySessionComponentBase
    {
        public const string MirrorScriptId = "Mirror.voxar";
        public const string CameraScriptId = "Camera.voxar";

        readonly LcdAppTerminalControls _controls = new LcdAppTerminalControls();

        // Server-only: tracks which SteamIds we've already pushed full
        // state to. Per-tick we diff the live player list against this
        // set to detect joins (and prune disconnects so a reconnecting
        // player re-syncs). Empty on clients — they never enter the
        // poll code path.
        readonly HashSet<ulong> _syncedPlayers = new HashSet<ulong>();
        readonly HashSet<ulong> _livePlayers   = new HashSet<ulong>();
        readonly List<IMyPlayer> _playerBuf    = new List<IMyPlayer>();

        bool _entityAddHooked;

        public override void LoadData()
        {
            _controls.Hook();
            SettingsNetwork.Register();

            // Client-only: ask the server for state. Server-side, this
            // is a no-op (we're already authoritative).
            SettingsNetwork.RequestFullSyncIfClient();

            // Server-only: hook entity-add so any block with our storage
            // entry is eager-loaded into MirrorStorage's in-memory dict
            // as it loads. Without this, dedicated servers (no TSS
            // running locally to trigger lazy loads) would have an empty
            // snapshot to ship to joining clients.
            if (IsServer())
            {
                MyAPIGateway.Entities.OnEntityAdd += OnEntityAdded;
                _entityAddHooked = true;

                // Also catch entities already loaded before this hook
                // (mod loads after some world entities are already in
                // the scene). One-shot sweep.
                var allEntities = new HashSet<IMyEntity>();
                try { MyAPIGateway.Entities.GetEntities(allEntities); }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] Initial entity sweep failed: " + ex);
                }
                foreach (var ent in allEntities) TryLoadEntity(ent);
            }
        }

        protected override void UnloadData()
        {
            if (_entityAddHooked)
            {
                try { MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdded; }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] OnEntityAdd unhook failed: " + ex);
                }
                _entityAddHooked = false;
            }

            _controls.Unhook();
            SettingsNetwork.Unregister();

            // Defensive: in .NET Framework the mod assembly persists
            // across world reloads. Drop all panel + settings state so
            // session-N entity references can't bleed into session-N+1
            // if any TSS failed to Dispose.
            PanelRegistry.Clear();
            Settings.MirrorStorage.Clear();

            _syncedPlayers.Clear();
            _livePlayers.Clear();
            _playerBuf.Clear();
        }

        public override void UpdateAfterSimulation()
        {
            // Server-only: detect newly-connected players and proactively
            // push the full settings state to them. Belt-and-suspenders
            // with the client-side RequestFullSyncIfClient (which may
            // fire before the network channel is fully up); whichever
            // arrives second is a redundant SnapshotAll send — harmless,
            // and rare.
            if (!IsServer()) return;

            _playerBuf.Clear();
            try { MyAPIGateway.Players.GetPlayers(_playerBuf); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] GetPlayers failed: " + ex);
                return;
            }

            // Build the current SteamId set, detect joins.
            _livePlayers.Clear();
            ulong serverId = MyAPIGateway.Multiplayer.ServerId;
            for (int i = 0; i < _playerBuf.Count; i++)
            {
                ulong sid = _playerBuf[i].SteamUserId;
                if (sid == 0UL || sid == serverId) continue;  // skip invalid + server self
                _livePlayers.Add(sid);

                if (_syncedPlayers.Add(sid))
                {
                    try { SettingsNetwork.SendFullSyncToClient(sid); }
                    catch (Exception ex)
                    {
                        MyLog.Default.WriteLine("[MirrorMod] FullSync push to " + sid + " failed: " + ex);
                    }
                }
            }

            // Prune disconnects so reconnecting players re-sync.
            if (_syncedPlayers.Count > _livePlayers.Count)
            {
                // Walk synced and remove anyone no longer live. Allocates
                // a list only when there's a difference, which is rare.
                List<ulong> toRemove = null;
                foreach (var sid in _syncedPlayers)
                {
                    if (!_livePlayers.Contains(sid))
                    {
                        if (toRemove == null) toRemove = new List<ulong>();
                        toRemove.Add(sid);
                    }
                }
                if (toRemove != null)
                    foreach (var sid in toRemove) _syncedPlayers.Remove(sid);
            }
        }

        void OnEntityAdded(IMyEntity entity)
        {
            // Cheap fast-paths so we don't pay the storage probe for
            // every grid / character / floating object that loads.
            if (entity == null) return;
            if (entity.Storage == null) return;
            try { TryLoadEntity(entity); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] OnEntityAdded load failed for "
                    + entity.EntityId + ": " + ex);
            }
        }

        public static readonly string NoPluginMessage =
            "Hi! I am a Mirror! Yes a real working Mirror in Space Engineers!\n" + 
            "\n" + 
            "Or at least I could be... Sadly I can not render the world by myself :(\n" + 
            "I need help from a plugin called \"Mirror\" that is available via Pulsar,\n" + 
            "the Space Engineers Plugin Loader.\n" + 
            "\n" + 
            "Read all about it here:\n" + 
            "https://github.com/SpaceGT/Pulsar";

        static bool TryLoadEntity(IMyEntity entity)
            => Settings.MirrorStorage.TryLoadEntity(entity);

        static bool IsServer()
            => MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer;

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

using System;
using System.Collections.Generic;
using MirrorCameraMod.Settings;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace MirrorCameraMod.Network
{
    /// <summary>
    /// Multiplayer sync layer for per-surface settings. Server is
    /// authoritative for state and persistence; clients send edits to the
    /// server, server broadcasts to all clients.
    ///
    /// <para><b>Wire protocol</b> (one envelope, three message types):</para>
    /// <list type="bullet">
    ///   <item><c>SurfaceUpdate</c>: client edits a value, sends to
    ///         server; server stores and broadcasts to every other
    ///         client. Server itself can also originate updates.</item>
    ///   <item><c>FullSyncRequest</c>: client sends on first session
    ///         load to grab the full state for any blocks that already
    ///         have settings persisted on the server.</item>
    ///   <item><c>FullSyncResponse</c>: server's reply with the
    ///         complete in-memory state.</item>
    /// </list>
    ///
    /// <para>Single-player / server-host (IsServer == true) skips the
    /// network round-trip — Set* calls <c>ApplyAuthoritative</c> directly
    /// and only broadcasts when there are remote clients.</para>
    /// </summary>
    public static class SettingsNetwork
    {
        /// <summary>Per-mod-unique ushort. Picked to avoid colliding
        /// with common-mod IDs (Digi's BuildInfo, etc.). If you ever
        /// see "unhandled message" complaints from another mod, change
        /// this — the value is opaque to the protocol.</summary>
        const ushort HandlerId = 0xCC53;

        static bool s_registered;

        // ── Lifecycle ────────────────────────────────────────────────────

        public static void Register()
        {
            if (s_registered) return;
            try
            {
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(HandlerId, OnMessageReceived);
                s_registered = true;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Network register failed: " + ex);
            }
        }

        public static void Unregister()
        {
            if (!s_registered) return;
            try
            {
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(HandlerId, OnMessageReceived);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Network unregister failed: " + ex);
            }
            s_registered = false;

            // Debounce state is keyed by entity id; drop it with the
            // session so a reloaded world starts clean.
            s_lastSend.Clear();
            s_pending.Clear();
            s_flushBuf.Clear();
        }

        /// <summary>Clients call after Register to ask the server for
        /// the full current state. Server's response populates
        /// <see cref="MirrorStorage"/>'s in-memory dict on this client.
        /// No-op on the server itself (already authoritative).</summary>
        public static void RequestFullSyncIfClient()
        {
            if (MyAPIGateway.Multiplayer == null) return;
            if (MyAPIGateway.Multiplayer.IsServer) return;
            var msg = new NetworkMessage { Type = NetworkMessageType.FullSyncRequest };
            SendToServer(msg);
        }

        // ── Outbound: from MirrorStorage.Set* ─────────────────────────────

        // Debounce per (blockId, surfaceIdx). Slider drag fires Set* at
        // ~60Hz; without this each drag would generate 60 SurfaceUpdate
        // messages/sec per client (and the server would then broadcast
        // each one to every other client — N×60 msgs/sec). With a
        // 100ms window we cap at ~10 msgs/sec per dragged slider.
        //
        // Leading edge sends immediately. A send that lands inside the
        // window is NOT dropped: its key is parked in s_pending and
        // FlushPending (every sim tick, from MirrorSession) sends the
        // CURRENT settings for that key once the window has passed. So
        // the final value of a drag, and any edit that immediately
        // follows another on the same surface (SetCameraId → resolve →
        // SetCameraName runs inside one call stack), always reaches
        // the server and the other clients. The flush re-reads the
        // settings rather than keeping the object passed to
        // SendUpdate, because ApplyRemote can swap the stored instance
        // in the meantime.
        static readonly TimeSpan SendDebounceWindow = TimeSpan.FromMilliseconds(100);
        static readonly Dictionary<long, DateTime> s_lastSend =
            new Dictionary<long, DateTime>();
        static readonly HashSet<long> s_pending  = new HashSet<long>();
        static readonly List<long>    s_flushBuf = new List<long>();

        static long MakeDebounceKey(long blockId, int surfaceIdx)
            => MirrorStorage.MakeKey(blockId, surfaceIdx);

        /// <summary>Called by <see cref="MirrorStorage"/> after an edit
        /// updates the local in-memory state. Sends now when the
        /// surface's debounce window has passed, otherwise defers to
        /// <see cref="FlushPending"/>. If we're the server, broadcasts
        /// to other clients; if client, sends to server (which
        /// broadcasts on its side).</summary>
        public static void SendUpdate(long blockId, int surfaceIdx, SurfaceSettings data)
        {
            if (MyAPIGateway.Multiplayer == null) return;  // single-player offline — no network

            long key = MakeDebounceKey(blockId, surfaceIdx);
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (s_lastSend.TryGetValue(key, out last) && now - last < SendDebounceWindow)
            {
                s_pending.Add(key);
                return;
            }
            s_lastSend[key] = now;
            s_pending.Remove(key);
            Dispatch(blockId, surfaceIdx, data);
        }

        /// <summary>Send the current settings of every surface whose
        /// last edit was deferred by the debounce and whose window has
        /// since passed. Called once per sim tick by
        /// <see cref="MirrorSession"/>; cheap no-op when nothing is
        /// pending, which is the steady state.</summary>
        public static void FlushPending()
        {
            if (s_pending.Count == 0) return;
            if (MyAPIGateway.Multiplayer == null) { s_pending.Clear(); return; }

            DateTime now = DateTime.UtcNow;
            s_flushBuf.Clear();
            foreach (long key in s_pending)
            {
                DateTime last;
                if (!s_lastSend.TryGetValue(key, out last) || now - last >= SendDebounceWindow)
                    s_flushBuf.Add(key);
            }

            for (int i = 0; i < s_flushBuf.Count; i++)
            {
                long key = s_flushBuf[i];
                s_pending.Remove(key);
                SurfaceSettings cur;
                if (!MirrorStorage.TryGetByKey(key, out cur)) continue;
                s_lastSend[key] = now;
                Dispatch(key >> 4, (int)(key & 0xF), cur);
            }
        }

        static void Dispatch(long blockId, int surfaceIdx, SurfaceSettings data)
        {
            var msg = new NetworkMessage
            {
                Type  = NetworkMessageType.SurfaceUpdate,
                Entry = new SurfaceEntry { BlockId = blockId, SurfaceIdx = surfaceIdx, Settings = data },
            };

            if (MyAPIGateway.Multiplayer.IsServer)
                BroadcastToOtherClients(msg, excludeSteamId: 0);
            else
                SendToServer(msg);
        }

        // ── Inbound ─────────────────────────────────────────────────────

        static void OnMessageReceived(ushort handlerId, byte[] data, ulong senderSteamId, bool fromServer)
        {
            NetworkMessage msg;
            try { msg = MyAPIGateway.Utilities.SerializeFromBinary<NetworkMessage>(data); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Deserialize failed: " + ex);
                return;
            }
            if (msg == null) return;

            switch (msg.Type)
            {
                case NetworkMessageType.SurfaceUpdate:
                    HandleSurfaceUpdate(msg, senderSteamId);
                    break;

                case NetworkMessageType.FullSyncRequest:
                    HandleFullSyncRequest(senderSteamId);
                    break;

                case NetworkMessageType.FullSyncResponse:
                    HandleFullSyncResponse(msg);
                    break;
            }
        }

        static void HandleSurfaceUpdate(NetworkMessage msg, ulong senderSteamId)
        {
            if (msg.Entry == null || msg.Entry.Settings == null) return;

            // Apply locally regardless of role — both server and clients
            // need the new state in their in-memory dict so reads see it.
            MirrorStorage.ApplyRemote(msg.Entry.BlockId, msg.Entry.SurfaceIdx, msg.Entry.Settings);

            // Server: rebroadcast to every other client so they all
            // converge. Sender excluded — they already have the value
            // (they're the source).
            if (MyAPIGateway.Multiplayer.IsServer)
                BroadcastToOtherClients(msg, excludeSteamId: senderSteamId);
        }

        static void HandleFullSyncRequest(ulong requesterSteamId)
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            SendFullSyncToClient(requesterSteamId);
        }

        /// <summary>Server-only: send the complete current state to one
        /// specific client. Used both as a reply to a client's
        /// <see cref="NetworkMessageType.FullSyncRequest"/> AND
        /// proactively by <see cref="MirrorSession"/> when it detects a
        /// new player connecting (since SE's mod API doesn't expose a
        /// player-connect event, we can't rely solely on the client's
        /// request reaching us before its TSSes start reading state).</summary>
        public static void SendFullSyncToClient(ulong steamId)
        {
            if (MyAPIGateway.Multiplayer == null) return;
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            if (steamId == 0UL) return;
            if (steamId == MyAPIGateway.Multiplayer.ServerId) return;  // skip self

            var response = new NetworkMessage
            {
                Type    = NetworkMessageType.FullSyncResponse,
                Entries = MirrorStorage.SnapshotAll(),
            };
            SendToSpecificClient(response, steamId);
        }

        static void HandleFullSyncResponse(NetworkMessage msg)
        {
            if (msg.Entries == null) return;
            foreach (var entry in msg.Entries)
            {
                if (entry == null || entry.Settings == null) continue;
                MirrorStorage.ApplyRemote(entry.BlockId, entry.SurfaceIdx, entry.Settings);
            }
        }

        // ── Send primitives ─────────────────────────────────────────────

        static void SendToServer(NetworkMessage msg)
        {
            byte[] bytes;
            try { bytes = MyAPIGateway.Utilities.SerializeToBinary(msg); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SendToServer serialize failed: " + ex);
                return;
            }
            try { MyAPIGateway.Multiplayer.SendMessageToServer(HandlerId, bytes); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SendToServer dispatch failed: " + ex);
            }
        }

        static void SendToSpecificClient(NetworkMessage msg, ulong steamId)
        {
            byte[] bytes;
            try { bytes = MyAPIGateway.Utilities.SerializeToBinary(msg); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SendToSpecificClient serialize failed: " + ex);
                return;
            }
            try { MyAPIGateway.Multiplayer.SendMessageTo(HandlerId, bytes, steamId); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SendToSpecificClient dispatch failed: " + ex);
            }
        }

        /// <summary>Broadcast to every connected non-server client except
        /// <paramref name="excludeSteamId"/> (pass 0 to broadcast to
        /// everyone). The server doesn't message itself.</summary>
        static void BroadcastToOtherClients(NetworkMessage msg, ulong excludeSteamId)
        {
            byte[] bytes;
            try { bytes = MyAPIGateway.Utilities.SerializeToBinary(msg); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Broadcast serialize failed: " + ex);
                return;
            }

            var players = s_playerBuf;
            players.Clear();
            try { MyAPIGateway.Players.GetPlayers(players); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] GetPlayers failed: " + ex);
                return;
            }

            ulong serverId = MyAPIGateway.Multiplayer.ServerId;
            for (int i = 0; i < players.Count; i++)
            {
                ulong sid = players[i].SteamUserId;
                if (sid == serverId) continue;       // server doesn't message itself
                if (sid == excludeSteamId) continue; // skip the originator
                try { MyAPIGateway.Multiplayer.SendMessageTo(HandlerId, bytes, sid); }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] SendMessageTo " + sid + " failed: " + ex);
                }
            }
        }

        // Reusable players list — avoids per-broadcast allocation. Sim
        // thread is single-writer so static reuse is safe.
        static readonly List<IMyPlayer> s_playerBuf = new List<IMyPlayer>();
    }
}

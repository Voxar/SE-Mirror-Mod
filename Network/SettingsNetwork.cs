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

        /// <summary>Called by <see cref="MirrorStorage"/> after an edit
        /// updates the local in-memory state. If we're the server,
        /// broadcasts to other clients. If we're a client, sends to
        /// server (which broadcasts on its side).</summary>
        public static void SendUpdate(long blockId, int surfaceIdx, SurfaceSettings data)
        {
            if (MyAPIGateway.Multiplayer == null) return;  // single-player offline — no network

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

            var response = new NetworkMessage
            {
                Type    = NetworkMessageType.FullSyncResponse,
                Entries = MirrorStorage.SnapshotAll(),
            };
            SendToSpecificClient(response, requesterSteamId);
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

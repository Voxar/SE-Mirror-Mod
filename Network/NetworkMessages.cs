using System.Collections.Generic;
using MirrorCameraMod.Settings;
using ProtoBuf;

namespace MirrorCameraMod.Network
{
    /// <summary>
    /// Discriminator for the single envelope <see cref="NetworkMessage"/>.
    /// Stored as one byte on the wire so we don't pay enum-name cost on
    /// every send.
    /// </summary>
    public enum NetworkMessageType : byte
    {
        SurfaceUpdate    = 1,  // one (block, surface) → new settings; client→server→all
        FullSyncRequest  = 2,  // client→server: send me all current state
        FullSyncResponse = 3,  // server→client: here is all current state
    }

    /// <summary>
    /// One per-surface settings record on the wire. Used both for
    /// SurfaceUpdate (single edit replication) and inside
    /// FullSyncResponse (one entry per persisted surface).
    /// </summary>
    [ProtoContract]
    public class SurfaceEntry
    {
        [ProtoMember(1)] public long             BlockId;
        [ProtoMember(2)] public int              SurfaceIdx;
        [ProtoMember(3)] public SurfaceSettings  Settings;
    }

    /// <summary>
    /// Wire envelope. ProtoBuf-net handles nullable members fine, so the
    /// shape is: <see cref="Type"/> picks which field is populated.
    /// </summary>
    [ProtoContract]
    public class NetworkMessage
    {
        [ProtoMember(1)] public NetworkMessageType Type;
        [ProtoMember(2)] public SurfaceEntry        Entry;    // SurfaceUpdate only
        [ProtoMember(3)] public List<SurfaceEntry>  Entries;  // FullSyncResponse only
    }

    /// <summary>
    /// Per-block persistence shape. Server writes one of these (binary,
    /// base64) to the block's <c>MyModStorageComponent</c> under the
    /// MirrorStorage GUID. Travels with blueprints, persists across
    /// save/reload. Only the server reads/writes this — clients receive
    /// the same data over the wire on join.
    /// </summary>
    [ProtoContract]
    public class BlockSurfacesData
    {
        [ProtoMember(1)] public List<BlockSurfaceEntry> Surfaces = new List<BlockSurfaceEntry>();
    }

    [ProtoContract]
    public class BlockSurfaceEntry
    {
        [ProtoMember(1)] public int             SurfaceIdx;
        [ProtoMember(2)] public SurfaceSettings Settings;
    }
}

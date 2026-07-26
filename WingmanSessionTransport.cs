using System;
using System.Collections.Generic;
using Mirage;
using Mirage.RemoteCalls;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

internal sealed class WingmanSessionTransport : NetworkSceneSingleton<WingmanSessionTransport>
{
    internal const int ProtocolVersion = 4;
    internal const int CmdSetModeIndex = 0;
    internal const int RpcModeChangedIndex = 1;
    internal const int CmdRequestSnapshotIndex = 2;
    internal const int RpcSnapshotIndex = 3;
    internal const int CmdSetLoadoutPlanIndex = 4;
    internal const int TransportPrefabHash = 0x4C570001;
    private const int RpcCount = 5;
    private const int MaxMountKeyLength = 128;

    internal readonly struct SnapshotEntry
    {
        internal readonly uint pid;
        internal readonly int ordinal;
        internal readonly int groupId;
        internal readonly int groupSlot;
        internal readonly int rawMode;

        internal SnapshotEntry(uint pid, int ordinal, int groupId, int groupSlot, int rawMode)
        {
            this.pid = pid;
            this.ordinal = ordinal;
            this.groupId = groupId;
            this.groupSlot = groupSlot;
            this.rawMode = rawMode;
        }
    }

    private static readonly HashSet<ClientObjectManager> registeredManagers = new HashSet<ClientObjectManager>();
    private static GameObject? template;
    private static WingmanSessionTransport? spawned;
    private static NetworkManagerNuclearOption? spawnedManager;
    private static Plugin? plugin;

    private readonly List<uint> orderedPids = new List<uint>();
    private readonly Dictionary<uint, SnapshotEntry> entries = new Dictionary<uint, SnapshotEntry>();
    private bool snapshotReady;
    private uint snapshotSessionId;
    private uint homePid;

    internal static void BindPlugin(Plugin value) => plugin = value;

    public override void Awake()
    {
        base.Awake();
        Identity.OnStartClient.AddListener(OnStartClient);
        Identity.OnStopClient.AddListener(OnStopClient);
    }

    public override int GetRpcCount() => RpcCount;

    protected override void RegisterRpc(RemoteCallCollection rpc)
    {
        rpc.Register(CmdSetModeIndex, "LoyalWingman.WingmanSessionTransport.CmdSetMode", false,
                     RpcInvokeType.ServerRpc, this, Skeleton_CmdSetMode, RpcRateLimitConfig.Enabled(1f, 10, 30, 2));
        rpc.Register(RpcModeChangedIndex, "LoyalWingman.WingmanSessionTransport.RpcModeChanged", false,
                     RpcInvokeType.ClientRpc, this, Skeleton_RpcModeChanged, default);
        rpc.Register(CmdRequestSnapshotIndex, "LoyalWingman.WingmanSessionTransport.CmdRequestSnapshot", false,
                     RpcInvokeType.ServerRpc, this, Skeleton_CmdRequestSnapshot, RpcRateLimitConfig.Enabled(1f, 2, 10, 3));
        rpc.Register(RpcSnapshotIndex, "LoyalWingman.WingmanSessionTransport.RpcSnapshot", false,
                     RpcInvokeType.ClientRpc, this, Skeleton_RpcSnapshot, default);
        rpc.Register(CmdSetLoadoutPlanIndex, "LoyalWingman.WingmanSessionTransport.CmdSetLoadoutPlan", false,
                     RpcInvokeType.ServerRpc, this, Skeleton_CmdSetLoadoutPlan, RpcRateLimitConfig.Enabled(1f, 5, 15, 3));
    }

    private static void Skeleton_CmdSetMode(NetworkBehaviour obj, NetworkReader reader, INetworkPlayer sender,
                                            int replyId)
    {
        int protocol = reader.ReadInt32();
        uint sessionId = reader.ReadUInt32();
        uint dronePid = reader.ReadUInt32();
        int mode = reader.ReadInt32();
        ((WingmanSessionTransport)obj).ServerHandleSetMode(protocol, sessionId, dronePid, mode, sender, out _);
    }

    private static void Skeleton_RpcModeChanged(NetworkBehaviour obj, NetworkReader reader, INetworkPlayer sender,
                                                int replyId)
    {
        int protocol = reader.ReadInt32();
        uint sessionId = reader.ReadUInt32();
        uint dronePid = reader.ReadUInt32();
        int mode = reader.ReadInt32();
        bool ok = reader.ReadBoolean();
        string reason = reader.ReadString();
        ((WingmanSessionTransport)obj).ClientHandleModeChanged(protocol, sessionId, dronePid, mode, ok, reason);
    }

    private static void Skeleton_CmdRequestSnapshot(NetworkBehaviour obj, NetworkReader reader,
                                                    INetworkPlayer sender, int replyId)
    {
        int protocol = reader.ReadInt32();
        uint requestedSessionId = reader.ReadUInt32();
        ((WingmanSessionTransport)obj).ServerHandleRequestSnapshot(protocol, requestedSessionId, sender);
    }

    private static void Skeleton_RpcSnapshot(NetworkBehaviour obj, NetworkReader reader, INetworkPlayer sender,
                                             int replyId)
    {
        int protocol = reader.ReadInt32();
        uint sessionId = reader.ReadUInt32();
        bool ok = reader.ReadBoolean();
        string reason = reader.ReadString();
        uint snapshotHomePid = reader.ReadUInt32();
        int count = reader.ReadInt32();
        var snapshotEntries = new List<SnapshotEntry>(Math.Max(0, count));
        for (int index = 0; index < count; ++index)
            snapshotEntries.Add(new SnapshotEntry(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
        ((WingmanSessionTransport)obj).ClientHandleSnapshot(protocol, sessionId, ok, reason, snapshotHomePid,
                                                            snapshotEntries);
    }

    private static void Skeleton_CmdSetLoadoutPlan(NetworkBehaviour obj, NetworkReader reader, INetworkPlayer sender,
                                                   int replyId)
    {
        int protocol = reader.ReadInt32();
        int cradleCount = reader.ReadInt32();
        int hardpoints = reader.ReadInt32();
        Plugin? ownerPlugin = plugin;
        string reason = "";
        if (ownerPlugin == null || !ownerPlugin.TryGetRemoteFqLoadoutDimensions(sender, out _, out _, out int expectedCradleCount,
                                                                                out int expectedHardpoints, out reason))
        {
            ownerPlugin?.RejectRemoteFqLoadoutPlan(ownerPlugin == null ? "plugin_missing" : reason);
            return;
        }
        if (protocol != ProtocolVersion) { ownerPlugin.RejectRemoteFqLoadoutPlan("protocol_mismatch"); return; }
        if (cradleCount != expectedCradleCount || hardpoints != expectedHardpoints)
        {
            ownerPlugin.RejectRemoteFqLoadoutPlan("sender_dimensions");
            return;
        }
        if (cradleCount < 0 || hardpoints <= 0 ||
            cradleCount > int.MaxValue / FqCradleLoadoutLogic.PhysicalRoundsPerCradle ||
            cradleCount > 0 && hardpoints > int.MaxValue / (cradleCount * FqCradleLoadoutLogic.PhysicalRoundsPerCradle))
        {
            ownerPlugin.RejectRemoteFqLoadoutPlan("dimensions_malformed");
            return;
        }
        if (!CarrierFqLoadoutPlan.TryCreate(cradleCount, new string?[hardpoints], out CarrierFqLoadoutPlan? plan) ||
            plan == null)
            return;
        try
        {
            for (int cradleIndex = 0; cradleIndex < cradleCount; ++cradleIndex)
                for (int physicalRound = 0; physicalRound < plan.PhysicalRoundsPerCradle; ++physicalRound)
                    for (int hardpoint = 0; hardpoint < hardpoints; ++hardpoint)
                    {
                        bool hasKey = reader.ReadBoolean();
                        string? key = hasKey ? reader.ReadString() : null;
                        if (key != null && key.Length > MaxMountKeyLength)
                        {
                            ownerPlugin.RejectRemoteFqLoadoutPlan("mount_key_length");
                            return;
                        }
                        if (!plan.TrySet(cradleIndex, physicalRound, hardpoint, key)) return;
                    }
            ((WingmanSessionTransport)obj).ServerHandleLoadoutPlan(sender, plan);
        }
        catch { ownerPlugin.RejectRemoteFqLoadoutPlan("reader_malformed"); }
    }

    internal bool RequestSetMode(uint sessionId, Aircraft drone, DroneModeOverride mode, INetworkPlayer? hostSender,
                                 out string reason)
    {
        if (drone == null)
        {
            reason = "drone_missing";
            return false;
        }

        uint dronePid = drone.persistentID.Id;
        if (IsServer)
            return ServerHandleSetMode(ProtocolVersion, sessionId, dronePid, (int)mode, hostSender, out reason);

        using var writer = NetworkWriterPool.GetWriter();
        writer.WriteInt32(ProtocolVersion);
        writer.WriteUInt32(sessionId);
        writer.WriteUInt32(dronePid);
        writer.WriteInt32((int)mode);
        ServerRpcSender.Send(this, CmdSetModeIndex, writer, Channel.Reliable, false);
        reason = "sent";
        return true;
    }
    internal bool SendLoadoutPlan(CarrierFqLoadoutPlan plan)
    {
        if (IsServer || Identity == null || !Identity.IsSpawned) return false;
        using var writer = NetworkWriterPool.GetWriter();
        writer.WriteInt32(ProtocolVersion);
        writer.WriteInt32(plan.CradleCount);
        writer.WriteInt32(plan.HardpointCount);
        for (int cradleIndex = 0; cradleIndex < plan.CradleCount; ++cradleIndex)
            for (int physicalRound = 0; physicalRound < plan.PhysicalRoundsPerCradle; ++physicalRound)
            {
                if (!plan.TryGetRow(cradleIndex, physicalRound, out string?[] row)) return false;
                for (int hardpoint = 0; hardpoint < row.Length; ++hardpoint)
                {
                    writer.WriteBoolean(row[hardpoint] != null);
                    if (row[hardpoint] != null) writer.WriteString(row[hardpoint]!);
                }
            }
        ServerRpcSender.Send(this, CmdSetLoadoutPlanIndex, writer, Channel.Reliable, false);
        return true;
    }
    private void ServerHandleLoadoutPlan(INetworkPlayer sender, CarrierFqLoadoutPlan plan) =>
        plugin?.TrySetRemoteFqLoadoutPlan(sender, plan);

    private bool ServerHandleSetMode(int protocol, uint sessionId, uint dronePid, int mode, INetworkPlayer? sender,
                                     out string reason)
    {
        bool ok;
        CarrierSession? resolvedSession = null;
        if (protocol != ProtocolVersion)
        {
            ok = false;
            reason = "protocol_mismatch";
        }
        else if (plugin == null)
        {
            ok = false;
            reason = "session_not_ready";
        }
        else if (!plugin.TryResolveCommandSession(sessionId, sender, out CarrierSession session, out reason))
        {
            ok = false;
        }
        else if (!plugin.TryResolveSessionDrone(session, dronePid, out Aircraft drone, out reason))
        {
            ok = false;
        }
        else if (!IsKnownMode(mode))
        {
            ok = false;
            reason = "unsupported_mode";
        }
        else
        {
            ok = plugin.TrySetSessionDroneMode(session, drone, (DroneModeOverride)mode, out reason);
            if (ok)
                resolvedSession = session;
        }

        if (sender != null)
            SendModeChanged(sender, sessionId, dronePid, mode, ok, reason);
        if (ok && resolvedSession != null)
            PublishSnapshotToOwner(resolvedSession);
        return ok;
    }

    private void ServerHandleRequestSnapshot(int protocol, uint requestedSessionId, INetworkPlayer? sender)
    {
        if (sender == null)
            return;
        if (protocol != ProtocolVersion)
        {
            SendSnapshot(sender, 0, false, "protocol_mismatch", 0, new List<SnapshotEntry>());
            return;
        }
        if (plugin == null)
        {
            SendSnapshot(sender, 0, false, "session_not_ready", 0, new List<SnapshotEntry>());
            return;
        }
        if (!plugin.TryResolveSnapshotSession(requestedSessionId, sender, out CarrierSession session, out string reason))
        {
            SendSnapshot(sender, 0, false, reason, 0, new List<SnapshotEntry>());
            return;
        }
        if (!plugin.TryGetSnapshotOwner(session, out INetworkPlayer owner))
        {
            SendSnapshot(sender, 0, false, "snapshot_owner_missing", 0, new List<SnapshotEntry>());
            return;
        }
        SendSnapshotForSession(session, owner);
    }

    internal void PublishSnapshotToOwner(CarrierSession session)
    {
        if (!IsServer || plugin == null || !plugin.TryGetSnapshotOwner(session, out INetworkPlayer owner))
            return;
        SendSnapshotForSession(session, owner);
    }

    internal void PublishSessionClosed(CarrierSession session, string reason)
    {
        if (!IsServer || plugin == null || !plugin.TryGetSnapshotOwner(session, out INetworkPlayer owner))
            return;
        SendSnapshot(owner, session.SessionId, false, string.IsNullOrEmpty(reason) ? "session_closed" : reason, 0,
                     new List<SnapshotEntry>());
    }

    private void SendSnapshotForSession(CarrierSession session, INetworkPlayer target)
    {
        var snapshotEntries = new List<SnapshotEntry>();
        if (plugin == null)
        {
            SendSnapshot(target, 0, false, "session_not_ready", 0, snapshotEntries);
            return;
        }
        if (!plugin.TryBuildModeSnapshot(session, snapshotEntries, out uint snapshotHomePid, out string reason))
        {
            SendSnapshot(target, 0, false, reason, 0, snapshotEntries);
            return;
        }
        SendSnapshot(target, session.SessionId, true, "", snapshotHomePid, snapshotEntries);
    }

    private void SendModeChanged(INetworkPlayer target, uint sessionId, uint dronePid, int mode, bool ok, string reason)
    {
        using var writer = NetworkWriterPool.GetWriter();
        writer.WriteInt32(ProtocolVersion);
        writer.WriteUInt32(sessionId);
        writer.WriteUInt32(dronePid);
        writer.WriteInt32(mode);
        writer.WriteBoolean(ok);
        writer.WriteString(reason);
        ClientRpcSender.SendTarget(this, RpcModeChangedIndex, writer, Channel.Reliable, target);
    }

    private void SendSnapshot(INetworkPlayer target, uint sessionId, bool ok, string reason, uint snapshotHomePid,
                              List<SnapshotEntry> snapshotEntries)
    {
        using var writer = NetworkWriterPool.GetWriter();
        writer.WriteInt32(ProtocolVersion);
        writer.WriteUInt32(sessionId);
        writer.WriteBoolean(ok);
        writer.WriteString(reason);
        writer.WriteUInt32(ok ? snapshotHomePid : 0);
        writer.WriteInt32(ok ? snapshotEntries.Count : 0);
        if (ok)
            foreach (SnapshotEntry entry in snapshotEntries)
            {
                writer.WriteUInt32(entry.pid);
                writer.WriteInt32(entry.ordinal);
                writer.WriteInt32(entry.groupId);
                writer.WriteInt32(entry.groupSlot);
                writer.WriteInt32(entry.rawMode);
            }
        ClientRpcSender.SendTarget(this, RpcSnapshotIndex, writer, Channel.Reliable, target);
    }

    private void ClientHandleModeChanged(int protocol, uint sessionId, uint dronePid, int mode, bool ok, string reason)
    {
        if (protocol != ProtocolVersion)
        {
            ClearClientSnapshot();
            plugin?.ClientNotifyModeChanged(sessionId, dronePid, mode, false, "protocol_mismatch");
            return;
        }
        plugin?.ClientNotifyModeChanged(sessionId, dronePid, mode, ok, reason);
    }

    private void ClientHandleSnapshot(int protocol, uint sessionId, bool ok, string reason, uint snapshotHomePid,
                                      List<SnapshotEntry> snapshotEntries)
    {
        if (protocol != ProtocolVersion)
        {
            ClearClientSnapshot();
            plugin?.ClientNotifySnapshot(sessionId, "protocol_mismatch");
            return;
        }
        if (MultiplayerCleanupLogic.ShouldClearClientSnapshot(ok))
        {
            ClearClientSnapshot();
            plugin?.ClientNotifySnapshot(sessionId, reason);
            return;
        }
        if (sessionId == 0)
        {
            ClearClientSnapshot();
            plugin?.ClientNotifySnapshot(0, "invalid_snapshot_session");
            return;
        }

        ClearClientSnapshot();
        snapshotSessionId = sessionId;
        homePid = snapshotHomePid;
        foreach (SnapshotEntry entry in snapshotEntries)
        {
            orderedPids.Add(entry.pid);
            entries[entry.pid] = entry;
        }
        snapshotReady = true;
        plugin?.ClientNotifySnapshot(sessionId, "ok");
    }

    private void OnStartClient()
    {
        ClearClientSnapshot();
        if (!IsServer)
            RequestSnapshot();
    }

    private void OnStopClient() => ClearClientSnapshot();

    private void RequestSnapshot()
    {
        using var writer = NetworkWriterPool.GetWriter();
        writer.WriteInt32(ProtocolVersion);
        writer.WriteUInt32(0);
        ServerRpcSender.Send(this, CmdRequestSnapshotIndex, writer, Channel.Reliable, false);
    }

    internal uint SnapshotSessionId => snapshotSessionId;
    internal bool SnapshotReady => snapshotReady;
    internal bool ContainsSnapshotPid(uint pid) => snapshotReady && entries.ContainsKey(pid);

    internal bool TryGetSnapshotEntry(uint pid, out int ordinal, out int groupId, out int groupSlot, out int rawMode)
    {
        if (snapshotReady && entries.TryGetValue(pid, out SnapshotEntry entry))
        {
            ordinal = entry.ordinal;
            groupId = entry.groupId;
            groupSlot = entry.groupSlot;
            rawMode = entry.rawMode;
            return true;
        }
        ordinal = groupId = groupSlot = rawMode = -1;
        return false;
    }

    internal bool SnapshotModeKnown(uint pid) =>
        TryGetSnapshotEntry(pid, out _, out _, out _, out int rawMode) && IsKnownMode(rawMode);

    internal bool ResolveSnapshotRoster(List<Aircraft> drones, out Aircraft? home)
    {
        drones.Clear();
        home = snapshotReady ? ResolveAircraft(homePid) : null;
        if (!snapshotReady)
            return false;
        foreach (uint pid in orderedPids)
        {
            Aircraft? drone = ResolveAircraft(pid);
            if (drone != null)
                drones.Add(drone);
        }
        return true;
    }

    internal void ClearClientSnapshot()
    {
        snapshotReady = false;
        snapshotSessionId = 0;
        homePid = 0;
        orderedPids.Clear();
        entries.Clear();
    }

    private static Aircraft? ResolveAircraft(uint pid)
    {
        if (pid == 0 || !UnitRegistry.TryGetPersistentUnit(new PersistentID { Id = pid }, out PersistentUnit unit) ||
            unit == null)
            return null;
        return unit.unit as Aircraft;
    }

    private static bool IsKnownMode(int mode) => mode == (int)DroneModeOverride.Auto ||
                                                     mode == (int)DroneModeOverride.Follow ||
                                                     mode == (int)DroneModeOverride.Loiter;

    internal static void EnsureTemplate()
    {
        if (template != null)
            return;
        GameObject value = new GameObject("LoyalWingman.WingmanSessionTransport");
        value.SetActive(false);
        NetworkIdentity identity = value.AddComponent<NetworkIdentity>();
        value.AddComponent<WingmanSessionTransport>();
        identity.PrefabHash = TransportPrefabHash;
        UnityEngine.Object.DontDestroyOnLoad(value);
        template = value;
    }

    internal static void RegisterForManager(NetworkManagerNuclearOption manager)
    {
        EnsureTemplate();
        ClientObjectManager clientManager = manager.ClientObjectManager;
        if (registeredManagers.Contains(clientManager))
            clientManager.UnregisterSpawnHandler(TransportPrefabHash);
        clientManager.RegisterSpawnHandler(TransportPrefabHash, SpawnClient, UnspawnClient);
        registeredManagers.Add(clientManager);
    }

    private static NetworkIdentity SpawnClient(SpawnMessage message)
    {
        EnsureTemplate();
        GameObject value = UnityEngine.Object.Instantiate(template!);
        try
        {
            value.SetActive(true);
            return value.GetComponent<NetworkIdentity>();
        }
        catch
        {
            UnityEngine.Object.Destroy(value);
            throw;
        }
    }

    private static void UnspawnClient(NetworkIdentity identity)
    {
        WingmanSessionTransport? transport = identity.GetComponent<WingmanSessionTransport>();
        transport?.ClearClientSnapshot();
        if (transport != null && i == transport)
            i = null!;
        UnityEngine.Object.Destroy(identity.gameObject);
    }

    internal static bool EnsureSpawned(Plugin value)
    {
        plugin = value;
        EnsureTemplate();
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (spawned == null)
            spawnedManager = null;
        else if (spawned.Identity != null && spawned.Identity.IsSpawned && spawnedManager == manager)
            return true;
        else
            CleanupRouter();

        plugin = value;
        if (manager == null || !manager.Server.Active)
            return false;

        GameObject? clone = null;
        WingmanSessionTransport? transport = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(template!);
            clone.SetActive(true);
            NetworkIdentity identity = clone.GetComponent<NetworkIdentity>();
            transport = clone.GetComponent<WingmanSessionTransport>();
            manager.ServerObjectManager.Spawn(identity);
            spawned = transport;
            spawnedManager = manager;
            UnityEngine.Debug.Log("[LoyalWingman] event=router_spawned prefab_hash=" + TransportPrefabHash);
            return true;
        }
        catch
        {
            if (transport != null && i == transport)
                i = null!;
            if (clone != null)
                UnityEngine.Object.Destroy(clone);
            spawned = null;
            spawnedManager = null;
            return false;
        }
    }

    internal static void CleanupRouter()
    {
        WingmanSessionTransport? live = i;
        live?.ClearClientSnapshot();
        WingmanSessionTransport? transport = spawned;
        NetworkManagerNuclearOption? manager = spawnedManager;
        spawned = null;
        spawnedManager = null;
        plugin = null;
        if (transport == null)
            return;
        transport.ClearClientSnapshot();
        if (i == transport)
            i = null!;
        NetworkIdentity identity = transport.Identity;
        if (identity != null && identity.IsSpawned && manager != null && manager.Server.Active)
            manager.ServerObjectManager.Destroy(identity, true);
        else
            UnityEngine.Object.Destroy(transport.gameObject);
    }

    internal static void UnregisterAllManagers()
    {
        foreach (ClientObjectManager manager in registeredManagers)
            if (manager != null)
                manager.UnregisterSpawnHandler(TransportPrefabHash);
        registeredManagers.Clear();
        if (template == null)
            return;
        if (i == template.GetComponent<WingmanSessionTransport>())
            i = null!;
        UnityEngine.Object.Destroy(template);
        template = null;
    }
}

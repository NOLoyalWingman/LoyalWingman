using System;
using System.Collections.Generic;
using Mirage;
using NuclearOption.Networking;

namespace LoyalWingman;

internal sealed class SessionRegistry
{
    private readonly Plugin plugin;
    private readonly Func<bool> statusEnabled;
    private readonly Action<string> log;
    private readonly SessionRoutingIndex<INetworkPlayer> routing = new SessionRoutingIndex<INetworkPlayer>();
    private readonly Dictionary<uint, CarrierSession> sessions = new Dictionary<uint, CarrierSession>();
    private readonly Dictionary<Aircraft, CarrierSession> aircraftSessions =
        new Dictionary<Aircraft, CarrierSession>(ReferenceIdentityComparer<Aircraft>.Instance);

    internal SessionRegistry(Plugin plugin, Func<bool> statusEnabled, Action<string> log)
    {
        this.plugin = plugin;
        this.statusEnabled = statusEnabled;
        this.log = log;
    }

    internal int Count => sessions.Count;

    internal CarrierSession[] SnapshotSessions()
    {
        uint[] ids = routing.SnapshotSessionIds();
        CarrierSession[] snapshot = new CarrierSession[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            snapshot[i] = sessions[ids[i]];
        return snapshot;
    }

    internal bool TryGetOrCreate(Player player, Aircraft home, CarrierFqLoadoutPlan? authorizedPlan,
                                 out CarrierSession session, out bool created, out string reason)
    {
        INetworkPlayer owner = player.Owner;
        if (routing.TryGetByOwner(owner, out uint existingId))
        {
            if (!sessions.TryGetValue(existingId, out session!))
            {
                // A construction failure must not reserve an owner without a runtime session.
                routing.TryRemoveSession(existingId, out _);
                log("event=session_registry_orphan_removed session=" + existingId + " reason=missing_runtime_session");
                return TryGetOrCreate(player, home, authorizedPlan, out session, out created, out reason);
            }
            created = false;
            if (ReferenceEquals(session.Home, home))
            {
                reason = "";
                return true;
            }
            if (session.Home == null)
                return TrySetHome(session, home, out reason);
            reason = "session_carrier_mismatch";
            return false;
        }

        if (aircraftSessions.ContainsKey(home))
        {
            session = null!;
            created = false;
            reason = "aircraft_already_routed";
            return false;
        }

        if (!routing.TryGetOrCreate(owner, out uint sessionId, out created, out reason))
        {
            session = null!;
            return false;
        }

        session = null!;
        try
        {
            CarrierFqLoadoutPlan? loadoutPlan = authorizedPlan?.Copy();
            session = new CarrierSession(sessionId, owner, player, home, plugin, statusEnabled, log, loadoutPlan);
            sessions.Add(sessionId, session);
            aircraftSessions.Add(home, session);
            return true;
        }
        catch (Exception e)
        {
            if (session != null)
            {
                if (aircraftSessions.TryGetValue(home, out CarrierSession? routed) && ReferenceEquals(routed, session))
                    aircraftSessions.Remove(home);
                sessions.Remove(sessionId);
            }
            routing.TryRemoveSession(sessionId, out _);
            session = null!;
            created = false;
            reason = "session_create_failed";
            log("event=session_registry_create_failed session=" + sessionId + " reason=" + e.GetType().Name);
            return false;
        }
    }

    internal bool TryGetById(uint sessionId, out CarrierSession session) => sessions.TryGetValue(sessionId, out session!);
    internal bool TryGetByOwner(INetworkPlayer owner, out CarrierSession session)
    {
        if (routing.TryGetByOwner(owner, out uint sessionId) && sessions.TryGetValue(sessionId, out CarrierSession? found))
        {
            session = found;
            return true;
        }
        session = null!;
        return false;
    }
    internal bool TryGetByPlayer(Player player, out CarrierSession session) => TryGetByOwner(player.Owner, out session);
    internal bool TryGetByAircraft(Aircraft aircraft, out CarrierSession session) => aircraftSessions.TryGetValue(aircraft, out session!);

    internal bool TryGetDrone(CarrierSession session, uint pid, out Aircraft drone, out string reason)
    {
        drone = null!;
        if (!routing.TryResolveDrone(pid, out uint sourceSessionId))
        {
            reason = "unknown_drone";
            return false;
        }
        if (sourceSessionId != session.SessionId)
        {
            reason = "wrong_session";
            return false;
        }
        foreach (Aircraft candidate in session.Roster)
            if (candidate.persistentID.Id == pid)
            {
                if (!aircraftSessions.TryGetValue(candidate, out CarrierSession? routedSession) ||
                    !ReferenceEquals(routedSession, session))
                {
                    reason = "drone_route_missing";
                    return false;
                }
                drone = candidate;
                reason = "";
                return true;
            }
        reason = "drone_route_missing";
        return false;
    }

    internal bool TrySetHome(CarrierSession session, Aircraft home, out string reason)
    {
        if (!sessions.TryGetValue(session.SessionId, out CarrierSession? active) || !ReferenceEquals(active, session))
        {
            reason = "unknown_session";
            return false;
        }
        if (ReferenceEquals(session.Home, home)) { reason = ""; return true; }
        if (aircraftSessions.ContainsKey(home)) { reason = "aircraft_already_routed"; return false; }
        if (session.Home != null)
        {
            if (!aircraftSessions.TryGetValue(session.Home, out CarrierSession? routedHome) ||
                !ReferenceEquals(routedHome, session))
            {
                reason = "home_route_missing";
                return false;
            }
            aircraftSessions.Remove(session.Home);
        }
        aircraftSessions.Add(home, session);
        session.Home = home;
        reason = "";
        return true;
    }

    internal bool TryClearHome(CarrierSession session, Aircraft expectedHome)
    {
        if (!sessions.TryGetValue(session.SessionId, out CarrierSession? active) || !ReferenceEquals(active, session) ||
            !ReferenceEquals(session.Home, expectedHome) ||
            !aircraftSessions.TryGetValue(expectedHome, out CarrierSession? routedHome) ||
            !ReferenceEquals(routedHome, session))
            return false;

        aircraftSessions.Remove(expectedHome);
        session.Home = null;
        return true;
    }

    internal bool TryAttachDrone(CarrierSession session, Aircraft drone, out string reason)
    {
        uint pid = drone.persistentID.Id;
        if (!sessions.TryGetValue(session.SessionId, out CarrierSession? active) || !ReferenceEquals(active, session))
        { reason = "unknown_session"; return false; }
        if (pid == 0) { reason = "invalid_drone_pid"; return false; }
        if (aircraftSessions.ContainsKey(drone)) { reason = "aircraft_already_routed"; return false; }
        if (session.Roster.Contains(drone) || session.Manager.IsRegistered(drone)) { reason = "drone_already_attached"; return false; }
        LogicalPlacement placement = session.Placements.Preview();
        if (!routing.TryAttachDrone(session.SessionId, pid, out reason)) return false;
        try
        {
            aircraftSessions.Add(drone, session);
            session.Roster.Add(drone);
            if (!session.Manager.Register(drone, placement.Ordinal, placement.GroupId, placement.GroupSlot) ||
                !session.Manager.IsRegistered(drone))
            { reason = "manager_register_failed"; RollbackAttach(session, drone, pid); return false; }
            if (!session.Placements.TryCommit(placement))
            { reason = "placement_commit_failed"; RollbackAttach(session, drone, pid); return false; }
            reason = ""; return true;
        }
        catch (Exception)
        {
            RollbackAttach(session, drone, pid);
            reason = "manager_register_failed"; return false;
        }
    }
    private void RollbackAttach(CarrierSession session, Aircraft drone, uint pid)
    {
        try { session.Manager.Unregister(drone, "attach_failed"); }
        catch (Exception e) { session.Context.Log("event=attach_rollback_unregister_exception drone_pid=" + pid + " exception=" + e.GetType().Name); }
        if (session.Manager.IsRegistered(drone))
            session.Context.Log("event=attach_rollback_manager_membership_remaining drone_pid=" + pid);
        try { session.Roster.Remove(drone); } catch { }
        try { aircraftSessions.Remove(drone); } catch { }
        try { routing.TryDetachDrone(session.SessionId, pid, out _); } catch { }
    }
    internal bool TryDetachDrone(CarrierSession session, Aircraft drone, string callerReason, out string reason)
    {
        uint pid = drone.persistentID.Id;
        if (!aircraftSessions.TryGetValue(drone, out CarrierSession? source)) { reason = "aircraft_route_missing"; return false; }
        if (!ReferenceEquals(source, session)) { reason = "wrong_aircraft_session"; return false; }
        if (!routing.TryResolveDrone(pid, out uint sourceSessionId)) { reason = "drone_route_missing"; return false; }
        if (sourceSessionId != session.SessionId) { reason = "wrong_drone_session"; return false; }
        try
        {
            session.Manager.Unregister(drone, callerReason);
        }
        catch (Exception e)
        {
            reason = "manager_unregister_" + e.GetType().Name;
            return false;
        }
        if (session.Manager.IsRegistered(drone)) { reason = "manager_unregister_failed"; return false; }
        session.Roster.Remove(drone);
        aircraftSessions.Remove(drone);
        if (!routing.TryDetachDrone(session.SessionId, pid, out reason))
        {
            // The PID route remains reserved, so another owner cannot adopt this drone after a partial detach.
            reason = "drone_route_detach_failed";
            return false;
        }
        reason = "";
        return true;
    }

    internal bool TryRemove(CarrierSession session, out string reason)
    {
        if (!sessions.ContainsKey(session.SessionId)) { reason = "unknown_session"; return false; }
        if (session.Roster.Count != 0) { reason = "drones_attached"; return false; }
        if (session.Home != null && (!aircraftSessions.TryGetValue(session.Home, out CarrierSession? routedHome) ||
                                    !ReferenceEquals(routedHome, session)))
        {
            reason = "home_route_missing";
            return false;
        }
        if (!routing.TryRemoveSession(session.SessionId, out _)) { reason = "unknown_session"; return false; }
        if (session.Home != null)
            aircraftSessions.Remove(session.Home);
        sessions.Remove(session.SessionId);
        reason = "";
        return true;
    }
    // Terminal teardown only: Unity-destroyed keys must not retain routes into a later scene.
    internal bool ForceRemoveForTerminalTeardown(CarrierSession session)
    {
        if (!sessions.TryGetValue(session.SessionId, out CarrierSession? active) || !ReferenceEquals(active, session)) return false;
        List<Aircraft> stale = new List<Aircraft>();
        foreach (KeyValuePair<Aircraft, CarrierSession> pair in aircraftSessions)
            if (ReferenceEquals(pair.Value, session)) stale.Add(pair.Key);
        foreach (Aircraft aircraft in stale) aircraftSessions.Remove(aircraft);
        session.Home = null;
        session.Roster.Clear();
        routing.TryRemoveSession(session.SessionId, out _);
        sessions.Remove(session.SessionId);
        return true;
    }
}

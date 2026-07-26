using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LoyalWingman
{
    internal sealed class ReferenceIdentityComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceIdentityComparer<T> Instance = new ReferenceIdentityComparer<T>();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal sealed class SessionRoutingIndex<TOwner> where TOwner : class
    {
        private readonly Dictionary<TOwner, uint> owners =
            new Dictionary<TOwner, uint>(ReferenceIdentityComparer<TOwner>.Instance);
        private readonly Dictionary<uint, TOwner> sessions = new Dictionary<uint, TOwner>();
        private readonly Dictionary<uint, uint> droneSessions = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, HashSet<uint>> sessionDrones = new Dictionary<uint, HashSet<uint>>();
        private uint nextSessionId;
        private bool exhausted;

        internal SessionRoutingIndex(uint firstSessionId = 1)
        {
            if (firstSessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(firstSessionId));

            nextSessionId = firstSessionId;
        }

        internal int SessionCount => sessions.Count;

        internal bool TryGetOrCreate(TOwner owner, out uint sessionId, out bool created, out string reason)
        {
            if (owners.TryGetValue(owner, out sessionId))
            {
                created = false;
                reason = "";
                return true;
            }

            if (exhausted)
            {
                sessionId = 0;
                created = false;
                reason = "session_id_exhausted";
                return false;
            }

            sessionId = nextSessionId;
            if (sessionId == uint.MaxValue)
                exhausted = true;
            else
                nextSessionId++;

            owners.Add(owner, sessionId);
            sessions.Add(sessionId, owner);
            sessionDrones.Add(sessionId, new HashSet<uint>());
            created = true;
            reason = "";
            return true;
        }

        internal bool TryGetByOwner(TOwner owner, out uint sessionId) => owners.TryGetValue(owner, out sessionId);

        internal bool TryGetOwner(uint sessionId, out TOwner owner)
        {
            if (sessions.TryGetValue(sessionId, out TOwner? foundOwner))
            {
                owner = foundOwner;
                return true;
            }

            owner = null!;
            return false;
        }

        internal bool TryResolveDrone(uint dronePid, out uint sessionId) => droneSessions.TryGetValue(dronePid, out sessionId);

        internal bool TryAttachDrone(uint sessionId, uint dronePid, out string reason)
        {
            if (dronePid == 0)
            {
                reason = "invalid_drone_pid";
                return false;
            }

            if (!sessionDrones.TryGetValue(sessionId, out HashSet<uint>? drones))
            {
                reason = "unknown_session";
                return false;
            }

            if (droneSessions.ContainsKey(dronePid))
            {
                reason = "drone_already_attached";
                return false;
            }

            drones!.Add(dronePid);
            droneSessions.Add(dronePid, sessionId);
            reason = "";
            return true;
        }

        internal bool TryDetachDrone(uint sessionId, uint dronePid, out string reason)
        {
            if (!droneSessions.TryGetValue(dronePid, out uint sourceSessionId))
            {
                reason = "unknown_drone";
                return false;
            }

            if (sourceSessionId != sessionId)
            {
                reason = "wrong_session";
                return false;
            }

            sessionDrones[sessionId].Remove(dronePid);
            droneSessions.Remove(dronePid);
            reason = "";
            return true;
        }

        internal bool TryRemoveSession(uint sessionId, out TOwner owner)
        {
            if (!sessions.TryGetValue(sessionId, out TOwner? foundOwner))
            {
                owner = null!;
                return false;
            }

            owner = foundOwner;

            foreach (uint dronePid in sessionDrones[sessionId])
                droneSessions.Remove(dronePid);

            sessionDrones.Remove(sessionId);
            sessions.Remove(sessionId);
            owners.Remove(owner);
            return true;
        }

        internal uint[] SnapshotSessionIds()
        {
            uint[] snapshot = new uint[sessions.Count];
            sessions.Keys.CopyTo(snapshot, 0);
            Array.Sort(snapshot);
            return snapshot;
        }

        internal uint[] SnapshotDronePids(uint sessionId)
        {
            if (!sessionDrones.TryGetValue(sessionId, out HashSet<uint>? drones))
                return Array.Empty<uint>();

            uint[] snapshot = new uint[drones!.Count];
            drones.CopyTo(snapshot);
            Array.Sort(snapshot);
            return snapshot;
        }

        internal void Clear()
        {
            owners.Clear();
            sessions.Clear();
            droneSessions.Clear();
            sessionDrones.Clear();
        }
    }
}

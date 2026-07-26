using System.Collections;
using System.Collections.Generic;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

internal enum CarrierSessionState
{
    PreSwitch,
    Switching,
    Complete,
    Poisoned
}

internal sealed class SessionCollisionLease
{
    internal readonly List<SessionCollisionPair> Pairs = new List<SessionCollisionPair>();
    internal Coroutine? Coroutine;
}

internal struct SessionCollisionPair
{
    internal Collider A, B;

    internal SessionCollisionPair(Collider a, Collider b)
    {
        A = a;
        B = b;
    }
}

internal sealed class CarrierSession
{
    internal readonly uint SessionId;
    internal readonly INetworkPlayer Owner;
    internal readonly Player Player;
    internal Aircraft? Home;
    internal readonly List<Aircraft> Roster = new List<Aircraft>();
    internal readonly WingmanSessionContext Context;
    internal readonly WingmanAiManager Manager;
    internal readonly CarrierCruiseController Cruise;
    internal readonly WingmanStatusReporter Reporter;
    internal readonly LogicalPlacementAllocator Placements = new LogicalPlacementAllocator();
    internal CarrierFqLoadoutPlan? LoadoutPlan;
    internal CarrierSessionState State = CarrierSessionState.PreSwitch;
    internal bool Busy;
    internal bool HomeReturningToBase;
    internal bool OwnerAircraftGap;
    internal int MapCurrentGroupId;
    internal float NextSwitchTime;
    internal Coroutine? SwitchCoroutine;
    internal ulong SwitchGeneration;
    internal bool ReleasePending;
    internal Coroutine? ReleaseCoroutine;
    internal ulong ReleaseGeneration;
    internal readonly HashSet<Aircraft> SubscribedDrones = new HashSet<Aircraft>();
    internal readonly List<SessionCollisionLease> CollisionLeases = new List<SessionCollisionLease>();

    internal CarrierSession(uint sessionId, INetworkPlayer owner, Player player, Aircraft home, Plugin plugin,
                            System.Func<bool> statusEnabled, System.Action<string> log,
                            CarrierFqLoadoutPlan? loadoutPlan)
    {
        SessionId = sessionId;
        Owner = owner;
        Player = player;
        Home = home;
        LoadoutPlan = loadoutPlan;
        Context = new WingmanSessionContext(plugin, this, log);
        Reporter = new WingmanStatusReporter(statusEnabled, Context.Log);
        Manager = new WingmanAiManager(Context, Reporter);
        Cruise = new CarrierCruiseController(Context, Reporter);
    }
}

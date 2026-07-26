using System;

namespace LoyalWingman;

internal sealed class WingmanSessionContext
{
    private readonly Plugin plugin;
    internal readonly CarrierSession Session;
    internal Action<string> Log { get; }

    internal WingmanSessionContext(Plugin plugin, CarrierSession session, Action<string> log)
    {
        this.plugin = plugin;
        Session = session;
        Log = text => log("session=" + session.SessionId + " owner=" + OwnerLabel + " " + text);
    }

    internal bool CombatEnabled => plugin.CombatEnabled;
    internal string OwnerLabel => Session.Owner.ToString() ?? "unavailable";
    internal bool CanReconcile() => plugin.SessionCanReconcile(Session);
    internal bool HasPlayerAssociation(Aircraft aircraft) => plugin.SessionHasPlayerAssociation(Session, aircraft);
    internal bool CanFallback(Aircraft drone) => plugin.SessionCanFallback(Session, drone);
    internal bool EligibleForInstall(Aircraft drone, out string reason) =>
        plugin.SessionEligibleForInstall(Session, drone, out reason);
    internal bool RuntimeEligible(Aircraft drone, Aircraft leader, out string reason) =>
        plugin.SessionRuntimeEligible(Session, drone, leader, out reason);
    internal bool TryGetLeader(out Aircraft leader) => plugin.TryGetAuthoritativeSessionLeader(Session, out leader);
    internal bool TryGetHq(out FactionHQ hq, out string reason) => plugin.TryGetSessionCommandHq(Session, out hq, out reason);
}

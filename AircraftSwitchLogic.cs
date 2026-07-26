namespace LoyalWingman;

internal enum EmergencyReturnOutcome { DetachOld, CompleteTarget, FatalPartial }

internal static class AircraftSwitchLogic
{
    internal static bool NeedsLocalSimTransition(bool current, bool desired) => current != desired;
    internal static bool CanQueueEmergencyReturn(bool requestAndSessionAvailable, bool managedCurrentSource,
                                                 bool authoritativeSpawnedSource, bool validExactHome) =>
        requestAndSessionAvailable && managedCurrentSource && authoritativeSpawnedSource && validExactHome;
    internal static bool OwnsEmergencyReturnRequest(bool pendingRequestMatches, bool sessionActive,
                                                    bool generationMatches) =>
        pendingRequestMatches && sessionActive && generationMatches;
    internal static EmergencyReturnOutcome ResolveEmergencyReturnOutcome(bool oldOwned, bool targetOwned) =>
        oldOwned != targetOwned
            ? oldOwned ? EmergencyReturnOutcome.DetachOld : EmergencyReturnOutcome.CompleteTarget
            : EmergencyReturnOutcome.FatalPartial;
    internal static bool ShouldInstallCarrierCruise(bool departingSessionHome, bool targetIsConfiguredDrone) =>
        departingSessionHome && targetIsConfiguredDrone;
}

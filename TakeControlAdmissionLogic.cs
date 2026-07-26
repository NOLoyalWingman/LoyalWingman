namespace LoyalWingman;

internal static class TakeControlAdmissionLogic
{
    // validTarget is true for any valid target, including a non-returning Home aircraft.
    internal static bool CanTakeControl(bool singleSelection, bool validTarget,
                                         bool targetIsCurrent, bool remoteModeOnly) =>
        singleSelection && validTarget && !targetIsCurrent && !remoteModeOnly;
}

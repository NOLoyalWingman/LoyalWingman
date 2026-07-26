namespace LoyalWingman;

internal enum DroneCarrierPortState { Empty, Loaded, Extending, NativeBypass, AcceptedFiring, PlayerReserved, PlayerHandoff }

internal enum AnimatedPortMotion { None, ExtendingRelease, ExtendingCapture, WaitingCapture, LockingCapture, RetractingRelease, RetractingCapture }

internal enum AnimatedProviderBindDecision { Legacy, Animated, Inert }
internal enum AnimatedCaptureRecoveryDecision { WaitForCapture, BeginLock, AbortRange, AbortTimeout }

internal static class DroneCarrierPortLogic
{
    internal const float AnimatedCapturePreparationRadius = 30f;
    internal const float AnimatedCaptureRadius = 5f;
    internal const float AnimatedCapturePreparationTimeoutSeconds = 20f;

    internal static bool TryBegin(ref DroneCarrierPortState state, bool authority)
    { if (state != DroneCarrierPortState.Loaded || !authority) return false; state = DroneCarrierPortState.Extending; return true; }
    internal static bool TryComplete(ref DroneCarrierPortState state, bool authority)
    { if (state != DroneCarrierPortState.Extending) return false; state = authority ? DroneCarrierPortState.NativeBypass : DroneCarrierPortState.Loaded; return authority; }
    internal static bool TryConsumeBypass(ref DroneCarrierPortState state, bool authority)
    { if (state != DroneCarrierPortState.NativeBypass) return false; if (!authority) { state = DroneCarrierPortState.Loaded; return false; } state = DroneCarrierPortState.AcceptedFiring; return true; }
    internal static bool TryConsumeAcceptedFire(ref DroneCarrierPortState state)
    { if (state != DroneCarrierPortState.AcceptedFiring) return false; state = DroneCarrierPortState.Empty; return true; }
    internal static void Cancel(ref DroneCarrierPortState state, bool hasDescriptor) =>
        state = hasDescriptor ? DroneCarrierPortState.Loaded : DroneCarrierPortState.Empty;
    internal static void Reset(ref DroneCarrierPortState state) => state = DroneCarrierPortState.Empty;
    internal static bool TryBeginPlayerHandoff(ref DroneCarrierPortState state)
    { if (state != DroneCarrierPortState.PlayerReserved) return false; state = DroneCarrierPortState.PlayerHandoff; return true; }
    internal static bool TryReservePlayerRecovery(ref DroneCarrierPortState state)
    { if (state != DroneCarrierPortState.Empty) return false; state = DroneCarrierPortState.PlayerReserved; return true; }
    internal static bool TryCommitPlayerHandoff(ref DroneCarrierPortState state)
    { if (state != DroneCarrierPortState.PlayerHandoff) return false; state = DroneCarrierPortState.Loaded; return true; }
    internal static bool CanBeginPlayerRecovery(bool server, bool session, bool authority, bool route, bool home, bool ready,
                                                bool trigger, bool projection, bool unreserved) =>
        server && session && authority && route && home && ready && trigger && projection && unreserved;
    internal static bool ShouldProtectPendingPlayerRecovery(bool pending) => pending;
    internal static bool CanCapture(bool server, bool empty, bool kestrel, bool alive, bool player, bool hq, bool trigger,
                                    bool conflict, bool carrierAuthority, bool candidateAuthority, bool projection) =>
        server && empty && kestrel && alive && !player && hq && trigger && !conflict && carrierAuthority && candidateAuthority && projection;
    internal static bool ShouldSuppressReleaseCapture(bool binding, bool exactExcluded) => binding || exactExcluded;
    internal static bool ShouldClearReleaseExclusion(bool validCandidate, bool occupied, bool geometryOverlap) =>
        !validCandidate || (!occupied && !geometryOverlap);
    internal static bool CanDestroyAfterReleaseRollback(bool attached, bool detached) => !attached || detached;
    internal static bool IsPureLegacyReleaseEligible(bool hasAnimatedProvider, bool providerOwnedButInert) =>
        !hasAnimatedProvider && !providerOwnedButInert;
    internal static AnimatedProviderBindDecision ResolveAnimatedProviderBind(int expectedProviderCount, bool expectedProviderValid,
                                                                               int additionalValidClaims)
    {
        if (expectedProviderCount == 0) return AnimatedProviderBindDecision.Legacy;
        return expectedProviderCount == 1 && expectedProviderValid && additionalValidClaims == 0
            ? AnimatedProviderBindDecision.Animated
            : AnimatedProviderBindDecision.Inert;
    }
    internal static bool AcceptAnimatedPortMilestone(long currentToken, long callbackToken, AnimatedPortMotion motion,
                                                      AnimatedPortMotion expectedMotion, string milestone, string expectedMilestone) =>
        currentToken != 0 && currentToken == callbackToken && motion == expectedMotion && milestone == expectedMilestone;
    internal static bool IsWithinAnimatedCapturePreparationRadiusSquared(float squaredDistance) =>
        squaredDistance <= AnimatedCapturePreparationRadius * AnimatedCapturePreparationRadius;
    internal static bool IsWithinAnimatedCaptureRadiusSquared(float squaredDistance) =>
        squaredDistance <= AnimatedCaptureRadius * AnimatedCaptureRadius;
    internal static float NormalizeAnimatedCaptureSquaredDistance(float rawSquaredDistance, bool preparationTriggerOccupied) =>
        preparationTriggerOccupied && rawSquaredDistance > AnimatedCapturePreparationRadius * AnimatedCapturePreparationRadius
            ? AnimatedCapturePreparationRadius * AnimatedCapturePreparationRadius : rawSquaredDistance;
    internal static AnimatedCaptureRecoveryDecision ResolveAnimatedCaptureRecovery(float squaredDistance, float elapsedSeconds,
                                                                                    bool preparationComplete)
    {
        if (!preparationComplete) return AnimatedCaptureRecoveryDecision.WaitForCapture;
        if (!IsWithinAnimatedCapturePreparationRadiusSquared(squaredDistance)) return AnimatedCaptureRecoveryDecision.AbortRange;
        if (elapsedSeconds >= AnimatedCapturePreparationTimeoutSeconds) return AnimatedCaptureRecoveryDecision.AbortTimeout;
        return IsWithinAnimatedCaptureRadiusSquared(squaredDistance)
            ? AnimatedCaptureRecoveryDecision.BeginLock
            : AnimatedCaptureRecoveryDecision.WaitForCapture;
    }
    internal static bool TryEnterAnimatedCaptureWait(long currentToken, long callbackToken, ref AnimatedPortMotion motion,
                                                      string milestone) =>
        TryAdvanceAnimatedCaptureMotion(currentToken, callbackToken, ref motion, AnimatedPortMotion.ExtendingCapture,
                                        AnimatedPortMotion.WaitingCapture, milestone, "Extended");
    internal static bool TryBeginAnimatedCaptureLock(long currentToken, long callbackToken, ref AnimatedPortMotion motion) =>
        TryAdvanceAnimatedCaptureMotion(currentToken, callbackToken, ref motion, AnimatedPortMotion.WaitingCapture,
                                        AnimatedPortMotion.LockingCapture, "", "");
    private static bool TryAdvanceAnimatedCaptureMotion(long currentToken, long callbackToken, ref AnimatedPortMotion motion,
                                                         AnimatedPortMotion expectedMotion, AnimatedPortMotion nextMotion,
                                                         string milestone, string expectedMilestone)
    {
        if (!AcceptAnimatedPortMilestone(currentToken, callbackToken, motion, expectedMotion, milestone, expectedMilestone)) return false;
        motion = nextMotion;
        return true;
    }
    internal static bool TryConsumeAnimatedPortOnce(ref bool consumed) { if (consumed) return false; consumed = true; return true; }
    internal static bool CloseAnimatedPortFullyStowed(long currentToken, long callbackToken, AnimatedPortMotion motion, string milestone) =>
        currentToken != 0 && currentToken == callbackToken &&
        (motion == AnimatedPortMotion.RetractingRelease || motion == AnimatedPortMotion.RetractingCapture) && milestone == "FullyStowed";
}

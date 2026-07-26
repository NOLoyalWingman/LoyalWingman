using System;

namespace LoyalWingman;

internal enum StrikeWeaponStatus { Missing, Exhausted, Waiting, Armed }
internal enum AntiShipWeaponKind { None, Agm, Ashm, LaserRocket }
internal enum AntiShipAttackStage { AgmVolley, LaserVolley, AshmVolley, SupportOrComplete }
internal enum LaserVolleyDisposition { Wait, Support, Release }

internal static class StrikeMissionLogic
{
    internal const float NearShotBreakoffSeconds = 5f;
    internal const float CollisionBreakoffSeconds = 2f;
    internal static bool ShouldRetreat(float distance, float minRange) => distance < minRange;
    internal static bool ShouldBreakOffAfterShot(float distance, float minRange) => distance < minRange * 2f;
    internal static float RetreatDistance(float turningRadius, float minRange) => MathF.Max(turningRadius * 2f, minRange * 2f);
    internal static bool ShouldExitRetreat(float verticalSeparation, float turningRadius, float distance, float retreatDistance) => verticalSeparation > turningRadius || distance > retreatDistance;
    internal static StrikeWeaponStatus ResolveWeaponStatus(bool compatible, bool ammo, bool unavailable) => !compatible ? StrikeWeaponStatus.Missing : ammo ? StrikeWeaponStatus.Armed : unavailable ? StrikeWeaponStatus.Waiting : StrikeWeaponStatus.Exhausted;
    internal static bool ShouldCompleteWeaponCheck(bool maneuver, StrikeWeaponStatus status) => !maneuver && (status == StrikeWeaponStatus.Missing || status == StrikeWeaponStatus.Exhausted);
    internal static AntiShipWeaponKind ResolveAntiShipWeaponKind(bool cruise, bool optical, bool lineOfSight, bool laserGuided, bool laserSeeker)
    {
        bool ordinaryOptical = optical && !cruise, ashm = cruise || ordinaryOptical && !lineOfSight, agm = ordinaryOptical && lineOfSight, laser = laserGuided && laserSeeker;
        return (ashm ? 1 : 0) + (agm ? 1 : 0) + (laser ? 1 : 0) != 1 ? AntiShipWeaponKind.None : agm ? AntiShipWeaponKind.Agm : ashm ? AntiShipWeaponKind.Ashm : AntiShipWeaponKind.LaserRocket;
    }
    internal static float AntiShipLaunchRange(float nativeMax) => float.IsFinite(nativeMax) && nativeMax > 0f ? nativeMax : 0f;
    internal static bool WithinAntiShipEnvelope(float distance, float minRange, float nativeMax, float angle, float minAlignment, float ownerSpeed, float minOwnerSpeed) => float.IsFinite(distance) && float.IsFinite(minRange) && float.IsFinite(angle) && float.IsFinite(minAlignment) && float.IsFinite(ownerSpeed) && float.IsFinite(minOwnerSpeed) && distance >= minRange && distance < AntiShipLaunchRange(nativeMax) && angle < minAlignment && ownerSpeed >= minOwnerSpeed;
    internal static AntiShipAttackStage NextAntiShipStage(AntiShipAttackStage stage) => stage switch { AntiShipAttackStage.AgmVolley => AntiShipAttackStage.LaserVolley, AntiShipAttackStage.LaserVolley => AntiShipAttackStage.AshmVolley, _ => AntiShipAttackStage.SupportOrComplete };
    internal static AntiShipAttackStage AdvanceEmptyAntiShipStage(AntiShipAttackStage stage, bool stageHasAmmo, int laserAmmo, bool laserPending, int activeLasers) => stage == AntiShipAttackStage.LaserVolley ? ResolveLaserVolley(laserAmmo, laserPending, activeLasers) == LaserVolleyDisposition.Wait ? stage : NextAntiShipStage(stage) : stage != AntiShipAttackStage.SupportOrComplete && !stageHasAmmo ? NextAntiShipStage(stage) : stage;
    internal static bool CanFireLaserVolley(bool lased, bool pending, float distance, float maxRange, float angle, float alignment) => lased && !pending && WithinAntiShipEnvelope(distance, 0f, maxRange, angle, alignment, 0f, 0f);
    internal static bool LaserSupportOwns(bool pending, int active) => pending || active > 0;
    internal static LaserVolleyDisposition ResolveLaserVolley(int ammo, bool pending, int active) => ammo > 0 || pending ? LaserVolleyDisposition.Wait : active > 0 ? LaserVolleyDisposition.Support : LaserVolleyDisposition.Release;
    internal static float AntiShipProfileHeight(float targetRange) => targetRange > 20000f ? 50f : 5f;
}

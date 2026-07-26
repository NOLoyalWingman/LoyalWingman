using System;

namespace LoyalWingman;

internal enum CasWeaponKind { None, Agm, LaserRocket, FixedGun }
internal enum CasAttackStage { AgmVolley, LaserVolley, FixedGunVolley, SupportOrComplete }

internal static class CasMissionLogic
{
    internal const float SearchRadiusMeters = 5000f;

    internal static bool IsCasTargetCategory(bool isGroundVehicle, bool isBuilding, bool isAircraft,
                                             bool aircraftAirborne) =>
        isGroundVehicle || isBuilding || isAircraft && !aircraftAirborne;

    internal static bool IsWithinSearchRadius(float centerX, float centerZ, float candidateX, float candidateZ)
    {
        if (!Finite(centerX) || !Finite(centerZ) || !Finite(candidateX) || !Finite(candidateZ))
            return false;
        float dx = candidateX - centerX, dz = candidateZ - centerZ;
        float distanceSq = dx * dx + dz * dz;
        return Finite(distanceSq) && distanceSq <= SearchRadiusMeters * SearchRadiusMeters;
    }

    internal static bool IsBetterCandidate(float candidateDistanceSq, string candidatePid, float bestDistanceSq,
                                           string bestPid)
    {
        if (!Finite(candidateDistanceSq) || candidateDistanceSq < 0f)
            return false;
        if (!Finite(bestDistanceSq) || bestDistanceSq < 0f)
            return true;
        return candidateDistanceSq < bestDistanceSq ||
               candidateDistanceSq == bestDistanceSq && string.CompareOrdinal(candidatePid, bestPid) < 0;
    }

    internal static CasAttackStage Advance(CasAttackStage stage, bool stageHasAmmo,
                                           bool stageReloading, bool stageSalvoInProgress,
                                           bool laserPending) =>
        stage == CasAttackStage.AgmVolley ? stageHasAmmo || stageReloading || stageSalvoInProgress ? stage : CasAttackStage.LaserVolley :
        stage == CasAttackStage.LaserVolley ? stageHasAmmo || stageReloading || stageSalvoInProgress || laserPending ? stage : CasAttackStage.FixedGunVolley :
        stage == CasAttackStage.FixedGunVolley && !stageHasAmmo && !stageReloading && !stageSalvoInProgress ? CasAttackStage.SupportOrComplete : stage;

    internal static bool FixedGunRequestAllowed(bool reloading, bool salvoInProgress, bool ready,
                                                bool safetyIsOn, bool casStationSafe) =>
        !reloading && !salvoInProgress && ready && !safetyIsOn && casStationSafe;

    internal static CasWeaponKind Classify(bool missile, bool gun, bool boresight, bool bomb, bool glideBomb,
        bool cargo, bool sling, bool troops, bool ir, bool arh, bool arm, bool optical, bool opticalCruise,
        bool lineOfSight, bool laserGuided, bool laserSeeker, float antiSurface, int turretCount)
    {
        if (bomb || glideBomb || cargo || sling || troops)
            return CasWeaponKind.None;
        if (gun && boresight)
            return CasWeaponKind.FixedGun;
        if (!Finite(antiSurface) || antiSurface <= 0f)
            return CasWeaponKind.None;
        if (missile && !gun && optical && !opticalCruise && lineOfSight && !laserGuided && !laserSeeker &&
            !arm && !ir && !arh)
            return CasWeaponKind.Agm;
        if (!gun && laserGuided && laserSeeker && !arm && !optical && !opticalCruise && !ir && !arh)
            return CasWeaponKind.LaserRocket;
        return CasWeaponKind.None;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

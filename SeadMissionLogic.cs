namespace LoyalWingman;

internal enum SeadWeaponKind { None, Arm, Agm, LaserRocket }
internal enum SeadAttackStage { ArmVolley, AgmVolley, LaserVolley, SupportOrComplete }

internal static class SeadMissionLogic
{
    internal static SeadWeaponKind Classify(bool missile, bool gun, bool bomb, bool glideBomb, bool cargo, bool sling,
                                            bool troops, bool irSeeker, bool arhSeeker, bool armSeeker, bool opticalSeeker,
                                            bool lineOfSight, bool cruise, bool laserGuided, bool laserSeeker) =>
        !missile || gun || bomb || glideBomb || cargo || sling || troops || irSeeker || arhSeeker ? SeadWeaponKind.None :
        armSeeker && !opticalSeeker && !cruise && !laserGuided && !laserSeeker ? SeadWeaponKind.Arm :
        opticalSeeker && lineOfSight && !cruise && !armSeeker && !laserGuided && !laserSeeker ? SeadWeaponKind.Agm :
        laserGuided && laserSeeker && !armSeeker && !opticalSeeker && !cruise ? SeadWeaponKind.LaserRocket : SeadWeaponKind.None;
    internal static SeadAttackStage Next(SeadAttackStage stage) => stage switch
    {
        SeadAttackStage.ArmVolley => SeadAttackStage.AgmVolley,
        SeadAttackStage.AgmVolley => SeadAttackStage.LaserVolley,
        _ => SeadAttackStage.SupportOrComplete
    };
    internal static SeadAttackStage Advance(SeadAttackStage stage, bool radarActive, bool stageHasAmmo, int laserAmmo,
                                            bool laserPending)
    {
        if (stage == SeadAttackStage.ArmVolley && (!radarActive || !stageHasAmmo)) return Next(stage);
        if (stage == SeadAttackStage.AgmVolley && !stageHasAmmo) return Next(stage);
        if (stage == SeadAttackStage.LaserVolley && laserAmmo <= 0 && !laserPending) return Next(stage);
        return stage;
    }
    internal static bool LaserSupportOwns(bool pending, int activeCount) => pending || activeCount > 0;
}

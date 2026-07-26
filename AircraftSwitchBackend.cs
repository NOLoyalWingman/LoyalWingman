// Aircraft switching logic adapted from BOTE 1.4.1 by MinecrackTyler.
using NuclearOption.Networking;
using UnityEngine;

namespace LoyalWingman;

// TODO: Add NetworkBehaviour/RPC transport if multiplayer switching is requested.
internal sealed class AircraftSwitchBackend
{
    private readonly System.Action<string> log;
    private readonly System.Func<long> fixedSequence;

    internal AircraftSwitchBackend(System.Action<string> log, System.Func<long> fixedSequence)
    {
        this.log = log;
        this.fixedSequence = fixedSequence;
    }
    public bool TryReady(out string reason)
    {
        reason = "";
        if (GameManager.gameState != GameState.SinglePlayer || NetworkManagerNuclearOption.i == null ||
            !NetworkManagerNuclearOption.i.Server.Active)
        {
            reason = "not_singleplayer_host";
            return false;
        }
        return true;
    }

    public void Switch(Player player, Aircraft oldAircraft, Aircraft newAircraft)
    {
        if ((int)GameManager.gameState == 1)
            UserCode_RpcSwitchAircraft_903200167(player, oldAircraft, newAircraft);
    }

    public void InitializeTargetCamAfterVerifiedSwitch(Aircraft aircraft)
    {
        // The switch path destroys the old local camera; initialize the target camera only after ownership verification.
        TargetCam targetCam = aircraft.targetCam;
        if (targetCam != null)
            targetCam.Initialize();
    }

    public bool TryRefreshWeaponStatusAfterVerifiedSwitch(Aircraft aircraft, out int stationIndex)
    {
        stationIndex = -1;
        WeaponManager? weaponManager = aircraft.weaponManager;
        WeaponStation? station = weaponManager?.currentWeaponStation;
        CombatHUD? combatHud = SceneSingleton<CombatHUD>.i;
        if (station == null || combatHud == null) return false;
        combatHud.ShowWeaponStation(station);
        stationIndex = station.Number;
        return true;
    }

    internal void LogJointSnapshot(Aircraft aircraft, string phase)
    {
        int parts = -1, fixedJoints = -1;
        TryCountFixedJoints(aircraft, out parts, out fixedJoints);
        TryLog("state=localsim_joint_snapshot phase=" + phase + " key=" + KeyOf(aircraft) + " pid=" + PidOf(aircraft) +
               " frame=" + Time.frameCount + " fixed_time=" + Time.fixedTime + " fixed_seq=" + FixedSequence() +
               " parts=" + parts + " fixed_joints=" + fixedJoints);
    }

    private void UserCode_RpcSwitchAircraft_903200167(Player player, Aircraft oldAircraft, Aircraft newAircraft)
    {
        if ((int)GameManager.gameState == 1 && oldAircraft != null && newAircraft != null &&
            player.Identity.Owner == oldAircraft.Identity.Owner && newAircraft.Player == null &&
            newAircraft.NetworkHQ == player.HQ)
        {
            oldAircraft.playerRef = default;
            player.RemoveAircraft(oldAircraft);
            player.RemoveAircraftAuthority(oldAircraft);
            if (oldAircraft.autopilot != null)
            {
                oldAircraft.pilots[0].SetStartingAiState();
            }
            else
            {
                oldAircraft.pilots[0].SwitchState(oldAircraft.pilots[0].parkedState);
            }
            oldAircraft.pilots[0].player = null;
            GuardedSetLocalSim(oldAircraft, "old_release");
            if (oldAircraft.LocalSim)
            {
                oldAircraft.partChecker = new Aircraft.PartChecker(oldAircraft);
            }
            oldAircraft.weaponManager.ClearTargetList();
            if (UnitRegistry.TryGetPersistentUnit(oldAircraft.persistentID, out PersistentUnit persistentUnit))
            {
                persistentUnit.player = null;
            }
            newAircraft.playerRef = new PlayerRef(player);
            newAircraft.Identity.AssignClientAuthority(player.Owner);
            GuardedSetLocalSim(newAircraft, "target_acquire");
            player.SetAircraft(newAircraft);
            newAircraft.pilots[0].SwitchState(null);
            oldAircraft.pilots[0].aircraft = oldAircraft;
            UserCode_CmdSwitchAircraft_1990430116(player, oldAircraft, newAircraft);
        }
    }

    private void UserCode_CmdSwitchAircraft_1990430116(Player player, Aircraft oldAircraft, Aircraft newAircraft)
    {
        if (newAircraft!.LocalSim)
        {
            newAircraft.partChecker = new Aircraft.PartChecker(newAircraft);
        }
        if (GameManager.IsLocalPlayer<Player>(player))
        {
            if (oldAircraft != null)
            {
                if (oldAircraft.statusDisplay != null)
                {
                    oldAircraft.onDisableUnit -= oldAircraft.statusDisplay.StatusDisplay_OnDisable;
                    Object.Destroy(oldAircraft.statusDisplay.gameObject);
                }
                oldAircraft.weaponManager.currentWeaponStation.SetStationActive(oldAircraft, false);
                TargetCam targetCam = oldAircraft.targetCam;
                if (targetCam != null)
                {
                    targetCam.enabled = false;
                    targetCam.OnDestroy();
                    if (oldAircraft.cockpit != null)
                        oldAircraft.cockpit.onParentDetached -= targetCam.TargetCam_OnDetach;
                    foreach (Transform item in targetCam.GetCamMount().transform)
                    {
                        Object.Destroy(item.gameObject);
                    }
                }
                oldAircraft.onDisableUnit -= SceneSingleton<CombatHUD>.i.threatList.ThreatList_OnAircraftDisable;
                SceneSingleton<CombatHUD>.i.threatList.ThreatList_OnAircraftDisable(oldAircraft);
                if (SceneSingleton<HUDAppManager>.i != null && SceneSingleton<MFDAppManager>.i != null)
                {
                    oldAircraft.onDisableUnit -= SceneSingleton<HUDAppManager>.i.HUDAppManager_OnUnitDisable;
                    oldAircraft.onDisableUnit -= SceneSingleton<MFDAppManager>.i.HUDAppManager_OnUnitDisable;
                    PlayerSettings.OnApplyOptions -= SceneSingleton<HUDAppManager>.i.RefreshSettings;
                    PlayerSettings.OnApplyOptions -= SceneSingleton<MFDAppManager>.i.RefreshSettings;
                    Object.Destroy(SceneSingleton<HUDAppManager>.i.gameObject);
                    Object.Destroy(SceneSingleton<MFDAppManager>.i.gameObject);
                }
                UnitPart? cockpit = oldAircraft.cockpit;
                Cockpit? oldCockpit = cockpit != null ? cockpit.GetComponentInChildren<Cockpit>() : null;
                if (oldCockpit != null)
                {
                    oldCockpit.enabled = false;
                    oldAircraft.onDisableUnit -= oldCockpit.Cockpit_OnAircraftDisable;
                    Object.Destroy(oldCockpit.tacScreen);
                }
            }
            SceneSingleton<CombatHUD>.i.RemoveAircraft();
            SceneSingleton<CombatHUD>.i.SetAircraft(newAircraft);
            SceneSingleton<DynamicMap>.i.DeselectAllIcons();
            if (!(newAircraft != null))
            {
                return;
            }
            newAircraft.weaponManager.currentWeaponStation.SetStationActive(newAircraft, true);
            SceneSingleton<CombatHUD>.i.weaponStatus.UpdateDisplay(newAircraft.weaponManager.currentWeaponStation);
            foreach (Missile knownMissile in newAircraft.GetMissileWarningSystem().knownMissiles)
            {
                SceneSingleton<CombatHUD>.i.threatList.ThreatList_OnMissileWarning(new MissileWarning.OnMissileWarning
                {
                    missile = knownMissile
                });
            }
            newAircraft.pilots[0].SwitchState(newAircraft.pilots[0].playerState);
            newAircraft.SetupLocalPlayerAndUI();
            UnitPart newCockpitPart = newAircraft.cockpit;
            Cockpit? newCockpit =
                newCockpitPart != null ? newCockpitPart.GetComponentInChildren<Cockpit>() : null;
            if (newCockpit != null)
            {
                newCockpit.Cockpit_OnAircraftInitialize();
            }
            newAircraft.weaponManager.ClearTargetList();
        }
        else if (oldAircraft != null)
        {
            GuardedSetLocalSim(oldAircraft, "old_remote_cmd");
        }
    }

    private void GuardedSetLocalSim(Aircraft aircraft, string source)
    {
        bool oldLocal = aircraft.LocalSim, oldRemote = aircraft.remoteSim;
        bool desiredLocal = aircraft.CheckIfLocalSim(), desiredRemote = !desiredLocal;
        int parts = -1, fixedJointsBefore = -1, fixedJointsAfter = -1;
        TryCountFixedJoints(aircraft, out parts, out fixedJointsBefore);
        bool transition = AircraftSwitchLogic.NeedsLocalSimTransition(oldLocal, desiredLocal);
        try
        {
            if (transition) aircraft.SetLocalSim(desiredLocal);
        }
        finally
        {
            bool newLocal = oldLocal, newRemote = oldRemote;
            try { newLocal = aircraft.LocalSim; newRemote = aircraft.remoteSim; } catch { }
            if (TryCountFixedJoints(aircraft, out int afterParts, out fixedJointsAfter)) parts = afterParts;
            else parts = -1;
            TryLog("state=localsim_decision source=" + source + " action=" + (transition ? "transition" : "skip") +
                   " key=" + KeyOf(aircraft) + " pid=" + PidOf(aircraft) + " old_local=" + oldLocal +
                   " desired_local=" + desiredLocal + " new_local=" + newLocal + " old_remote=" + oldRemote +
                   " desired_remote=" + desiredRemote + " new_remote=" + newRemote + " frame=" + Time.frameCount +
                   " fixed_time=" + Time.fixedTime + " fixed_seq=" + FixedSequence() + " parts=" + parts +
                   " fixed_joints_before=" + fixedJointsBefore + " fixed_joints_after=" + fixedJointsAfter);
        }
    }

    private static bool TryCountFixedJoints(Aircraft aircraft, out int parts, out int fixedJoints)
    {
        parts = fixedJoints = -1;
        try
        {
            int partCount = 0, jointCount = 0;
            bool rootIncluded = false;
            foreach (UnitPart part in aircraft.GetAllParts())
            {
                if (part == null || part.gameObject == null) continue;
                partCount++;
                if (part.gameObject == aircraft.gameObject) rootIncluded = true;
                foreach (FixedJoint joint in part.gameObject.GetComponents<FixedJoint>()) if (joint != null) jointCount++;
            }
            if (!rootIncluded)
                foreach (FixedJoint joint in aircraft.gameObject.GetComponents<FixedJoint>()) if (joint != null) jointCount++;
            parts = partCount;
            fixedJoints = jointCount;
            return true;
        }
        catch { return false; }
    }

    private void TryLog(string message) { try { log(message); } catch { } }
    private long FixedSequence() { try { return fixedSequence(); } catch { return -1; } }
    private static string KeyOf(Aircraft aircraft) { try { return aircraft.definition?.jsonKey ?? ""; } catch { return ""; } }
    private static string PidOf(Aircraft aircraft) { try { return aircraft.persistentID.ToString(); } catch { return ""; } }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Chat;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoyalWingman;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("blueprinter.kestrel", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "discord9.loyalwingman", Name = "Loyal Wingman", Version = "0.0.1";
    public int MountedDroneLoadoutApiVersion => 1;
    public bool TryQueryMountedDroneLoadout(Weapon portWeapon, out long revision, out string reason,
                                             out string[] currentMountKeys, out string[][] legalMountKeysBySlot)
    {
        revision = 0; reason = ""; currentMountKeys = Array.Empty<string>(); legalMountKeysBySlot = Array.Empty<string[]>();
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != mainManagedThreadId) { reason = "wrong_thread"; return false; }
            if (!ReferenceEquals(activeInstance, this)) { reason = "plugin_inactive"; return false; }
            if (!(portWeapon is DroneCarrierPortWeapon port)) { reason = "invalid_port_weapon"; return false; }
            if (GameManager.gameState != GameState.SinglePlayer) { reason = "not_single_player"; return false; }
            if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active) { reason = "server_inactive"; return false; }
            if (!port.TryGetLoadoutSnapshot(out DroneCarrierPortWeapon.MountedDroneLoadoutSnapshot snapshot, out reason)) return false;
            revision = snapshot.revision;
            if (!TryGetLocalSession(out CarrierSession session) || !IsActiveSession(session)) { reason = "session_not_ready"; return false; }
            if (!ReferenceEquals(session.Home, snapshot.carrier)) { reason = "session_home_mismatch"; return false; }
            if (!PortReleaseAuthority(port, snapshot.carrier, snapshot.station)) { reason = "port_not_authoritative"; return false; }
            return MountedDroneLoadoutNativeEnumerator.TryEnumerate(session.Player, snapshot.descriptor.definition, snapshot.loadout,
                snapshot.carrier!, out currentMountKeys, out legalMountKeysBySlot, out reason);
        }
        catch { reason = "query_failed"; currentMountKeys = Array.Empty<string>(); legalMountKeysBySlot = Array.Empty<string[]>(); return false; }
    }
    public bool TryApplyMountedDroneLoadout(Weapon portWeapon, long expectedRevision, string[] requestedMountKeys,
                                             out long revision, out string reason, out string[] currentMountKeys)
    {
        currentMountKeys = Array.Empty<string>();
        if (!TryQueryMountedDroneLoadout(portWeapon, out revision, out reason, out currentMountKeys, out _)) return false;
        try
        {
            if (!MountedDroneLoadoutLogic.RevisionMatches(expectedRevision, revision)) { reason = "stale_revision"; return false; }
            if (!TryGetLocalSession(out CarrierSession session) || !IsActiveSession(session) || session.State != CarrierSessionState.Complete || session.Busy)
            { reason = "session_busy"; return false; }
            string snapshotReason = "port_changed";
            if (!(portWeapon is DroneCarrierPortWeapon port) || !port.TryGetLoadoutSnapshot(out DroneCarrierPortWeapon.MountedDroneLoadoutSnapshot snapshot, out snapshotReason))
            { reason = snapshotReason == "ok" ? "port_changed" : snapshotReason; return false; }
            if (snapshot.revision != revision || !ReferenceEquals(session.Home, snapshot.carrier)) { reason = "port_changed"; return false; }
            if (!MountedDroneLoadoutNativeEnumerator.TryBuildRequestedLoadout(session.Player, snapshot.descriptor.definition,
                    snapshot.loadout, snapshot.carrier!, requestedMountKeys, out NuclearOption.SavedMission.Loadout candidate,
                    out string[] normalizedKeys, out reason)) return false;
            if (currentMountKeys.Length == normalizedKeys.Length)
            {
                bool same = true;
                for (int i = 0; i < currentMountKeys.Length; i++) if (!string.Equals(currentMountKeys[i], normalizedKeys[i], StringComparison.Ordinal)) { same = false; break; }
                if (same) { reason = "no_change"; return true; }
            }
            if (!MountedDroneLoadoutLogic.TryNextRevision(snapshot.revision, true, out long nextRevision)) { reason = "revision_exhausted"; return false; }
            if (!DroneCarrierPortWeapon.TryCreateFreeAppliedDescriptor(snapshot.descriptor, candidate, out DroneCarrierPortWeapon.Descriptor applied))
            { reason = "descriptor_invalid"; return false; }
            if (!port.TryEnterLoadoutApply(snapshot)) { reason = "port_changed"; return false; }
            try
            {
                if (!port.CommitLoadoutApply(snapshot, applied, nextRevision)) { reason = "port_changed"; return false; }
                revision = nextRevision; currentMountKeys = normalizedKeys; reason = "ok";
                port.ProjectCommittedLoadout();
                return true;
            }
            finally { port.EndLoadoutApply(); }
        }
        catch { reason = "apply_failed"; return false; }
    }
    private ConfigEntry<bool> enable = null!, enableWingmanStatusMessages = null!, combatEnable = null!;
    private ConfigEntry<KeyboardShortcut> previousKey = null!, nextKey = null!, designateTargetKey = null!, selectAllKey = null!;
    private ConfigEntry<int> verificationFrames = null!;
    private ConfigEntry<float> spawnBack = null!, spawnDown = null!, launchBoost = null!, lowSpeedWarning = null!,
                               switchCooldown = null!;
    private ConfigEntry<string> carrierKey = null!, droneKey = null!;
    private AircraftSwitchBackend? backend;
    private EmergencyReturn? pendingEmergency;
    private PayloadRegistry? payload;
    private DroneCarrierPortRegistry? droneCarrierPortRegistry;
    private bool droneCarrierPortRegistrationAttempted;
    private int droneCarrierPortRegistrationRetries;
    private MapCommandPanel? mapPanel;
    private FqCradleLoadoutUi? fqCradleLoadoutUi;
    private RecoveryGuidanceVisual? recoveryGuidance;
    private Harmony? sessionTransportHarmony, cradleStationHarmony;
    private SessionRegistry? sessionRegistry;
    private CarrierFqLoadoutPlan? localFqLoadoutDraft;
    private bool fqLoadoutPlanDirty;
    private readonly Dictionary<INetworkPlayer, CarrierFqLoadoutPlan> pendingRemoteFqLoadoutPlans =
        new Dictionary<INetworkPlayer, CarrierFqLoadoutPlan>(ReferenceIdentityComparer<INetworkPlayer>.Instance);
    private bool followEnabledLastFrame;
    private NetworkManagerNuclearOption? trackedNetworkManager;
    private bool serverWasActive;
    private long fixedSequence;
    private int mainManagedThreadId;
    private static Plugin? activeInstance;
    private static readonly AccessTools.FieldRef<UnitPart, bool> criticalPartField =
        AccessTools.FieldRefAccess<UnitPart, bool>("criticalPart");
    private void Awake()
    {
        mainManagedThreadId = Thread.CurrentThread.ManagedThreadId;
        activeInstance = this;
        DroneCarrierPortWeapon.AnimatedMilestone += OnAnimatedPortMilestone;
        WingmanSessionTransport.BindPlugin(this);
        WingmanSessionTransport.EnsureTemplate();
        enable = Config.Bind("General", "Enable", true, "");
        combatEnable =
            Config.Bind("Combat", "Enable", true, "Enable designated-target A2A and autonomous missile defense.");
        enableWingmanStatusMessages = Config.Bind("Messages", "EnableWingmanStatusMessages", true,
                                                  "Show local wingman status messages in the gameplay HUD.");
        previousKey = Config.Bind("Input", "PreviousAircraftKey", new KeyboardShortcut(KeyCode.F10), "");
        nextKey = Config.Bind("Input", "NextAircraftKey", new KeyboardShortcut(KeyCode.F11), "");
        designateTargetKey = Config.Bind("Input", "DesignateTargetKey", new KeyboardShortcut(KeyCode.F8),
                                         "Designate or cancel the current A2A target.");
        selectAllKey = Config.Bind("Input", "SelectAllKey", new KeyboardShortcut(KeyCode.F5),
                                   "Select all commandable FQs while the LW map screen is open.");
        verificationFrames = Config.Bind("Safety", "SwitchVerificationFrames", 3, "");
        lowSpeedWarning = Config.Bind("Safety", "LowSpeedReleaseWarningMetersPerSecond", 120f, "");
        switchCooldown = Config.Bind("Input", "SwitchCooldownSeconds", .25f,
                                     "Post-switch input debounce; switching remains serialized by the busy lock.");
        carrierKey = Config.Bind("Aircraft", "CarrierJsonKey", "QuadVTOL1", "");
        if (carrierKey.Value == "P_Trisurface1")
        {
            carrierKey.Value = "QuadVTOL1";
            Config.Save();
            Log("state=config_repaired key=CarrierJsonKey");
        }
        droneKey = Config.Bind("Aircraft", "DroneJsonKey", "kestrel", "");
        spawnBack = Config.Bind("Launch", "SpawnBackMeters", 12f, "");
        spawnDown = Config.Bind("Launch", "SpawnDownMeters", 4f, "");
        launchBoost = Config.Bind("Launch", "LaunchBoostMetersPerSecond", 15f, "");
        var mountKey = Config.Bind("Payload", "PayloadMountKey", PayloadRegistry.Key, "");
        if (mountKey.Value != PayloadRegistry.Key)
            Logger.LogWarning("[LoyalWingman] state=config_warning key=PayloadMountKey expected=" +
                              PayloadRegistry.Key + " actual=" + mountKey.Value);
        var bay = Config.Bind("Payload", "CargoBayFallbackIndex", 0, "");
        var mass = Config.Bind("Payload", "PayloadMassKg", 7000f, "");
        var cost = Config.Bind("Payload", "PayloadCost", 29f, "");
        if (cost.Value == 29000000f || cost.Value == 29000f)
        {
            cost.Value = 29f;
            Config.Save();
            Log("state=config_repaired key=PayloadCost");
        }
        var rcs = Config.Bind("Payload", "PayloadRcs", .8f, "");
        var drag = Config.Bind("Payload", "PayloadDrag", 1.2f, "");
        payload =
            new PayloadRegistry(Logger, carrierKey.Value, bay.Value, mass.Value, cost.Value, rcs.Value, drag.Value);
        droneCarrierPortRegistry = new DroneCarrierPortRegistry(Logger);
        fqCradleLoadoutUi = new FqCradleLoadoutUi(this, Logger);
        recoveryGuidance = new RecoveryGuidanceVisual();
        backend = new AircraftSwitchBackend(Log, () => fixedSequence);
        sessionRegistry = new SessionRegistry(this, () => enableWingmanStatusMessages.Value, Log);
        mapPanel = new MapCommandPanel(this, Log);
        InstallSessionTransportRegistration();
        InstallCradleLaunchFilter();
        NativeWeaponReleaseBridge.RegisterReleaseCallback(this, RequestNativeRelease, out string callbackReason);
        if (callbackReason != "ok")
            Log("state=native_release_rejected reason=" + callbackReason);
        SceneManager.sceneLoaded += OnScene;
    }
    private void Start()
    {
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (manager != null)
            WingmanSessionTransport.RegisterForManager(manager);
        StartCoroutine(InitializePayload());
    }
    private IEnumerator InitializePayload()
    {
        while (!BlueprinterReady())
            yield return new WaitForSeconds(1f);
        while (payload != null && !payload.EnsureRegistered())
            yield return new WaitForSeconds(1f);
        if (!droneCarrierPortRegistrationAttempted && droneCarrierPortRegistry != null)
        {
            droneCarrierPortRegistrationAttempted = true;
            while (droneCarrierPortRegistrationRetries++ < 3 && !droneCarrierPortRegistry.EnsureRegistered())
                yield return new WaitForSeconds(1f);
        }
    }
    private void Update()
    {
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        bool server = manager != null && manager.Server.Active;
        if (serverWasActive && (!server || manager != trackedNetworkManager))
            CleanupAllSessions(server ? "network_changed" : "network_stopped");
        if (server)
            trackedNetworkManager = manager;
        else
            trackedNetworkManager = null;
        serverWasActive = server;
        PublishDirtyFqLoadoutPlan(server);

        if (server)
        {
            EnsureSessionRouter();
            CarrierSession[] snapshot = sessionRegistry?.SnapshotSessions() ?? Array.Empty<CarrierSession>();
            foreach (CarrierSession s in snapshot)
            {
                ObserveSessionAssets(s);
                if (!IsActiveSession(s))
                    continue;
                if (GameManager.gameState == GameState.SinglePlayer)
                    s.Cruise.Tick();
                if (enable.Value)
                    s.Manager.Tick();
            }
            if (followEnabledLastFrame && !enable.Value)
                foreach (CarrierSession s in snapshot)
                    if (IsActiveSession(s))
                        s.Manager.StopAll("disabled");
            followEnabledLastFrame = enable.Value;
        }
        TickRecoveryGuidance();
        mapPanel?.Tick(enable.Value);
        if (GameManager.gameState != GameState.SinglePlayer || !server || !enable.Value)
            return;
        HandleSinglePlayerInput();
    }
    private void FixedUpdate() => fixedSequence++;
    private void ObserveSessionAssets(CarrierSession s)
    {
        if (!IsActiveSession(s))
            return;

        if (GameManager.gameState != GameState.SinglePlayer)
        {
            if (s.Player == null || s.Player.Identity == null || s.Player.Owner == null || s.Player.Aircraft == null ||
                !IsCompleteCurrentAuthority(s.Player.Aircraft, s.Player))
            {
                CleanupSession(s, "session_owner_lost");
                return;
            }
            Aircraft? home = s.Home;
            if (home == null || home.disabled || home.HasEjected() || home.unitState == Unit.UnitState.Returned ||
                !TryGetAuthoritativeSessionLeader(s, out _))
            {
                CleanupSession(s, "assets_lost");
            }
            return;
        }

        if (GameManager.GetLocalPlayer(out Player currentPlayer) && currentPlayer != null && currentPlayer != s.Player)
        {
            CleanupSession(s, "player_changed");
            return;
        }

        Aircraft? oldHome = s.Home;
        if (oldHome != null && (oldHome.disabled || oldHome.HasEjected() || oldHome.unitState == Unit.UnitState.Returned))
        {
            if (s.HomeReturningToBase && oldHome.unitState == Unit.UnitState.Returned)
            {
                s.Context.Log("state=rtb_completed asset=home pid=" + PidOf(oldHome));
                SendLwChat("[LW] Home recovered to inventory.");
            }
            s.HomeReturningToBase = false;
            s.Cruise.Cleanup("home_lost");
            sessionRegistry?.TryClearHome(s, oldHome);
            if (!HasSurvivingFq(s) && !TryGetLocalSessionLeader(s, out _))
            {
                CleanupSession(s, "assets_lost");
                return;
            }
            WingmanSessionTransport.i?.PublishSnapshotToOwner(s);
        }
        else if (oldHome == null && !HasSurvivingFq(s) && !TryGetLocalSessionLeader(s, out _))
        {
            CleanupSession(s, "assets_lost");
            return;
        }

        if (!IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy)
            return;
        if (!GameManager.GetLocalPlayer(out Player player) || player == null)
        {
            SetOwnerAircraftGap(s);
            return;
        }
        if (player != s.Player)
        {
            CleanupSession(s, "player_changed");
            return;
        }
        if (!TryGetLocalSessionLeader(s, out Aircraft local))
        {
            SetOwnerAircraftGap(s);
            return;
        }
        if (s.OwnerAircraftGap)
        {
            s.OwnerAircraftGap = false;
            s.Context.Log("state=session_owner_rebound pid=" + PidOf(local));
            s.Manager.Reconcile("session_owner_rebound");
        }
    }
    private bool HasSurvivingFq(CarrierSession s)
    {
        foreach (Aircraft aircraft in s.Roster)
            if (aircraft != s.Home && aircraft != null && s.Manager.IsRegistered(aircraft) &&
                !aircraft.disabled && !aircraft.HasEjected() && aircraft.unitState != Unit.UnitState.Abandoned &&
                aircraft.unitState != Unit.UnitState.Returned)
                return true;
        return false;
    }
    private void SetOwnerAircraftGap(CarrierSession s)
    {
        if (!s.OwnerAircraftGap)
            s.Context.Log("state=session_owner_gap action=preserve");
        s.OwnerAircraftGap = true;
    }
    private static bool TryGetLocalSessionLeader(CarrierSession s, out Aircraft leader)
    {
        leader = null!;
        try
        {
            return GameManager.GetLocalAircraft(out leader) && leader == s.Player.Aircraft &&
                   IsCompleteCurrentAuthority(leader, s.Player) && !leader.disabled && !leader.HasEjected() &&
                   leader.unitState != Unit.UnitState.Abandoned && leader.unitState != Unit.UnitState.Returned;
        }
        catch
        {
            leader = null!;
            return false;
        }
    }
    private void OnScene(Scene _, LoadSceneMode __)
    {
        recoveryGuidance?.Dispose();
        recoveryGuidance = new RecoveryGuidanceVisual();
        DroneCarrierPortWeapon.ClearAll();
        fqCradleLoadoutUi?.ClearScene();
        ClearActiveFqLoadoutPlan("scene");
        CleanupAllSessions("scene_loaded");
    }
    private void TickRecoveryGuidance()
    {
        if (recoveryGuidance == null || !TryGetLocalSession(out CarrierSession session) || !IsActiveSession(session) ||
            !TryGetActiveLeader(session, out Aircraft drone) || ReferenceEquals(drone, session.Home) ||
            !session.Roster.Contains(drone) || !session.Manager.IsRegistered(drone) || drone.definition?.jsonKey != droneKey.Value ||
            session.Home == null || session.HomeReturningToBase ||
            !DroneCarrierPortWeapon.TryGetNearestRecoveryGuidancePort(drone, session.Home, out DroneCarrierPortWeapon port))
        {
            recoveryGuidance?.Hide();
            return;
        }
        recoveryGuidance.Tick(drone, port);
    }
    // Native release creates a wingman under the current carrier; only an explicit F10/F11 request transfers player
    // control through the local switch backend.
    private bool RequestNativeRelease(Aircraft attachedCarrier, LoyalWingmanCradleWeapon firingRound, int physicalCradleId,
                                      int physicalRound, out string reason)
    {
        reason = "";
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            reason = "not_authoritative_server";
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        if (firingRound == null || firingRound.PhysicalHardpoint == null || physicalCradleId < 0 || physicalRound < 0 ||
            physicalRound >= LoyalWingmanCradleWeapon.Capacity ||
            firingRound.PhysicalHardpoint.mount?.jsonKey != PayloadRegistry.Key)
        {
            reason = "cradle_attachment";
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        Player? player = attachedCarrier.Player;
        if (player == null || player.Owner == null || !IsCompleteCurrentAuthority(attachedCarrier, player))
        {
            reason = "carrier_owner";
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        if (payload == null || !payload.Ready)
        {
            reason = "payload_not_ready";
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        CarrierFqLoadoutPlan? authoritativePlan = player.IsLocalPlayer ? localFqLoadoutDraft :
                                             pendingRemoteFqLoadoutPlans.TryGetValue(player.Owner, out CarrierFqLoadoutPlan? remotePlan) ? remotePlan : null;
        if (sessionRegistry == null || !sessionRegistry.TryGetOrCreate(player, attachedCarrier, authoritativePlan,
                                                                        out CarrierSession s, out bool created, out reason))
        {
            Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        if (authoritativePlan != null)
            s.LoadoutPlan = authoritativePlan.Copy();
        string?[]? capturedLoadoutRow = null;
        s.LoadoutPlan?.TryGetRow(physicalCradleId, physicalRound, out capturedLoadoutRow);
        bool stateReady = created || s.State == CarrierSessionState.Complete;
        string? blockReason = CradleReleaseProgressionLogic.SessionBlockReason(s.HomeReturningToBase, stateReady,
                                                                                s.Busy, s.ReleasePending);
        if (blockReason != null)
        {
            reason = blockReason;
            s.Context.Log("state=native_release_rejected reason=" + reason);
            return false;
        }
        ulong token = ++s.ReleaseGeneration;
        s.ReleasePending = true;
        s.ReleaseCoroutine = StartCoroutine(DeployNativeRelease(s, token, attachedCarrier, player, capturedLoadoutRow,
                                                                 created, physicalCradleId, physicalRound));
        reason = "native_release_scheduled";
        s.Context.Log("state=release_scheduled reason=" + reason + " carrier_pid=" + PidOf(attachedCarrier) +
                      " physical_cradle=" + physicalCradleId + " physical_round=" + physicalRound);
        return true;
    }

    private IEnumerator DeployNativeRelease(CarrierSession s, ulong token, Aircraft expectedCarrier, Player expectedPlayer,
                                            string?[]? capturedLoadoutRow,
                                             bool created, int physicalCradleId, int physicalRound)
    {
        try
        {
            yield return null;
            if (!IsActiveSession(s) || token != s.ReleaseGeneration || s.Player != expectedPlayer || s.Home != expectedCarrier ||
                expectedCarrier.Player != expectedPlayer || expectedPlayer.Aircraft != expectedCarrier ||
                !IsCompleteCurrentAuthority(expectedCarrier, expectedPlayer))
            {
                s.Context.Log("state=release_rejected reason=carrier_changed");
                if (created && IsActiveSession(s) && s.Roster.Count == 0)
                    CleanupSession(s, "release_authority_changed");
                yield break;
            }
            Deploy(s, expectedPlayer, expectedCarrier, created, physicalCradleId, physicalRound, capturedLoadoutRow);
        }
        finally
        {
            if (token == s.ReleaseGeneration)
            {
                s.ReleasePending = false;
                s.ReleaseCoroutine = null;
            }
        }
    }
    private void Deploy(CarrierSession s, Player player, Aircraft carrier, bool created, int physicalCradleId, int physicalRound,
                        string?[]? capturedLoadoutRow)
    {
        if (!IsActiveSession(s) || s.Player != player || s.Home != carrier || payload == null || !payload.Ready)
        {
            if (created && IsActiveSession(s) && s.Roster.Count == 0)
                CleanupSession(s, "deploy_initial_rejected");
            return;
        }
        if (!PreflightServer(player, carrier, out AircraftDefinition definition, out int livery))
        {
            if (created && IsActiveSession(s) && s.Roster.Count == 0)
                CleanupSession(s, "deploy_preflight_rejected");
            return;
        }
        WarnRelease(carrier);
        if (!payload.IsEquipped(carrier, out _, out string equipped))
        {
            s.Context.Log("state=release_rejected reason=" + equipped);
            if (created && IsActiveSession(s) && s.Roster.Count == 0)
                CleanupSession(s, "payload_not_equipped");
            return;
        }
        PruneRoster(s);
        int joinIndex = 0;
        foreach (Aircraft existing in s.Roster)
            if (existing != carrier && s.Manager.IsRegistered(existing))
                joinIndex++;
        Aircraft? drone = null;
        bool attached = false;
        try
        {
            int roleOrdinal = s.Placements.Preview().Ordinal;
            string loadoutSource = capturedLoadoutRow == null ? "server_default" : "local_ui";
            NuclearOption.SavedMission.Loadout? loadout = BuildFqLoadout(definition, physicalCradleId, physicalRound,
                                                                          capturedLoadoutRow, carrier);
            s.Context.Log("state=release_loadout source=" + loadoutSource + " ordinal=" + roleOrdinal);
            Vector3 position = SpawnJoinPosition(carrier, joinIndex);
            drone = Spawner.i.SpawnAircraft(null, definition.unitPrefab, loadout,
                                            definition.aircraftParameters.DefaultFuelLevel, new LiveryKey(livery),
                                            position.ToGlobalPosition(), carrier.transform.rotation,
                                            carrier.rb.velocity + carrier.transform.forward * launchBoost.Value, null,
                                            carrier.NetworkHQ, "", 1f, .5f);
            if (drone == null)
                throw new InvalidOperationException("spawn_null");
            if (drone.targetCam != null)
                drone.targetCam.enabled = false;
            string attachReason = "session_registry";
            if (sessionRegistry == null || !sessionRegistry.TryAttachDrone(s, drone, out attachReason))
                throw new InvalidOperationException("attach_" + attachReason);
            attached = true;
            if (!TryConfigureDroneCockpitCriticalPart(drone, out string cockpitReason))
                throw new InvalidOperationException("cockpit_critical_part_" + cockpitReason);
            SubscribeDrone(s, drone);
            SessionCollisionLease lease = new SessionCollisionLease();
            s.CollisionLeases.Add(lease);
            AddCollisionPairs(lease, carrier, drone);
            foreach (Aircraft existing in s.Roster)
                if (existing != drone && s.Manager.IsRegistered(existing))
                    AddCollisionPairs(lease, existing, drone);
            lease.Coroutine = StartCoroutine(IgnoreCollisions(s, lease));
            s.State = CarrierSessionState.Complete;
            s.NextSwitchTime = Time.realtimeSinceStartup;
        }
        catch (Exception e)
        {
            if (drone != null)
            {
                UnsubscribeDrone(s, drone);
                if (attached && sessionRegistry != null)
                    if (!sessionRegistry.TryDetachDrone(s, drone, "spawn_failed", out string detachReason))
                        s.Context.Log("event=session_detach_failed drone_pid=" + PidOf(drone) + " reason=" + detachReason);
                Despawn(drone);
            }
            s.Context.Log("state=release_rejected reason=spawn error=" + e.GetType().Name);
            if (created && IsActiveSession(s) && s.Roster.Count == 0)
                CleanupSession(s, "spawn");
            return;
        }
        try
        {
            s.Manager.Reconcile("release_spawned");
            EnsureSessionRouter();
            WingmanSessionTransport.i?.PublishSnapshotToOwner(s);
        }
        catch (Exception e)
        {
            s.Context.Log("state=post_spawn_error error=" + e.GetType().Name + " pid=" + PidOf(drone));
        }
        s.Context.Log("state=release_ready control=carrier drone_pid=" + PidOf(drone) + " roster=" + s.Roster.Count +
                      " event=drone_attached physical_cradle=" + physicalCradleId + " physical_round=" + physicalRound);
    }
    private void StartSwitch(CarrierSession s, Aircraft oldAircraft, Aircraft target, string direction, bool takeOverReturningFq = false)
    {
        ulong token = ++s.SwitchGeneration;
        s.Busy = true;
        s.SwitchCoroutine = StartCoroutine(SwitchWrapper(s, token, oldAircraft, target, direction, takeOverReturningFq));
    }
    private IEnumerator SwitchWrapper(CarrierSession s, ulong token, Aircraft oldAircraft, Aircraft target, string direction,
                                      bool takeOverReturningFq)
    {
        IEnumerator work = WaitAndSwitch(s, oldAircraft, target, direction, takeOverReturningFq);
        try
        {
            while (true)
            {
                object current;
                try
                {
                    if (!work.MoveNext())
                        break;
                    current = work.Current;
                }
                catch (Exception e)
                {
                    s.Context.Log("state=failed phase=switch_unexpected error=" + e.GetType().Name);
                    if (token == s.SwitchGeneration)
                        Fatal(s, oldAircraft, target);
                    break;
                }
                yield return current;
            }
        }
        finally
        {
            if (token == s.SwitchGeneration)
            {
                s.SwitchCoroutine = null;
                s.Busy = false;
            }
        }
    }
    private void WarnRelease(Aircraft aircraft)
    {
        if (aircraft.rb.velocity.magnitude < lowSpeedWarning.Value)
            Log("state=release_warning reason=low_speed speed_mps=" + aircraft.rb.velocity.magnitude +
                " world_y=" + aircraft.transform.position.y);
    }
    private void HandleSinglePlayerInput()
    {
        if (!TryGetLocalSession(out CarrierSession s) || !IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy)
            return;
        if (designateTargetKey.Value.IsDown() && TryGetActiveLeader(s, out Aircraft leader))
            s.Manager.DesignateTarget(leader);
        if (previousKey.Value.IsDown())
            Cycle(s, -1);
        if (nextKey.Value.IsDown())
            Cycle(s, 1);
    }
    private void Cycle(CarrierSession s, int direction)
    {
        string directionName = direction < 0 ? "previous" : "next";
        if (s.Busy || s.State != CarrierSessionState.Complete || !TryGetActiveLeader(s, out Aircraft from))
        {
            Reject(s, "not_complete", directionName, null, null);
            return;
        }
        PruneRoster(s);
        RosterSnapshot(s, "cycle");
        if (Time.realtimeSinceStartup < s.NextSwitchTime)
        {
            Reject(s, "cooldown", directionName, from, null);
            return;
        }
        if (!FindTarget(s, from, direction, out Aircraft target, out string targetReason))
        {
            Reject(s, targetReason, directionName, from, null);
            return;
        }
        s.State = CarrierSessionState.Switching;
        s.Context.Log("state=switch_cycle_requested direction=" + directionName + " from=" + NameOf(from) +
                      " to=" + NameOf(target));
        StartSwitch(s, from, target, directionName);
    }
    private bool FindTarget(CarrierSession s, Aircraft from, int direction, out Aircraft target,
                                   out string reason)
    {
        target = null!;
        reason = "no_eligible_target";
        List<Aircraft> candidates = new List<Aircraft>();
        if (s.Home != null)
            candidates.Add(s.Home);
        foreach (Aircraft aircraft in s.Roster)
            if (aircraft != null && !candidates.Contains(aircraft))
                candidates.Add(aircraft);
        int at = candidates.IndexOf(from);
        if (at < 0)
            at = direction > 0 ? -1 : 0;
        string recoveryReason = "";
        bool recoveryOnly = true;
        int count = candidates.IndexOf(from) < 0 ? candidates.Count : candidates.Count - 1;
        for (int step = 1; step <= count; step++)
        {
            Aircraft candidate = candidates[(at + direction * step + candidates.Count * 2) % candidates.Count];
            if (candidate == from)
                continue;
            if (candidate == s.Home ? s.HomeReturningToBase : s.Manager.IsReturningToBase(candidate))
                continue;
            if (!NativeRecoveryGuard.TryEvaluate(candidate, out string candidateReason))
            {
                if (recoveryReason == "")
                    recoveryReason = candidateReason;
                else if (recoveryReason != candidateReason)
                    recoveryOnly = false;
                continue;
            }
            if (StaticEligible(candidate))
            {
                target = candidate;
                reason = "";
                return true;
            }
            recoveryOnly = false;
        }
        if (recoveryOnly && recoveryReason != "")
            reason = recoveryReason;
        return false;
    }
    private void SubscribeDrone(CarrierSession s, Aircraft drone)
    {
        if (s.SubscribedDrones.Add(drone))
            drone.onDisableUnit += OnDroneDisabled;
    }

    private bool TryConfigureDroneCockpitCriticalPart(Aircraft drone, out string reason)
    {
        reason = "";
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active ||
            drone == null || !drone.IsServer)
        {
            reason = "authority";
            return false;
        }
        if (drone.definition?.jsonKey != "kestrel")
        {
            reason = "definition";
            return false;
        }
        UnitPart? cockpit = drone.cockpit;
        if (cockpit == null || cockpit.parentUnit != drone)
        {
            reason = "cockpit";
            return false;
        }
        try
        {
            criticalPartField(cockpit) = true;
            Log("state=drone_cockpit_critical configured=true pid=" + PidOf(drone));
            return true;
        }
        catch (Exception e)
        {
            reason = "field_" + e.GetType().Name;
            return false;
        }
    }

    private IEnumerator CompleteDroneCarrierPortLaunch(DroneCarrierPortWeapon port)
    {
        yield return null;
        if (port == null || !port.CompleteLaunch(PortLaunchAuthority(port, port.Carrier, port.Station), out WeaponStation? station, out Unit? owner, out Unit? target,
                                                  out GlobalPosition aimpoint) || station == null || owner == null)
            yield break;
        station.LaunchMount(owner, target, aimpoint);
    }

    internal static void ReleaseDroneCarrierPort(DroneCarrierPortWeapon port, AircraftDefinition definition,
                                                  NuclearOption.SavedMission.Loadout loadout, LiveryKey livery)
    {
        if (activeInstance != null)
            activeInstance.ReleaseDroneCarrierPortNow(port, definition, loadout, livery);
    }

    private void ReleaseDroneCarrierPortNow(DroneCarrierPortWeapon port, AircraftDefinition definition,
                                             NuclearOption.SavedMission.Loadout loadout, LiveryKey livery)
    {
        Aircraft? carrier = port.Carrier;
        Hardpoint? portHardpoint = port.PortHardpoint;
        CarrierSession? session = null;
        Aircraft? drone = null;
        bool attached = false, subscribed = false, created = false;
        SessionCollisionLease? lease = null;
        try
        {
            if (!PortReleaseAuthority(port, carrier, port.Station) || carrier == null || portHardpoint == null ||
                carrier.rb == null || carrier.NetworkHQ == null || Spawner.i == null || carrier.Player == null ||
                sessionRegistry == null)
                throw new InvalidOperationException("port_release_guard");
            if (!sessionRegistry.TryGetOrCreate(carrier.Player, carrier, null, out session, out created, out string sessionReason))
                throw new InvalidOperationException("session_" + sessionReason);
            if (session.HomeReturningToBase || session.Busy || session.ReleasePending ||
                (!created && session.State != CarrierSessionState.Complete))
                throw new InvalidOperationException("session_unavailable");
            Transform pose;
            Vector3 spawnPosition;
            Quaternion spawnRotation;
            Vector3 launchDirection;
            if (port.HasAnimatedProvider)
            {
                pose = port.AnimatedLaunchPose ?? throw new InvalidOperationException("animated_launch_pose");
                spawnPosition = pose.position;
                spawnRotation = pose.rotation;
                launchDirection = pose.forward;
            }
            else
            {
                pose = portHardpoint.transform;
                spawnPosition = pose.position - carrier.transform.up * definition.spawnOffset.y +
                                carrier.transform.forward * definition.spawnOffset.z;
                spawnRotation = carrier.transform.rotation;
                launchDirection = carrier.transform.forward;
            }
            port.BeginReleaseExclusion();
            drone = Spawner.i.SpawnAircraft(null, definition.unitPrefab, loadout, 1f, livery,
                spawnPosition.ToGlobalPosition(), spawnRotation,
                carrier.rb.velocity + launchDirection * launchBoost.Value, null, carrier.NetworkHQ, "", 1f, .5f);
            if (drone == null) throw new InvalidOperationException("port_spawn_null");
            port.BindReleaseExclusion(drone);
            if (drone.targetCam != null) drone.targetCam.enabled = false;
            if (!sessionRegistry.TryAttachDrone(session, drone, out string attachReason))
                throw new InvalidOperationException("attach_" + attachReason);
            attached = true;
            if (!TryConfigureDroneCockpitCriticalPart(drone, out string cockpitReason))
                throw new InvalidOperationException("cockpit_critical_part_" + cockpitReason);
            SubscribeDrone(session, drone); subscribed = true;
            lease = new SessionCollisionLease(); session.CollisionLeases.Add(lease);
            AddCollisionPairs(lease, carrier, drone, port.RecoveryTrigger);
            Log("state=drone_port_release_lease recovery_trigger_excluded=true radius=" + port.RecoveryRadius);
            foreach (Aircraft existing in session.Roster)
                if (existing != drone && session.Manager.IsRegistered(existing)) AddCollisionPairs(lease, existing, drone);
            lease.Coroutine = StartCoroutine(IgnoreCollisions(session, lease));
            if (port.HasAnimatedProvider &&
                !session.Manager.TryBeginLaunchClearance(drone, carrier, -carrier.transform.up,
                                                         out string clearanceReason))
            {
                Log("state=launch_clearance_begin_failed drone_pid=" + PidOf(drone) + " carrier_pid=" +
                    PidOf(carrier) + " reason=" + clearanceReason);
                throw new InvalidOperationException("launch_clearance_" + clearanceReason);
            }
            session.State = CarrierSessionState.Complete;
            session.NextSwitchTime = Time.realtimeSinceStartup;
            session.Manager.Reconcile("port_release_spawned");
            EnsureSessionRouter();
            WingmanSessionTransport.i?.PublishSnapshotToOwner(session);
            Log("state=drone_port_release old=loaded new=empty spawned_pid=" + PidOf(drone) + " key=" + definition.jsonKey +
                " pose_mode=" + (port.HasAnimatedProvider ? "provider_final" : "legacy_offset") +
                (port.HasAnimatedProvider ? "" : " spawn_offset=" + definition.spawnOffset) +
                " hook_position=" + pose.position + " spawn_position=" + spawnPosition);
        }
        catch (Exception e)
        {
            try { port.CancelReleaseExclusion(); } catch (Exception x) { Log("state=port_rollback exclusion=" + x.GetType().Name); }
            bool detached = false, destroyed = false, preservedManaged = false;
            if (drone != null && session != null)
            {
                if (attached && sessionRegistry != null)
                {
                    try
                    {
                        detached = sessionRegistry.TryDetachDrone(session, drone, "port_spawn_failed", out string detachReason);
                        if (!detached) session.Context.Log("event=session_detach_failed drone_pid=" + PidOf(drone) + " reason=" + detachReason);
                    }
                    catch (Exception x) { Log("state=port_rollback detach=" + x.GetType().Name); }
                }
                if (DroneCarrierPortLogic.CanDestroyAfterReleaseRollback(attached, detached))
                {
                    if (lease != null)
                    {
                        try { if (lease.Coroutine != null) StopCoroutine(lease.Coroutine); } catch (Exception x) { Log("state=port_rollback lease_stop=" + x.GetType().Name); }
                        try { RestoreLease(session, lease); } catch (Exception x) { Log("state=port_rollback lease_restore=" + x.GetType().Name); }
                    }
                    if (subscribed)
                        try { UnsubscribeDrone(session, drone); } catch (Exception x) { Log("state=port_rollback unsubscribe=" + x.GetType().Name); }
                    try { destroyed = TryDestroyDroneCarrierPortCapture(drone); } catch (Exception x) { Log("state=port_rollback destroy=" + x.GetType().Name); }
                    if (attached && detached) CompletePortRemoval(session, "port_release_rollback");
                }
                else
                {
                    preservedManaged = true;
                    if (lease != null && lease.Coroutine == null)
                        try { lease.Coroutine = StartCoroutine(IgnoreCollisions(session, lease)); }
                        catch (Exception x) { Log("state=port_rollback lease_restart=" + x.GetType().Name); }
                    try { SubscribeDrone(session, drone); } catch (Exception x) { Log("state=port_rollback subscribe=" + x.GetType().Name); }
                    CompletePortRemoval(session, "port_release_rollback_detach_failed");
                }
            }
            if (created && session != null && !preservedManaged)
                try { if (IsActiveSession(session) && session.Roster.Count == 0) CleanupSession(session, "port_spawn"); }
                catch (Exception x) { Log("state=port_rollback session_cleanup=" + x.GetType().Name); }
            Log("state=drone_port_release_failed error=" + e.GetType().Name + " reason=" + e.Message + " detached=" + detached + " destroyed=" + destroyed + " preservedManaged=" + preservedManaged);
        }
    }
    private static bool PortLaunchAuthority(DroneCarrierPortWeapon port, Aircraft? owner, WeaponStation? station)
    {
        return GameManager.gameState == GameState.SinglePlayer && NetworkManagerNuclearOption.i != null &&
               NetworkManagerNuclearOption.i.Server.Active && owner != null && station != null && port.Carrier == owner &&
               port.Station == station && owner.IsServer && owner.LocalSim && owner.HasAuthority && owner.Identity != null &&
               owner.Identity.IsSpawned && port.HasDescriptor && station.Weapons.Contains(port);
    }
    private static bool PortReleaseAuthority(DroneCarrierPortWeapon port, Aircraft? owner, WeaponStation? station)
    {
        return GameManager.gameState == GameState.SinglePlayer && NetworkManagerNuclearOption.i != null &&
               NetworkManagerNuclearOption.i.Server.Active && owner != null && station != null && port.Carrier == owner &&
               port.Station == station && owner.IsServer && owner.LocalSim && owner.HasAuthority && owner.Identity != null &&
               owner.Identity.IsSpawned && station.Weapons.Contains(port) && port.IsAcceptedRelease(station);
    }

    internal static void TryCaptureDroneCarrierPort(DroneCarrierPortWeapon port, Aircraft candidate)
    {
        if (activeInstance != null) activeInstance.TryBeginDroneCarrierPortCapture(port, candidate);
    }
    internal static void TryBeginAnimatedCaptureLock(DroneCarrierPortWeapon port, Aircraft candidate, long token) =>
        activeInstance?.TryBeginAnimatedCaptureLockInternal(port, candidate, token);
    internal static void AbortAnimatedCapture(DroneCarrierPortWeapon port, Aircraft candidate, long token,
                                              AnimatedPortMotion motion, string reason) =>
        activeInstance?.AbortAnimatedCaptureInternal(port, candidate, token, motion, reason);
    internal static void CancelPendingAnimatedPortRecovery(DroneCarrierPortWeapon port, Aircraft candidate) =>
        activeInstance?.CancelPendingAnimatedPortRecoveryInternal(port, candidate);
    private void CancelPendingAnimatedPortRecoveryInternal(DroneCarrierPortWeapon port, Aircraft candidate)
    {
        port.CancelPlayerHandoff();
        if (TryGetLocalSession(out CarrierSession session) && ReferenceEquals(session.Player.Aircraft, candidate))
        { session.State = CarrierSessionState.Complete; session.Busy = false; session.SwitchCoroutine = null; session.Manager.Reconcile("animated_port_reset"); }
    }
    private void AbortAnimatedCaptureInternal(DroneCarrierPortWeapon port, Aircraft candidate, long token, AnimatedPortMotion motion, string reason)
    {
        Log("state=recovery_aborted_" + reason + " candidate_pid=" + PidOf(candidate) + " raw_distance=" + Mathf.Sqrt(port.AnimatedCaptureRawSquaredDistance) + " occupied=" + port.AnimatedCaptureTriggerOccupied + " root=" + candidate.transform.position + " pose=" + port.AnimatedRecoveryPosePosition);
        FailAnimatedCapture(port, candidate, token, motion, reason);
    }
    private void TryBeginAnimatedCaptureLockInternal(DroneCarrierPortWeapon port, Aircraft candidate, long token)
    {
        bool player = port.IsPlayerReserved(candidate);
        bool admitted = candidate.rb != null && port.Station != null && port.Carrier != null && port.PortHardpoint != null &&
            port.RecoveryEnabled && NativeRecoveryGuard.TryEvaluate(candidate, out _) &&
            port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) && port.HasAnimatedFinalOverlap(candidate.rb) &&
            (player ? TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, true, out _, out _) : TryEvaluateDroneCarrierPortCapture(port, candidate, out _, true));
        if (!admitted) { FailAnimatedCapture(port, candidate, token, AnimatedPortMotion.WaitingCapture, "capture_lock_guard"); return; }
        if (!port.TryBeginAnimatedCaptureLock(token)) return;
        Log("state=capture_radius_enter candidate_pid=" + PidOf(candidate));
        if (!port.TryAnimatedCaptureLock(token)) FailAnimatedCapture(port, candidate, token, AnimatedPortMotion.LockingCapture, "capture_lock_request");
    }

    private void TryBeginDroneCarrierPortCapture(DroneCarrierPortWeapon port, Aircraft candidate)
    {
        if (candidate == null) return;
        if (port.HasAnimatedProvider)
        {
            if (!port.RecoveryEnabled) return;
            if (TryGetLocalSession(out CarrierSession animatedLocal) && ReferenceEquals(animatedLocal.Player.Aircraft, candidate))
            {
                string playerReason = "admission";
                if (!NativeRecoveryGuard.TryEvaluate(candidate, out string playerGuard) ||
                    !TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, false, out animatedLocal, out playerReason) ||
                    !port.ReservePlayerRecovery(candidate))
                { Log("state=port_capture_rejected stage=animated_player_admission reason=" + (playerGuard ?? playerReason) + " candidate_pid=" + PidOf(candidate)); return; }
                long token = port.BeginAnimatedOperation(AnimatedPortMotion.ExtendingCapture);
                port.BeginAnimatedCaptureMonitor(token, candidate);
                animatedLocal.State = CarrierSessionState.PreSwitch; animatedLocal.Busy = true; ++animatedLocal.SwitchGeneration;
                if (token == 0 || !port.TryAnimatedExtend(token)) { port.ResetAnimatedOperation(); port.CancelPlayerHandoff(); animatedLocal.State = CarrierSessionState.Complete; animatedLocal.Busy = false; animatedLocal.SwitchCoroutine = null; animatedLocal.Manager.Reconcile("animated_extend_failed"); return; }
                Log("state=recovery_prepare_enter candidate_pid=" + PidOf(candidate));
                return;
            }
            if (!NativeRecoveryGuard.TryEvaluate(candidate, out string guard) || !TryEvaluateDroneCarrierPortCapture(port, candidate, out _) || !port.Reserve(candidate))
            { Log("state=port_capture_rejected stage=animated_admission reason=" + guard + " candidate_pid=" + PidOf(candidate)); return; }
            long captureToken = port.BeginAnimatedOperation(AnimatedPortMotion.ExtendingCapture);
            port.BeginAnimatedCaptureMonitor(captureToken, candidate);
            if (captureToken == 0 || !port.TryAnimatedExtend(captureToken)) { port.ResetAnimatedOperation(); port.Cancel(); }
            else Log("state=recovery_prepare_enter candidate_pid=" + PidOf(candidate));
            return;
        }
        if (TryGetLocalSession(out CarrierSession local) && ReferenceEquals(local.Player.Aircraft, candidate))
        {
            if (!TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, false, out local, out string reason))
            {
                Log("state=port_capture_rejected stage=player_admission reason=" + reason + " candidate_pid=" + PidOf(candidate));
                return;
            }
            if (!port.ReservePlayerRecovery(candidate))
            {
                Log("state=port_capture_rejected stage=player_reserve reason=reserve candidate_pid=" + PidOf(candidate));
                return;
            }
            local.State = CarrierSessionState.PreSwitch; local.Busy = true;
            ulong token = ++local.SwitchGeneration;
            local.SwitchCoroutine = StartCoroutine(CompleteDroneCarrierPortPlayerRecovery(port, candidate, local, token));
            return;
        }
        if (!TryEvaluateDroneCarrierPortCapture(port, candidate, out CarrierSession? session))
        {
            Log("state=port_capture_rejected stage=capture_admission reason=capture_guard candidate_pid=" + PidOf(candidate));
            return;
        }
        if (!port.Reserve(candidate))
        {
            Log("state=port_capture_rejected stage=capture_reserve reason=reserve candidate_pid=" + PidOf(candidate));
            return;
        }
        StartCoroutine(CompleteDroneCarrierPortCapture(port, candidate, session));
    }

    private IEnumerator CompleteDroneCarrierPortCapture(DroneCarrierPortWeapon port, Aircraft candidate, CarrierSession? session,
                                                         long animatedToken = 0)
    {
        yield return null;
        if (animatedToken != 0)
        {
            if (port == null || port.AnimatedOperationToken != animatedToken || port.AnimatedMotion != AnimatedPortMotion.LockingCapture ||
                port.ReservedCandidate != candidate || candidate.rb == null || !NativeRecoveryGuard.TryEvaluate(candidate, out _) ||
                port.Carrier == null || port.PortHardpoint == null || port.Station == null ||
                !port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) ||
                !TryEvaluateDroneCarrierPortCapture(port, candidate, out session, true) || !port.HasAnimatedFinalOverlap(candidate.rb))
            { if (port != null) FailAnimatedCapture(port, candidate, animatedToken, AnimatedPortMotion.LockingCapture, "ai_commit_guard"); yield break; }
        }
        else
        {
            if (port == null || port.ReservedCandidate != candidate || !port.IsInTrigger(candidate) ||
                !TryEvaluateDroneCarrierPortCapture(port, candidate, out session) || !port.BuildProvisional(candidate))
            { port?.Cancel(); yield break; }
        }
        if (animatedToken != 0 && !port.BuildProvisional(candidate))
        { FailAnimatedCapture(port, candidate, animatedToken, AnimatedPortMotion.LockingCapture, "ai_provisional"); yield break; }
        if (animatedToken != 0 && (port.AnimatedOperationToken != animatedToken || port.AnimatedMotion != AnimatedPortMotion.LockingCapture ||
            port.Carrier == null || port.PortHardpoint == null || port.Station == null ||
            !port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) || port.ReservedCandidate != candidate ||
            candidate.rb == null || !NativeRecoveryGuard.TryEvaluate(candidate, out _) || !port.RecoveryEnabled ||
            !port.HasAnimatedFinalOverlap(candidate.rb)))
        { FailAnimatedCapture(port, candidate, animatedToken, AnimatedPortMotion.LockingCapture, "ai_final_guard"); yield break; }
        if (session != null)
        {
            if (sessionRegistry == null || !sessionRegistry.TryDetachDrone(session, candidate, "port_capture", out _))
            { if (animatedToken != 0) FailAnimatedCapture(port, candidate, animatedToken, AnimatedPortMotion.LockingCapture, "ai_detach"); else port.Cancel(); yield break; }
            UnsubscribeDrone(session, candidate);
        }
        if (!TryDestroyDroneCarrierPortCapture(candidate))
        {
            if (session != null)
            {
                CompletePortRemoval(session, "port_capture_destroy_failed");
                Log("state=drone_port_capture terminal=detached_destroy_failed");
            }
            if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "ai_destroy_failed");
            port.ClearAfterDetachedDestroyFailure(); yield break;
        }
        port.CommitCapture();
        if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "ai_committed");
        if (session != null) CompletePortRemoval(session, "port_capture");
        Log("state=drone_port_capture committed=true key=kestrel");
    }

    private bool TryDestroyDroneCarrierPortCapture(Aircraft candidate)
    {
        try
        {
            if (candidate == null || candidate.disabled || candidate.Identity == null || !candidate.Identity.IsSpawned || candidate.Identity.NetId == 0)
                return false;
            NetworkIdentity identity = candidate.Identity;
            candidate.NetworkHQ?.RemoveFactionUnit(candidate);
            candidate.ServerObjectManager.Destroy(identity, true);
            return !identity.IsSpawned && identity.NetId == 0;
        }
        catch (Exception e) { Log("state=drone_port_capture_destroy_failed error=" + e.GetType().Name); return false; }
    }

    private bool TryEvaluateDroneCarrierPortCapture(DroneCarrierPortWeapon port, Aircraft candidate, out CarrierSession? session,
                                                     bool animatedFinal = false)
    {
        session = null;
        if (candidate == null) return false;
        Aircraft? carrier = port.Carrier;
        bool server = GameManager.gameState == GameState.SinglePlayer && NetworkManagerNuclearOption.i != null &&
                      NetworkManagerNuclearOption.i.Server.Active;
        bool managed = sessionRegistry != null && sessionRegistry.TryGetByAircraft(candidate, out session);
        bool managedValid = !managed || (session != null && IsActiveSession(session) && ReferenceEquals(session.Home, carrier) &&
                                        session.Roster.Contains(candidate) && session.Manager.IsRegistered(candidate));
        bool carrierAuthority = carrier != null && carrier.IsServer && carrier.LocalSim && Registered(carrier);
        bool candidateAuthority = candidate != null && candidate.IsServer && candidate.LocalSim && Registered(candidate);
        bool alive = candidate!.definition != null && !candidate.disabled && !candidate.HasEjected() &&
                     candidate.unitState != Unit.UnitState.Abandoned && candidate.unitState != Unit.UnitState.Returned && candidate.rb != null;
        bool playerAssociated = carrier == null || carrier.Player == null || HasAnyPlayerAssociation(candidate, carrier.Player);
        bool sameHq = carrier != null && candidate.NetworkHQ == carrier.NetworkHQ;
        bool presence = animatedFinal && port.HasAnimatedProvider
            ? port.RecoveryEnabled && candidate.rb != null && port.HasAnimatedFinalOverlap(candidate.rb)
            : port.IsInTrigger(candidate);
        return managedValid && !port.IsReleaseCaptureSuppressed(candidate) && DroneCarrierPortLogic.CanCapture(server, !port.HasDescriptor, candidate.definition?.jsonKey == "kestrel",
            alive, playerAssociated, sameHq, presence, !managedValid, carrierAuthority, candidateAuthority,
            port.IsCaptureProjectionUsable);
    }

    private bool TryEvaluateDroneCarrierPortPlayerRecovery(DroneCarrierPortWeapon port, Aircraft candidate, bool callback, out CarrierSession session,
                                                            out string reason)
    {
        reason = ""; session = null!;
        if (!TryGetLocalSession(out session) || !IsActiveSession(session) || candidate == null || port.Carrier == null) { reason = "session"; return false; }
        Aircraft home = port.Carrier;
        bool server = GameManager.gameState == GameState.SinglePlayer && NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
        bool authority = ReferenceEquals(session.Player.Aircraft, candidate) && TryGetActiveLeader(session, out Aircraft leader) &&
                         ReferenceEquals(leader, candidate) && IsCompleteCurrentAuthority(candidate, session.Player);
        bool route = sessionRegistry != null && sessionRegistry.TryGetByAircraft(candidate, out CarrierSession routed) &&
                     ReferenceEquals(routed, session) && session.Roster.Contains(candidate) && session.Manager.IsRegistered(candidate) &&
                     candidate.definition?.jsonKey == "kestrel";
        bool homeIdentity = port.HasAnimatedProvider ? ReferenceEquals(home, port.Carrier) : home.definition?.jsonKey == carrierKey.Value;
        bool homeValid = ReferenceEquals(session.Home, home) && homeIdentity && home.IsServer && home.LocalSim && Registered(home) &&
                         !HasAnyPlayerAssociation(home, session.Player) && home.NetworkHQ == candidate.NetworkHQ;
        bool expectedSession = callback ? session.State == CarrierSessionState.PreSwitch && session.Busy && port.IsPlayerReserved(candidate) :
                                         session.State == CarrierSessionState.Complete && !session.Busy;
        bool ready = expectedSession && !session.HomeReturningToBase && SwitchGuards(session, candidate, home, false, out reason);
        bool presence = callback && port.HasAnimatedProvider
            ? port.RecoveryEnabled && candidate.rb != null && port.HasAnimatedFinalOverlap(candidate.rb)
            : port.IsInTrigger(candidate);
        bool admitted = DroneCarrierPortLogic.CanBeginPlayerRecovery(server, true, authority, route, homeValid, ready,
            presence, callback ? port.IsPlayerCallbackProjectionUsable(candidate) : port.IsCaptureProjectionUsable,
            callback ? port.IsPlayerReserved(candidate) : port.ReservedCandidate == null);
        if (!admitted && reason == "") reason = "player_recovery_guard";
        return admitted;
    }

    private IEnumerator CompleteDroneCarrierPortPlayerRecovery(DroneCarrierPortWeapon port, Aircraft candidate, CarrierSession s, ulong token,
                                                                long animatedToken = 0)
    {
        yield return null;
        if (token != s.SwitchGeneration)
        { if (animatedToken != 0 && port != null) { TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_generation"); FinishPlayerRecoveryFailure(s, port, candidate, s.Home ?? candidate, "generation"); } yield break; }
        if (port == null)
        {
            s.State = CarrierSessionState.Complete; s.Busy = false; s.SwitchCoroutine = null;
            s.Manager.Reconcile("port_player_capture_port_missing"); s.Context.Log("state=port_player_recovery_rejected reason=port_missing");
            yield break;
        }
        if (animatedToken != 0 && (port.AnimatedOperationToken != animatedToken || port.AnimatedMotion != AnimatedPortMotion.LockingCapture ||
            !port.IsPlayerReserved(candidate) || candidate.rb == null || !NativeRecoveryGuard.TryEvaluate(candidate, out _) ||
            port.Carrier == null || port.PortHardpoint == null || port.Station == null ||
            !port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) ||
            !TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, true, out CarrierSession animatedCurrent, out _) ||
            !ReferenceEquals(animatedCurrent, s) || !port.RecoveryEnabled || !port.HasAnimatedFinalOverlap(candidate.rb)))
        { TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_commit_guard"); FinishPlayerRecoveryFailure(s, port, candidate, s.Home ?? candidate, "callback"); yield break; }
        if (!(port.IsPlayerReserved(candidate) && port.ReservedCandidate == candidate &&
              TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, true, out CarrierSession current, out string reason) &&
              ReferenceEquals(current, s)))
        { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_callback"); FinishPlayerRecoveryFailure(s, port, candidate, s.Home!, "callback"); yield break; }
        if (!port.BeginPlayerHandoff(candidate)) { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_handoff"); FinishPlayerRecoveryFailure(s, port, candidate, s.Home!, "handoff"); yield break; }
        s.State = CarrierSessionState.Switching; s.Busy = true;
        Aircraft home = s.Home!;
        if (!SwitchGuards(s, candidate, home, false, out reason) ||
            (s.Cruise.IsCruisingCarrier(home) && !s.Cruise.RestoreBeforeTargeting(home, "port_player_recovery")))
        { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_preswitch"); FinishPlayerRecoveryFailure(s, port, candidate, home, reason == "" ? "cruise_restore" : reason); yield break; }
        OwnershipDiagnostics.Snapshot(Logger, "before_switch", "port_player_recovery", s.Player, candidate, home);
        try { backend!.Switch(s.Player, candidate, home); }
        catch (Exception e) { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_switch"); FinishPlayerRecoveryAfterSwitchFailure(s, port, candidate, home, "switch_" + e.GetType().Name); yield break; }
        for (int i = 0; i < Mathf.Clamp(verificationFrames.Value, 1, 30); ++i) yield return null;
        if (!TryVerify(s.Player, candidate, home)) { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_postcondition"); FinishPlayerRecoveryAfterSwitchFailure(s, port, candidate, home, "postcondition"); yield break; }
        InitializeVerifiedTargetCam(home);
        if (animatedToken != 0 && (port.AnimatedOperationToken != animatedToken || port.AnimatedMotion != AnimatedPortMotion.LockingCapture ||
            !port.IsPendingPlayerHandoff(candidate) || !ReferenceEquals(port.ReservedCandidate, candidate) || port.Carrier == null || port.Station == null ||
            port.PortHardpoint == null || !port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) ||
            !port.RecoveryEnabled || candidate.rb == null || !NativeRecoveryGuard.TryEvaluate(candidate, out _) ||
            !port.HasAnimatedFinalOverlap(candidate.rb)))
        { TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_final_overlap"); FinishPlayerRecoveryAfterSwitchFailure(s, port, candidate, home, "final_overlap"); yield break; }
        if (sessionRegistry == null || !sessionRegistry.TryDetachDrone(s, candidate, "port_player_capture", out reason))
        { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_detach"); FinishPlayerRecoveryFailure(s, port, candidate, home, "detach_" + reason); yield break; }
        UnsubscribeDrone(s, candidate);
        if (!TryDestroyDroneCarrierPortCapture(candidate))
        { if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_destroy_failed"); port.ClearAfterDetachedDestroyFailure(); FinishPlayerRecovery(s, "destroy_failed", "port_player_capture_destroy_failed"); yield break; }
        port.CommitPlayerHandoff(); if (animatedToken != 0) TryRequestAnimatedRetract(port, animatedToken, AnimatedPortMotion.LockingCapture, AnimatedPortMotion.RetractingCapture, "player_committed"); FinishPlayerRecovery(s, "verified");
    }

    private void CompletePortRemoval(CarrierSession session, string reconcile)
    {
        session.State = CarrierSessionState.Complete;
        session.Busy = false;
        session.ReleasePending = false;
        session.NextSwitchTime = Time.realtimeSinceStartup;
        try { session.Manager.Reconcile(reconcile); } catch (Exception e) { Log("state=port_removal reconcile=" + e.GetType().Name); }
        try { EnsureSessionRouter(); } catch (Exception e) { Log("state=port_removal router=" + e.GetType().Name); }
        try { WingmanSessionTransport.i?.PublishSnapshotToOwner(session); } catch (Exception e) { Log("state=port_removal publish=" + e.GetType().Name); }
    }

    private void FinishPlayerRecovery(CarrierSession s, string reason, string reconcile = "port_player_capture_verified")
    {
        s.State = CarrierSessionState.Complete; s.Busy = false; s.SwitchCoroutine = null;
        s.NextSwitchTime = Time.realtimeSinceStartup + SwitchCooldownSeconds(); s.Manager.Reconcile(reconcile);
        EnsureSessionRouter();
        WingmanSessionTransport.i?.PublishSnapshotToOwner(s);
        s.Context.Log("state=port_player_recovery " + reason);
    }
    private void FinishPlayerRecoveryFailure(CarrierSession s, DroneCarrierPortWeapon port, Aircraft oldAircraft, Aircraft home, string reason)
    {
        port.CancelPlayerHandoff(); s.State = CarrierSessionState.Complete; s.Busy = false; s.SwitchCoroutine = null;
        s.Manager.Reconcile("port_player_capture_rejected"); s.Context.Log("state=port_player_recovery_rejected reason=" + reason);
    }
    private void FinishPlayerRecoveryAfterSwitchFailure(CarrierSession s, DroneCarrierPortWeapon port, Aircraft oldAircraft, Aircraft home, string reason)
    {
        Ownership ownership = TryClassify(s.Player, oldAircraft, home);
        OwnershipDiagnostics.Snapshot(Logger, "failure_classified", "port_player_" + reason + "_" + ownership, s.Player, oldAircraft, home);
        if (ownership == Ownership.Partial) { Fatal(s, oldAircraft, home); return; }
        if (ownership == Ownership.Target) PreserveNativeAi(oldAircraft);
        FinishPlayerRecoveryFailure(s, port, oldAircraft, home, reason);
    }

    private bool TryRequestAnimatedRetract(DroneCarrierPortWeapon port, long token, AnimatedPortMotion expectedMotion,
                                           AnimatedPortMotion retractMotion, string reason)
    {
        if (!port.BeginAnimatedRetract(token, expectedMotion, retractMotion))
        { Log("state=animated_port_retract_rejected reason=" + reason + " stage=begin"); return false; }
        if (!port.TryAnimatedRetract(token))
        { Log("state=animated_port_retract_rejected reason=" + reason + " stage=command"); port.ResetAnimatedOperation(); return false; }
        return true;
    }

    private void FailAnimatedCapture(DroneCarrierPortWeapon port, Aircraft? candidate, long token, AnimatedPortMotion expectedMotion, string reason)
    {
        TryRequestAnimatedRetract(port, token, expectedMotion, AnimatedPortMotion.RetractingCapture, reason);
        bool playerPending = port.State == DroneCarrierPortState.PlayerReserved || port.State == DroneCarrierPortState.PlayerHandoff;
        if (playerPending)
        {
            if (candidate != null && TryGetLocalSession(out CarrierSession session) && ReferenceEquals(session.Player.Aircraft, candidate) &&
                sessionRegistry != null && sessionRegistry.TryGetByAircraft(candidate, out CarrierSession routed) && ReferenceEquals(routed, session))
                FinishPlayerRecoveryFailure(session, port, candidate, session.Home ?? candidate, reason);
            else
            {
                port.CancelPlayerHandoff();
                if (TryGetLocalSession(out CarrierSession local) && ReferenceEquals(local.Home, port.Carrier) &&
                    (local.State == CarrierSessionState.PreSwitch || local.State == CarrierSessionState.Switching || local.Busy))
                {
                    local.State = CarrierSessionState.Complete; local.Busy = false; local.SwitchCoroutine = null;
                    try { local.Manager.Reconcile("port_player_capture_rejected"); }
                    catch (Exception e) { Log("state=port_player_recovery_cleanup_failed error=" + e.GetType().Name); }
                    try { local.Context.Log("state=port_player_recovery_rejected reason=" + reason); }
                    catch (Exception e) { Log("state=port_player_recovery_cleanup_log_failed error=" + e.GetType().Name); }
                }
            }
        }
        else
            port.Cancel();
    }

    private void UnsubscribeDrone(CarrierSession s, Aircraft drone)
    {
        if (drone != null && s.SubscribedDrones.Remove(drone))
            drone.onDisableUnit -= OnDroneDisabled;
    }

    private void ClearDroneSubscriptions(CarrierSession s, HashSet<Aircraft>? protectedPending = null)
    {
        foreach (Aircraft drone in s.SubscribedDrones)
            if (drone != null && (protectedPending == null || !protectedPending.Contains(drone)))
                drone.onDisableUnit -= OnDroneDisabled;
        if (protectedPending == null) s.SubscribedDrones.Clear();
        else s.SubscribedDrones.RemoveWhere(drone => !protectedPending.Contains(drone));
    }

    private void OnDroneDisabled(Unit disabledUnit)
    {
        if (!(disabledUnit is Aircraft oldDrone))
            return;
        if (sessionRegistry == null || !sessionRegistry.TryGetByAircraft(oldDrone, out CarrierSession s))
            return;
        if (oldDrone == s.Home)
            return;
        if (TryQueueEmergencyReturn(s, oldDrone)) return;
        bool wasReturning = s.Manager.IsReturningToBase(oldDrone);
        int slot = -1;
        s.Manager.TryGetMapStatus(oldDrone, out slot, out _, out _, out _, out _);
        string label = slot < 0 ? "unknown" : "FQ-" + (slot + 1);
        Unit.UnitState unitState = oldDrone.unitState;
        CompleteOrdinaryDisabledDrone(s, oldDrone, slot, label, wasReturning, unitState);
    }
    private void CompleteOrdinaryDisabledDrone(CarrierSession s, Aircraft oldDrone, int slot = -1, string label = "unknown",
                                               bool wasReturning = false, Unit.UnitState unitState = default)
    {
        if (unitState == Unit.UnitState.Returned && wasReturning)
            s.Context.Log("state=rtb_completed asset=fq pid=" + PidOf(oldDrone) + " slot=" + slot + " label=" + label);
        UnsubscribeDrone(s, oldDrone);
        string detachReason = "registry";
        if (sessionRegistry == null || !sessionRegistry.TryDetachDrone(s, oldDrone, "disabled", out detachReason))
        {
            s.Context.Log("event=session_detach_failed drone_pid=" + PidOf(oldDrone) + " reason=" + detachReason);
            return;
        }
        if (s.Home == null && !HasSurvivingFq(s))
            CleanupSession(s, "assets_lost");
        else
            WingmanSessionTransport.i?.PublishSnapshotToOwner(s);
    }

    private bool TryQueueEmergencyReturn(CarrierSession s, Aircraft oldDrone)
    {
        bool requestAndSessionAvailable = pendingEmergency == null && !s.Busy && s.State == CarrierSessionState.Complete &&
            IsActiveSession(s) && backend != null && backend.TryReady(out _) && TryGetLocalSession(out CarrierSession local) &&
            ReferenceEquals(local, s) && sessionRegistry != null && sessionRegistry.TryGetByAircraft(oldDrone, out CarrierSession routed) && ReferenceEquals(routed, s);
        bool managedCurrentSource = s.Manager.IsRegistered(oldDrone) && s.Roster.Contains(oldDrone) && !ReferenceEquals(oldDrone, s.Home) &&
            ReferenceEquals(s.Player.Aircraft, oldDrone) && GameManager.GetLocalAircraft(out Aircraft current) && ReferenceEquals(current, oldDrone);
        bool authoritativeSpawnedSource = IsCompleteCurrentAuthority(oldDrone, s.Player) && oldDrone.Identity != null &&
            oldDrone.Identity.IsSpawned && oldDrone.Identity.NetId != 0;
        bool validExactHome = s.Home != null && !s.HomeReturningToBase && ReferenceEquals(s.Home.NetworkHQ, oldDrone.NetworkHQ) &&
            !HasAnyPlayerAssociation(s.Home, s.Player) && NativeRecoveryGuard.TryEvaluate(s.Home, out _);
        if (!AircraftSwitchLogic.CanQueueEmergencyReturn(requestAndSessionAvailable, managedCurrentSource, authoritativeSpawnedSource, validExactHome)) return false;
        Aircraft home = s.Home!;
        pendingEmergency = new EmergencyReturn(s, oldDrone, home, ++s.SwitchGeneration);
        s.State = CarrierSessionState.Switching; s.Busy = true;
        s.Context.Log("state=emergency_return_queued reason=disabled old_pid=" + PidOf(oldDrone) + " home_pid=" + PidOf(s.Home));
        s.SwitchCoroutine = StartCoroutine(EmergencyReturnNextFrame(pendingEmergency));
        return true;
    }
    private IEnumerator EmergencyReturnNextFrame(EmergencyReturn request)
    {
        yield return null;
        CarrierSession s = request.session;
        bool owns = false;
        try
        {
            owns = AircraftSwitchLogic.OwnsEmergencyReturnRequest(ReferenceEquals(pendingEmergency, request), IsActiveSession(s), s.SwitchGeneration == request.generation);
            if (!owns) yield break;
            if (pendingEmergency != request || !IsActiveSession(s) || s.SwitchGeneration != request.generation || !s.Busy ||
                sessionRegistry == null || !sessionRegistry.TryGetByAircraft(request.oldDrone, out CarrierSession routed) ||
                !ReferenceEquals(routed, s) || !s.Manager.IsRegistered(request.oldDrone) || request.oldDrone.Identity == null ||
                !request.oldDrone.Identity.IsSpawned || request.oldDrone.Identity.NetId == 0 ||
                s.State != CarrierSessionState.Switching || !ReferenceEquals(s.Player.Aircraft, request.oldDrone) || !IsCompleteCurrentAuthority(request.oldDrone, s.Player) ||
                !GameManager.GetLocalAircraft(out Aircraft local) || !ReferenceEquals(local, request.oldDrone) ||
                !ReferenceEquals(s.Home, request.home) || s.HomeReturningToBase || !NativeRecoveryGuard.TryEvaluate(request.home, out _) ||
                HasAnyPlayerAssociation(request.home, s.Player) || request.home.NetworkHQ != request.oldDrone.NetworkHQ || backend == null)
            { CompleteOrdinaryDisabledDrone(s, request.oldDrone); s.Context.Log("state=emergency_return_rejected reason=guard"); yield break; }
            if (s.Cruise.IsCruisingCarrier(request.home) && !s.Cruise.RestoreBeforeTargeting(request.home, "emergency_return"))
            { CompleteOrdinaryDisabledDrone(s, request.oldDrone); s.Context.Log("state=emergency_return_rejected reason=carrier_cruise_restore_failed"); yield break; }
            try { backend.Switch(s.Player, request.oldDrone, request.home); }
            catch (Exception e) { s.Context.Log("state=emergency_return_switch_exception error=" + e.GetType().Name); }
            for (int i = 0; i < Mathf.Clamp(verificationFrames.Value, 1, 30); i++) yield return null;
            if (!AircraftSwitchLogic.OwnsEmergencyReturnRequest(ReferenceEquals(pendingEmergency, request), IsActiveSession(s), s.SwitchGeneration == request.generation)) yield break;
            Ownership ownership = EmergencyVerified(s.Player, request.oldDrone, request.home) ? Ownership.Target : TryClassify(s.Player, request.oldDrone, request.home);
            EmergencyReturnOutcome outcome = AircraftSwitchLogic.ResolveEmergencyReturnOutcome(ownership == Ownership.Old, ownership == Ownership.Target);
            if (outcome == EmergencyReturnOutcome.DetachOld) { CompleteOrdinaryDisabledDrone(s, request.oldDrone); yield break; }
            if (outcome == EmergencyReturnOutcome.FatalPartial) { Fatal(s, request.oldDrone, request.home); owns = false; yield break; }
            CompleteEmergencyTarget(s, request);
        }
        finally
        {
            if (owns && AircraftSwitchLogic.OwnsEmergencyReturnRequest(ReferenceEquals(pendingEmergency, request), IsActiveSession(s), s.SwitchGeneration == request.generation))
            { pendingEmergency = null; s.SwitchCoroutine = null; s.Busy = false; s.State = CarrierSessionState.Complete; }
        }
    }
    private void CompleteEmergencyTarget(CarrierSession s, EmergencyReturn request)
    {
        UnsubscribeDrone(s, request.oldDrone);
        string reason = "registry";
        if (sessionRegistry == null || !sessionRegistry.TryDetachDrone(s, request.oldDrone, "emergency_disabled", out reason)) s.Context.Log("event=session_detach_failed drone_pid=" + PidOf(request.oldDrone) + " reason=" + reason);
        s.State = CarrierSessionState.Complete; s.Busy = false;
        InitializeVerifiedTargetCam(request.home); RefreshVerifiedWeaponStatus(request.home); s.NextSwitchTime = Time.realtimeSinceStartup + SwitchCooldownSeconds();
        s.Manager.Reconcile("emergency_return_verified"); WingmanSessionTransport.i?.PublishSnapshotToOwner(s);
        s.Context.Log("state=emergency_return_verified reason=switched old_pid=" + PidOf(request.oldDrone)); s.Reporter.EmergencyReturned();
    }
    private static bool EmergencyVerified(Player player, Aircraft oldDrone, Aircraft home)
    {
        try
        {
            return IsCompleteCurrentAuthority(home, player) && GameManager.GetLocalAircraft(out Aircraft local) && local == home &&
                     CombatHUD.i != null && CombatHUD.i.aircraft == home && !HasAnyPlayerAssociation(oldDrone, player);
        }
        catch { return false; }
    }
    private sealed class EmergencyReturn
    {
        internal readonly CarrierSession session; internal readonly Aircraft oldDrone, home; internal readonly ulong generation;
        internal EmergencyReturn(CarrierSession session, Aircraft oldDrone, Aircraft home, ulong generation)
        { this.session = session; this.oldDrone = oldDrone; this.home = home; this.generation = generation; }
    }

    private IEnumerator WaitAndSwitch(CarrierSession s, Aircraft oldAircraft, Aircraft target, string direction, bool takeOverReturningFq = false)
    {
        if (!SwitchGuards(s, oldAircraft, target, takeOverReturningFq, out string reason))
        {
            HandlePreSwitchFailure(s, oldAircraft, target, direction, reason);
            yield break;
        }
        if (s.Cruise.IsCruisingCarrier(target) && !s.Cruise.RestoreBeforeTargeting(target, "switch_target"))
        {
            HandlePreSwitchFailure(s, oldAircraft, target, direction, "carrier_cruise_restore_failed");
            yield break;
        }
        if (takeOverReturningFq && !s.Manager.PrepareReturningFqForTakeControl(target, out reason))
        {
            HandlePreSwitchFailure(s, oldAircraft, target, direction, reason);
            yield break;
        }
        if (s.Manager.IsRegistered(target) && !s.Manager.Suspend(target, "switch_target"))
        {
            s.Context.Log("state=follow_suspend_failed reason=switch_target target=" + NameOf(target));
            s.State = CarrierSessionState.Complete;
            Reject(s, "follow_suspend_failed", direction, oldAircraft, target);
            yield break;
        }
        OwnershipDiagnostics.Snapshot(Logger, "before_switch", "switch", s.Player, oldAircraft, target);
        try
        {
            backend!.Switch(s.Player, oldAircraft, target);
        }
        catch (Exception e)
        {
            FailSwitch(s, oldAircraft, target, direction, "switch_" + e.GetType().Name);
            yield break;
        }
        for (int i = 0; i < Mathf.Clamp(verificationFrames.Value, 1, 30); i++)
            yield return null;
        if (TryVerify(s.Player, oldAircraft, target))
        {
            backend!.LogJointSnapshot(target, "verified");
            StartCoroutine(LogVerifiedTargetJointSnapshots(target));
            InitializeVerifiedTargetCam(target);
            RefreshVerifiedWeaponStatus(target);
            OwnershipDiagnostics.Snapshot(Logger, "verified", "success", s.Player, oldAircraft, target);
            PreserveNativeAi(oldAircraft);
            if (AircraftSwitchLogic.ShouldInstallCarrierCruise(ReferenceEquals(oldAircraft, s.Home), target.definition?.jsonKey == droneKey.Value))
                s.Cruise.TryInstall(s.Player, oldAircraft, target);
            s.State = CarrierSessionState.Complete;
            s.NextSwitchTime = Time.realtimeSinceStartup + SwitchCooldownSeconds();
            s.Manager.Reconcile("switch_verified");
            s.Context.Log("state=switch_verified direction=" + direction);
            yield break;
        }
        FailSwitch(s, oldAircraft, target, direction, "postcondition");
    }

    private void InitializeVerifiedTargetCam(Aircraft aircraft)
    {
        try
        {
            backend!.InitializeTargetCamAfterVerifiedSwitch(aircraft);
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Log("state=target_cam_initialize_failed error=" + e.GetType().Name + " pid=" + PidOf(aircraft));
        }
    }
    private void RefreshVerifiedWeaponStatus(Aircraft aircraft)
    {
        try
        {
            if (backend!.TryRefreshWeaponStatusAfterVerifiedSwitch(aircraft, out int stationIndex))
                Log("state=weapon_status_refresh pid=" + PidOf(aircraft) + " station=" + stationIndex);
            else
                Log("state=weapon_status_refresh_skipped reason=required_objects_missing pid=" + PidOf(aircraft));
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Log("state=weapon_status_refresh_failed error=" + e.GetType().Name + " pid=" + PidOf(aircraft));
        }
    }
    private IEnumerator LogVerifiedTargetJointSnapshots(Aircraft target)
    {
        yield return new WaitForFixedUpdate();
        if (target == null || target.disabled) yield break;
        backend?.LogJointSnapshot(target, "post_fixed_1");
        yield return new WaitForFixedUpdate();
        if (target == null || target.disabled) yield break;
        backend?.LogJointSnapshot(target, "post_fixed_2");
    }
    internal bool TryGetFqLoadoutDefinition(out AircraftDefinition definition)
    {
        definition = null!;
        if (Encyclopedia.Lookup == null || !Encyclopedia.Lookup.TryGetValue(droneKey.Value, out UnitDefinition unit) ||
            unit is not AircraftDefinition aircraft || aircraft.unitPrefab == null ||
            aircraft.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets == null ||
            aircraft.aircraftParameters == null)
            return false;
        definition = aircraft;
        return true;
    }
    internal int GetSelectedFqCradleCount(Aircraft preview)
    {
        if (preview == null || preview.weaponManager?.hardpointSets == null) return 0;
        int count = 0;
        foreach (HardpointSet set in preview.weaponManager.hardpointSets)
            foreach (Hardpoint hardpoint in set.hardpoints)
                if (hardpoint.mount != null && hardpoint.mount.jsonKey == PayloadRegistry.Key) ++count;
        return count;
    }
    internal bool TryCreateDefaultFqLoadoutPlan(int cradleCount, out CarrierFqLoadoutPlan? plan, out string reason)
    {
        plan = null;
        reason = "";
        if (!TryGetFqLoadoutDefinition(out AircraftDefinition definition))
        {
            reason = "definition";
            return false;
        }
        HardpointSet[] sets = definition.unitPrefab.GetComponent<Aircraft>()!.weaponManager.hardpointSets;
        List<WeaponMount>? defaults = definition.aircraftParameters.loadouts != null &&
                                      definition.aircraftParameters.loadouts.Count > 1
                                          ? definition.aircraftParameters.loadouts[1]?.weapons
                                          : null;
        if (defaults == null)
        {
            reason = "default_missing";
            return false;
        }
        if (defaults.Count != sets.Length)
        {
            reason = "count";
            return false;
        }
        string?[] keys = new string?[sets.Length];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = defaults[i]?.jsonKey;

        int roleIndex = WingmanLoadoutLogic.RoleHardpointSetIndex < sets.Length
                            ? WingmanLoadoutLogic.RoleHardpointSetIndex
                            : -1;
        int irIndex = -1;
        WeaponMount? irMount = null;
        for (int i = 0; i < sets.Length && irMount == null; i++)
        {
            if (i == roleIndex)
                continue;
            GameObject? weaponPrefab = defaults[i]?.info?.weaponPrefab;
            if (weaponPrefab?.GetComponent<IRSeeker>() == null && weaponPrefab?.GetComponent<ARHSeeker>() == null)
                continue;
            irMount = FirstLegalIrMount(sets[i]);
            if (irMount != null)
                irIndex = i;
        }
        for (int i = 0; i < sets.Length && irMount == null; i++)
        {
            if (i == roleIndex)
                continue;
            irMount = FirstLegalIrMount(sets[i]);
            if (irMount != null)
                irIndex = i;
        }

        string? armKey = null;
        string? antiShipKey = null;
        string? aamKey = null;
        if (roleIndex >= 0 && sets[roleIndex]?.weaponOptions != null)
            foreach (WeaponMount mount in sets[roleIndex].weaponOptions)
            {
                if (mount == null || mount.NotAllowed(MissionManager.AllowEventContent))
                    continue;
                if (mount.jsonKey == "ARM1_single")
                    armKey = mount.jsonKey;
                else if (mount.jsonKey == "AShM1_single")
                    antiShipKey = mount.jsonKey;
                else if (mount.jsonKey == "AAM2_double")
                    aamKey = mount.jsonKey;
            }
        if (!FqCradleLoadoutLogic.TryCreateResetPlan(cradleCount, keys, irIndex, irMount?.jsonKey, roleIndex, aamKey, armKey,
                                                     antiShipKey, out plan))
        {
            reason = "reset_invalid";
            return false;
        }
        if (irMount == null)
            Log("state=fq_loadout_ir_default_fallback reason=no_legal_ir");
        return true;
    }
    private static WeaponMount? FirstLegalIrMount(HardpointSet? set)
    {
        if (set?.weaponOptions == null)
            return null;
        foreach (WeaponMount mount in set.weaponOptions)
            if (mount != null && !mount.NotAllowed(MissionManager.AllowEventContent) &&
                mount.info?.weaponPrefab?.GetComponent<IRSeeker>() != null)
                return mount;
        return null;
    }
    internal void SetActiveFqLoadoutPlan(CarrierFqLoadoutPlan plan)
    {
        localFqLoadoutDraft = plan.Copy();
        fqLoadoutPlanDirty = true;
        PublishDirtyFqLoadoutPlan(NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active);
    }
    private bool PublishDirtyFqLoadoutPlan(bool server)
    {
        if (!fqLoadoutPlanDirty) return true;
        if (server || GameManager.gameState == GameState.SinglePlayer)
        {
            fqLoadoutPlanDirty = false;
            return true;
        }
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (localFqLoadoutDraft != null && transport != null && transport.SendLoadoutPlan(localFqLoadoutDraft))
            fqLoadoutPlanDirty = false;
        return !fqLoadoutPlanDirty;
    }
    internal bool TryGetRemoteFqLoadoutDimensions(INetworkPlayer sender, out Player? player, out Aircraft? aircraft,
                                                  out int cradleCount, out int hardpoints, out string reason)
    {
        player = sender?.Identity?.GetComponent<Player>();
        aircraft = player?.Aircraft;
        cradleCount = hardpoints = 0;
        if (player == null || aircraft == null || !ReferenceEquals(player.Owner, sender) ||
            !IsCompleteCurrentAuthority(aircraft, player)) { reason = "sender_authority"; return false; }
        if (!TryGetFqLoadoutDefinition(out AircraftDefinition definition) ||
            definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets == null)
        { reason = "hardpoint_definition"; return false; }
        cradleCount = GetSelectedFqCradleCount(aircraft);
        hardpoints = definition.unitPrefab.GetComponent<Aircraft>()!.weaponManager.hardpointSets.Length;
        reason = "";
        return true;
    }
    internal void RejectRemoteFqLoadoutPlan(string reason) =>
        Log("state=fq_loadout_plan accepted=false reason=" + reason);
    internal void TrySetRemoteFqLoadoutPlan(INetworkPlayer sender, CarrierFqLoadoutPlan plan)
    {
        string reason = "";
        try
        {
            if (!TryGetRemoteFqLoadoutDimensions(sender, out _, out Aircraft? aircraft, out int cradleCount, out int hardpoints,
                                                  out reason)) { }
            else if (plan.CradleCount != cradleCount) reason = "cradle_count";
            else if (plan.HardpointCount != hardpoints) reason = "hardpoint_count";
            else
            {
                CarrierFqLoadoutPlan copy = plan.Copy();
                pendingRemoteFqLoadoutPlans[sender] = copy;
                if (sessionRegistry != null && sessionRegistry.TryGetByOwner(sender, out CarrierSession session) &&
                    ReferenceEquals(session.Home, aircraft))
                    session.LoadoutPlan = copy.Copy();
                Log("state=fq_loadout_plan accepted=true cradles=" + copy.CradleCount + " hardpoints=" + copy.HardpointCount);
                return;
            }
        }
        catch (Exception e) { reason = "exception_" + e.GetType().Name; }
        Log("state=fq_loadout_plan accepted=false reason=" + reason);
    }
    internal void ClearActiveFqLoadoutPlan(string reason)
    {
        if (localFqLoadoutDraft != null)
            Log("state=fq_loadout_cleared reason=" + reason);
        localFqLoadoutDraft = null;
        fqLoadoutPlanDirty = false;
    }
    private NuclearOption.SavedMission.Loadout? BuildFqLoadout(AircraftDefinition definition, int physicalCradleId, int physicalRound,
                                                                string?[]? row, Aircraft carrier)
    {
        if (row == null)
            return BuildRoleLoadout(definition, physicalRound);
        int hp = -1;
        string key = "null";
        string reason = "";
        try
        {
            Aircraft? prefab = definition.unitPrefab?.GetComponent<Aircraft>();
            HardpointSet[]? sets = prefab?.weaponManager?.hardpointSets;
            List<WeaponMount>? defaults = definition.aircraftParameters.loadouts != null &&
                                          definition.aircraftParameters.loadouts.Count > 1
                                              ? definition.aircraftParameters.loadouts[1]?.weapons
                                              : null;
            if (defaults == null)
            {
                reason = "default_missing";
                throw new InvalidOperationException();
            }
            if (sets == null || row.Length != sets.Length || defaults.Count != sets.Length)
            {
                reason = "count";
                throw new InvalidOperationException();
            }
            List<WeaponMount> weapons = new List<WeaponMount>(defaults);
            for (int i = 0; i < sets.Length; i++)
            {
                hp = i;
                key = row[i] ?? "null";
                if (row[i] == null)
                {
                    weapons[i] = null!;
                    continue;
                }
                if (sets[i]?.weaponOptions == null)
                {
                    reason = "options";
                    throw new InvalidOperationException();
                }
                WeaponMount? selected = null;
                foreach (WeaponMount option in sets[i].weaponOptions)
                    if (option != null && option.jsonKey == row[i])
                    {
                        selected = option;
                        break;
                    }
                if (selected == null)
                {
                    reason = "missing_option";
                    throw new InvalidOperationException();
                }
                if (selected.NotAllowed(MissionManager.AllowEventContent))
                {
                    reason = "blocked";
                    throw new InvalidOperationException();
                }
                weapons[i] = selected;
            }
            NuclearOption.SavedMission.Loadout loadout = new NuclearOption.SavedMission.Loadout { weapons = weapons };
            for (int i = 0; i < sets.Length; i++)
                if (!WeaponChecker.MountAllowedConflict(sets[i], loadout))
                {
                    hp = i;
                    key = row[i] ?? "null";
                    reason = "conflict";
                    throw new InvalidOperationException();
                }
            List<string> selectedKeys = new List<string>();
            foreach (string? value in row)
                selectedKeys.Add(value ?? "empty");
            Log("state=fq_loadout_selected carrier_pid=" + PidOf(carrier) + " physical_cradle=" + physicalCradleId + " physical_round=" + physicalRound + " keys=" +
                string.Join(",", selectedKeys));
            return loadout;
        }
        catch (Exception e)
        {
            if (reason == "")
                reason = "exception_" + e.GetType().Name;
            Log("state=fq_loadout_fallback physical_cradle=" + physicalCradleId + " physical_round=" + physicalRound + " hp=" + hp + " key=" + key + " reason=" +
                reason);
            return BuildRoleLoadout(definition, physicalRound);
        }
    }
    private NuclearOption.SavedMission.Loadout? BuildRoleLoadout(AircraftDefinition definition, int physicalRound)
    {
        WingmanLoadoutPlan plan = WingmanLoadoutLogic.PlanForPhysicalRound(physicalRound);
        if (!plan.UsesCustomLoadout)
            return null;
        Aircraft? prefab = definition.unitPrefab.GetComponent<Aircraft>();
        WeaponManager? manager = prefab?.weaponManager;
        List<WeaponMount>? defaults = definition.aircraftParameters.loadouts != null &&
                                      definition.aircraftParameters.loadouts.Count > 1
                                          ? definition.aircraftParameters.loadouts[1]?.weapons
                                          : null;
        int defaultCount = defaults?.Count ?? -1;
        int hardpointCount = manager?.hardpointSets?.Length ?? -1;
        WeaponMount? selected = null;
        HardpointSet? roleSet = null;
        if (manager != null && manager.hardpointSets != null &&
            manager.hardpointSets.Length > WingmanLoadoutLogic.RoleHardpointSetIndex)
            roleSet = manager.hardpointSets[WingmanLoadoutLogic.RoleHardpointSetIndex];
        if (roleSet?.weaponOptions != null)
            foreach (WeaponMount candidate in roleSet.weaponOptions)
                if (candidate != null && candidate.jsonKey == plan.mountKey)
                {
                    selected = candidate;
                    break;
                }
        if (!WingmanLoadoutLogic.HasCompleteLoadout(plan, defaultCount, hardpointCount, selected != null,
                                                    selected != null && selected.NotAllowed(MissionManager.AllowEventContent), out string reason))
        {
            Log("state=role_loadout_fallback physical_round=" + physicalRound + " role=" + plan.role + " mount=" +
                plan.mountKey + " reason=" + reason);
            return null;
        }
        NuclearOption.SavedMission.Loadout loadout = new NuclearOption.SavedMission.Loadout
        {
            weapons = new List<WeaponMount>(defaults!)
        };
        loadout.weapons[WingmanLoadoutLogic.RoleHardpointSetIndex] = selected!;
        Log("state=role_loadout_selected physical_round=" + physicalRound + " role=" + plan.role + " mount=" + plan.mountKey);
        return loadout;
    }

    private void HandlePreSwitchFailure(CarrierSession s, Aircraft oldAircraft, Aircraft target, string direction,
                                      string reason)
    {
        if (!IsCompleteCurrentAuthority(oldAircraft, s.Player))
        {
            Fatal(s, oldAircraft, target);
            return;
        }
        s.State = CarrierSessionState.Complete;
        Reject(s, reason, direction, oldAircraft, target);
    }
    private void FailSwitch(CarrierSession s, Aircraft oldAircraft, Aircraft target, string direction, string reason)
    {
        Ownership ownership = TryClassify(s.Player, oldAircraft, target);
        OwnershipDiagnostics.Snapshot(Logger, "failure_classified", reason + "_" + ownership, s.Player, oldAircraft,
                                      target);
        if (ownership == Ownership.Old)
        {
            s.State = CarrierSessionState.Complete;
            s.Manager.Reconcile("switch_rejected");
            Reject(s, reason, direction, oldAircraft, target);
        }
        else if (ownership == Ownership.Target)
        {
            PreserveNativeAi(oldAircraft);
            s.State = CarrierSessionState.Complete;
            s.NextSwitchTime = Time.realtimeSinceStartup + SwitchCooldownSeconds();
            s.Context.Log("state=switch_recovered_target reason=" + reason);
        }
        else
            Fatal(s, oldAircraft, target);
    }
    private float SwitchCooldownSeconds() => Mathf.Clamp(switchCooldown.Value, .05f, 3f);
    private bool SwitchGuards(CarrierSession s, Aircraft oldAircraft, Aircraft target, bool takeOverReturningFq, out string reason)
    {
        reason = "";
        if (backend == null || !backend.TryReady(out reason))
            return false;
        if (target == s.Home ? s.HomeReturningToBase : s.Manager.IsReturningToBase(target) && !takeOverReturningFq)
        {
            reason = "rtb_in_progress";
            return false;
        }
        if (GameManager.gameState != GameState.SinglePlayer || !StaticEligible(oldAircraft) || oldAircraft == target)
        {
            reason = "invalid_aircraft";
            return false;
        }
        if (!NativeRecoveryGuard.TryEvaluate(target, out reason))
            return false;
        try
        {
            if (s.Player == null || s.Player.Identity == null || s.Player.Identity.Owner == null ||
                s.Player.Owner == null || s.Player.HQ == null || s.Player.Aircraft != oldAircraft ||
                !IsCompleteCurrentAuthority(oldAircraft, s.Player) || HasAnyPlayerAssociation(target, s.Player) ||
                target.NetworkHQ != s.Player.HQ)
            {
                reason = "ownership";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            reason = "guard_" + e.GetType().Name;
            return false;
        }
    }
    private static bool StaticEligible(Aircraft a) => a != null && !a.disabled && Registered(a) && a.Identity != null
                                                      && a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null
                                                      && a.NetworkHQ != null;
    private static bool TryVerify(Player p, Aircraft oldAircraft, Aircraft target)
    {
        try
        {
            return StaticEligible(oldAircraft) && StaticEligible(target) && p != null && p.Identity != null &&
                   p.Identity.Owner != null && p.Owner != null && p.HQ != null && p.Aircraft == target &&
                   GameManager.GetLocalAircraft(out Aircraft local) && local == target && CombatHUD.i != null &&
                   CombatHUD.i.aircraft == target && IsCompleteCurrentAuthority(target, p) &&
                   !HasAnyPlayerAssociation(oldAircraft, p) && oldAircraft.Identity.Owner != p.Identity.Owner &&
                   target.NetworkHQ == p.HQ;
        }
        catch
        {
            return false;
        }
    }
    private enum Ownership
    {
        Old,
        Target,
        Partial
    }
    private static Ownership TryClassify(Player p, Aircraft oldAircraft, Aircraft target)
    {
        try
        {
            bool old = HasAnyPlayerAssociation(oldAircraft, p), next = HasAnyPlayerAssociation(target, p);
            return IsCompleteCurrentAuthority(oldAircraft, p) && !next ? Ownership.Old
                   : IsCompleteCurrentAuthority(target, p) && !old ? Ownership.Target
                                                                       : Ownership.Partial;
        }
        catch
        {
            return Ownership.Partial;
        }
    }
    // A non-null NetworkIdentity.Owner is authority, while null is intentionally treated as server/default, not a
    // player.
    private static bool IsCompleteCurrentAuthority(Aircraft a, Player p)
    {
        if (a == null || p == null || p.Identity == null || p.Identity.Owner == null || a.Identity == null ||
            a.Identity.Owner != p.Identity.Owner || a.Player != p || a.pilots == null || a.pilots.Length == 0 ||
            a.pilots[0] == null || a.pilots[0].player != p)
            return false;
        return UnitRegistry.TryGetPersistentUnit(a.persistentID, out PersistentUnit u) && u != null && u.player == p;
    }
    private static bool HasAnyPlayerAssociation(Aircraft a, Player p)
    {
        try
        {
            if (a == null || p == null || p.Identity == null || p.Identity.Owner == null || a.Identity == null)
                return true;
            if (a.Player != null || a.pilots == null || a.pilots.Length == 0 || a.pilots[0] == null ||
                a.pilots[0].player != null)
                return true;
            if (a.Identity.Owner != null)
                return true;
            return !UnitRegistry.TryGetPersistentUnit(a.persistentID, out PersistentUnit u) || u == null ||
                   u.player != null;
        }
        catch
        {
            return true;
        }
    }
    private static bool CanSafelyDespawn(Aircraft a, Player p)
    {
        try
        {
            return !HasAnyPlayerAssociation(a, p);
        }
        catch
        {
            return false;
        }
    }
    internal bool MapGetRoster(List<Aircraft> drones, out Aircraft? home)
    {
        drones.Clear();
        home = null;
        if (TryGetLocalSession(out CarrierSession s))
        {
            home = s.Home;
            foreach (Aircraft aircraft in s.Roster)
                if (aircraft != null && aircraft != s.Home && s.Manager.IsRegistered(aircraft))
                    drones.Add(aircraft);
            return s.State == CarrierSessionState.Complete && !s.Busy;
        }
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        return transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
               transport.ResolveSnapshotRoster(drones, out home);
    }
    internal bool MapTryReturnToBase(Aircraft aircraft, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        return aircraft == s.Home ? TryReturnHomeToBase(s, aircraft, out reason) :
               s.Manager.TryReturnToBase(aircraft, out reason);
    }
    internal bool MapCancelReturnToBase(Aircraft aircraft, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        return aircraft == s.Home ? TryCancelHomeReturnToBase(s, aircraft, out reason) :
               s.Manager.TryCancelReturnToBase(aircraft, out reason);
    }
    internal void MapReportRtbOrdered(int successCount)
    {
        if (successCount > 0 && MapCommandSessionReady(out CarrierSession s, out _))
            SendLwChat(s, "[LW] RTB ordered for " + successCount + " aircraft.");
    }
    internal void MapReportRtbCancelled(int successCount)
    {
        if (successCount > 0 && MapCommandSessionReady(out CarrierSession s, out _))
            SendLwChat(s, "[LW] RTB cancelled for " + successCount + " aircraft.");
    }
    internal bool MapIsReturningToBase(Aircraft aircraft)
    {
        return TryGetLocalSession(out CarrierSession s) &&
               (s.HomeReturningToBase && aircraft == s.Home || s.Manager.IsReturningToBase(aircraft));
    }
    internal bool MapTakeControl(Aircraft target, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!TryGetActiveLeader(s, out Aircraft current))
        {
            reason = "session_not_ready";
            return false;
        }
        if (!GameManager.GetLocalPlayer(out Player player) || player != s.Player ||
            (target != s.Home && !s.Roster.Contains(target)))
        {
            reason = "not_in_roster";
            return false;
        }
        if (target == s.Home && s.HomeReturningToBase)
        {
            reason = "rtb_in_progress";
            return false;
        }
        bool returningFq = target != s.Home && s.Manager.IsReturningToBase(target);
        bool validTarget = !target.disabled && (target == s.Home || s.Manager.IsRegistered(target));
        if (!TakeControlAdmissionLogic.CanTakeControl(true, validTarget, target == current, false))
        {
            reason = "already_controlled";
            return false;
        }
        if (Time.realtimeSinceStartup < s.NextSwitchTime)
        {
            reason = "cooldown";
            return false;
        }
        s.State = CarrierSessionState.Switching;
        Log("state=switch_map_requested from=" + NameOf(current) + " to=" + NameOf(target));
        StartSwitch(s, current, target, "map_panel", returningFq);
        return true;
    }
    internal bool MapSetMode(Aircraft drone, DroneModeOverride mode, out string reason)
    {
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (transport == null || transport.Identity == null || !transport.Identity.IsSpawned)
        {
            reason = "transport_unavailable";
            return false;
        }
        if (TryGetLocalSession(out CarrierSession s) && IsActiveSession(s) && s.Manager.IsRegistered(drone))
            return transport.RequestSetMode(s.SessionId, drone, mode, s.Owner, out reason);
        if (transport.SnapshotReady && transport.SnapshotSessionId != 0 && drone != null &&
            transport.ContainsSnapshotPid(drone.persistentID.Id))
            return transport.RequestSetMode(transport.SnapshotSessionId, drone, mode, null, out reason);
        reason = "session_unavailable";
        return false;
    }
    private bool MapCommandSessionReady(out CarrierSession s, out string reason)
    {
        s = null!;
        if (GameManager.gameState != GameState.SinglePlayer)
        {
            reason = "multiplayer_unavailable";
            return false;
        }
        if (!TryGetLocalSession(out s) || !IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy)
        {
            reason = "session_not_ready";
            return false;
        }
        reason = "";
        return true;
    }
    private bool MapCommandSessionReady(out string reason) => MapCommandSessionReady(out _, out reason);
    internal bool TryResolveCommandSession(uint sessionId, INetworkPlayer? sender, out CarrierSession session, out string reason)
    {
        session = null!;
        if (sessionId == 0 || sessionRegistry == null || sender == null || !sessionRegistry.TryGetById(sessionId, out session) ||
            !SessionCanReconcile(session)) { reason = "session_not_ready"; return false; }
        if (!ReferenceEquals(sender, session.Owner)) { reason = "sender_not_session_owner"; return false; }
        reason = ""; return true;
    }
    internal bool TryResolveSessionDrone(CarrierSession session, uint dronePid, out Aircraft drone, out string reason)
    {
        drone = null!;
        if (sessionRegistry == null || !IsActiveSession(session)) { reason = "session_not_ready"; return false; }
        if (!sessionRegistry.TryGetDrone(session, dronePid, out drone, out reason)) return false;
        if (!session.Manager.IsRegistered(drone)) { reason = "not_registered_drone"; return false; }
        reason = ""; return true;
    }
    internal bool TrySetSessionDroneMode(CarrierSession session, Aircraft drone, DroneModeOverride mode, out string reason) =>
        IsActiveSession(session) ? session.Manager.SetRecordMode(drone, mode, out reason) : SetUnavailable(out reason);
    internal bool TryResolveSnapshotSession(uint requestedSessionId, INetworkPlayer? sender, out CarrierSession session,
                                            out string reason)
    {
        session = null!;
        if (sessionRegistry == null || sender == null) { reason = "session_not_ready"; return false; }
        bool found = requestedSessionId == 0 ? sessionRegistry.TryGetByOwner(sender, out session) :
                                               sessionRegistry.TryGetById(requestedSessionId, out session);
        if (!found || !SessionCanReconcile(session)) { reason = "session_not_ready"; return false; }
        if (!ReferenceEquals(sender, session.Owner)) { reason = "sender_not_session_owner"; return false; }
        reason = ""; return true;
    }
    internal bool TryGetSnapshotOwner(CarrierSession session, out INetworkPlayer owner)
    {
        owner = null!;
        if (!IsActiveSession(session)) return false;
        owner = session.Owner;
        return owner != null;
    }
    internal bool TryBuildModeSnapshot(CarrierSession session, List<WingmanSessionTransport.SnapshotEntry> entries,
                                       out uint homePid, out string reason)
    {
        entries.Clear(); homePid = 0;
        if (!SessionCanReconcile(session)) { reason = "session_not_ready"; return false; }
        homePid = session.Home?.persistentID.Id ?? 0;
        foreach (Aircraft aircraft in session.Roster)
            if (session.Manager.IsRegistered(aircraft) && session.Manager.TryGetMapStatus(aircraft, out int ordinal,
                out DroneModeOverride mode, out _, out _, out _))
                if (session.Manager.TryGetMapGroup(aircraft, out int groupId, out int groupSlot))
                    entries.Add(new WingmanSessionTransport.SnapshotEntry(aircraft.persistentID.Id, ordinal, groupId,
                                                                            groupSlot, (int)mode));
        reason = ""; return true;
    }
    internal void ClientNotifyModeChanged(uint sessionId, uint dronePid, int mode, bool ok, string reason) =>
        Log("state=mode_changed_rpc session=" + sessionId + " pid=" + dronePid + " mode=" + mode + " ok=" + ok + " reason=" + reason);
    internal void ClientNotifySnapshot(uint sessionId, string reason) =>
        Log("state=mode_snapshot session=" + sessionId + " reason=" + reason);
    private static bool SetUnavailable(out string reason) { reason = "session_not_ready"; return false; }

    internal bool MapAssignAirToAir(Aircraft drone, Aircraft target, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignAirToAir(drone, target, out reason);
    internal bool MapAssignCap(Aircraft drone, GlobalPosition anchor, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignCap(drone, anchor, out reason);
    internal bool MapClearCap(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearCap(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetCapAnchor(Aircraft drone, out GlobalPosition anchor, out int revision)
    {
        anchor = default; revision = 0;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetCapAnchor(drone, out anchor, out revision);
    }
    internal bool MapClearAirToAir(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearAirToAir(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetAirToAirTarget(Aircraft drone, out Aircraft target)
    {
        target = null!;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetAirToAirTarget(drone, out target);
    }
    internal bool MapAssignSead(Aircraft drone, Unit target, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignSead(drone, target, out reason);
    internal bool MapClearSead(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearSead(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetSeadTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetSeadTarget(drone, out target);
    }
    internal bool MapAssignAntiShip(Aircraft drone, Ship target, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignAntiShip(drone, target, out reason);
    internal bool MapClearAntiShip(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearAntiShip(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetAntiShipTarget(Aircraft drone, out Ship target)
    {
        target = null!;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetAntiShipTarget(drone, out target);
    }
    internal bool MapAssignCas(Aircraft drone, GlobalPosition center, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignCas(drone, center, out reason);
    internal bool MapClearCas(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearCas(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetCasCenter(Aircraft drone, out GlobalPosition center)
    {
        center = default;
        return MapCommandSessionReady(out CarrierSession s, out _) &&
               s.Manager.TryGetCasCenter(drone, out center, out _, out _);
    }
    internal bool MapTryGetCasTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetCasTarget(drone, out target);
    }
    internal bool MapAssignStrike(Aircraft drone, IReadOnlyList<Unit> targets, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryAssignStrike(drone, targets, out reason);
    internal bool MapClearStrike(Aircraft drone, out string reason)
    {
        if (!MapCommandSessionReady(out CarrierSession s, out reason)) return false;
        if (!s.Manager.ClearStrike(drone, "map_panel")) { reason = "not_registered"; return false; }
        reason = ""; return true;
    }
    internal bool MapTryGetStrikeTarget(Aircraft drone, out Unit target)
    {
        target = null!;
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetStrikeTarget(drone, out target);
    }
    internal bool MapTryGetStrikeTargetSet(Aircraft drone, out PersistentID[] targetIds)
    {
        targetIds = Array.Empty<PersistentID>();
        return MapCommandSessionReady(out CarrierSession s, out _) && s.Manager.TryGetStrikeTargetSet(drone, out targetIds);
    }
    internal bool MapStandDown(Aircraft drone, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.StandDown(drone, out reason);
    internal bool MapCruiseToPoint(Aircraft drone, GlobalPosition point, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.TryCruiseToPoint(drone, point, out reason);
    internal bool MapClearLoiterHere(Aircraft drone, out string reason) =>
        MapCommandSessionReady(out CarrierSession s, out reason) && s.Manager.ClearLoiterHere(drone, out reason);
    internal bool MapTryGetLoiterHere(Aircraft drone, out GlobalPosition point)
    {
        if (MapCommandSessionReady(out CarrierSession s, out _)) return s.Manager.TryGetLoiterHere(drone, out point);
        point = default; return false;
    }
    internal bool MapIsRegistered(Aircraft aircraft)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.IsRegistered(aircraft);
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        return aircraft != null && transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
               transport.ContainsSnapshotPid(aircraft.persistentID.Id);
    }
    internal WingmanMode MapCurrentMode => TryGetLocalSession(out CarrierSession s) ? s.Manager.CurrentMode : WingmanMode.Loiter;
    internal bool MapTryGetStatus(Aircraft drone, out int slotOrdinal, out DroneModeOverride mode,
                                  out bool assigned, out bool defending, out bool attacking)
    {
        if (TryGetLocalSession(out CarrierSession s))
            return s.Manager.TryGetMapStatus(drone, out slotOrdinal, out mode, out assigned, out defending, out attacking);
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (drone != null && transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
            transport.TryGetSnapshotEntry(drone.persistentID.Id, out slotOrdinal, out _, out _, out int rawMode))
        {
            mode = rawMode == (int)DroneModeOverride.Auto ? DroneModeOverride.Auto :
                   rawMode == (int)DroneModeOverride.Follow ? DroneModeOverride.Follow : DroneModeOverride.Loiter;
            assigned = defending = attacking = false;
            return true;
        }
        slotOrdinal = -1; mode = DroneModeOverride.Auto; assigned = defending = attacking = false; return false;
    }
    internal bool MapTryGetCruisePhase(Aircraft drone, out bool cruising)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.TryGetMapCruisePhase(drone, out cruising);
        cruising = false; return false;
    }
    internal bool MapTryGetGroup(Aircraft drone, out int groupId, out int groupSlot)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.TryGetMapGroup(drone, out groupId, out groupSlot);
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (drone != null && transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
            transport.TryGetSnapshotEntry(drone.persistentID.Id, out _, out groupId, out groupSlot, out _))
            return true;
        groupId = groupSlot = -1; return false;
    }
    internal bool MapTryGetFqPresentationLabel(Aircraft drone, out string label)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.TryGetPresentationLabel(drone, out label);
        label = "";
        return false;
    }
    internal bool MapTryGetGroupPage(out uint sessionId, out int groupId, out bool local)
    {
        if (TryGetLocalSession(out CarrierSession s) && IsActiveSession(s))
        {
            sessionId = s.SessionId;
            local = GameManager.gameState == GameState.SinglePlayer;
            groupId = local ? s.MapCurrentGroupId : 0;
            return true;
        }
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0)
        {
            sessionId = transport.SnapshotSessionId; groupId = 0; local = false; return true;
        }
        sessionId = 0; groupId = 0; local = false; return false;
    }
    internal void MapSetGroupPage(uint sessionId, int groupId)
    {
        if (GameManager.gameState == GameState.SinglePlayer && TryGetLocalSession(out CarrierSession s) &&
            IsActiveSession(s) && s.SessionId == sessionId)
            s.MapCurrentGroupId = groupId;
    }
    internal bool MapTryGetManagedFq(Aircraft aircraft, out uint sessionId, out int groupId)
    {
        sessionId = 0; groupId = -1;
        if (aircraft == null || aircraft.disabled || !aircraft.gameObject.activeInHierarchy ||
            !TryGetLocalSession(out CarrierSession s) || !IsActiveSession(s) ||
            !s.Roster.Contains(aircraft) || aircraft == s.Home || !s.Manager.IsRegistered(aircraft) ||
            !s.Manager.TryGetMapGroup(aircraft, out groupId, out _))
            return false;
        sessionId = s.SessionId;
        return true;
    }
    internal bool MapTryGetMission(Aircraft drone, out WingmanMission mission)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.TryGetMission(drone, out mission);
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        if (drone != null && transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
            transport.ContainsSnapshotPid(drone.persistentID.Id)) { mission = WingmanMission.None; return true; }
        mission = WingmanMission.None; return false;
    }
    internal bool MapHasSession
    {
        get
        {
            if (TryGetLocalSession(out CarrierSession s) && IsActiveSession(s)) return true;
            WingmanSessionTransport? transport = WingmanSessionTransport.i;
            return transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0;
        }
    }
    internal bool MapModeOnlyRemoteSession
    {
        get
        {
            if (GameManager.gameState == GameState.SinglePlayer) return false;
            if (TryGetLocalSession(out CarrierSession s) && IsActiveSession(s)) return true;
            WingmanSessionTransport? transport = WingmanSessionTransport.i;
            return transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0;
        }
    }
    internal bool MapModeKnown(Aircraft drone)
    {
        if (TryGetLocalSession(out CarrierSession s)) return s.Manager.IsRegistered(drone);
        WingmanSessionTransport? transport = WingmanSessionTransport.i;
        return drone != null && transport != null && transport.SnapshotReady && transport.SnapshotSessionId != 0 &&
               transport.SnapshotModeKnown(drone.persistentID.Id);
    }
    internal bool MapCombatEnabled => CombatEnabled;
    internal bool MapSelectAllPressed => selectAllKey.Value.IsDown();
    internal void MapLog(string text) => Log(text);
    private bool TryGetLocalSession(out CarrierSession session)
    {
        session = null!;
        return sessionRegistry != null && GameManager.GetLocalPlayer(out Player player) && player.Owner != null &&
               sessionRegistry.TryGetByPlayer(player, out session) && session.Player == player && player.IsLocalPlayer;
    }
    private bool IsActiveSession(CarrierSession session) => sessionRegistry != null &&
                                                            sessionRegistry.TryGetById(session.SessionId, out CarrierSession active) &&
                                                            ReferenceEquals(active, session);
    private bool EnsureSessionRouter() => WingmanSessionTransport.EnsureSpawned(this);

    private bool TryGetActiveLeader(CarrierSession s, out Aircraft leader)
    {
        leader = null!;
        try
        {
            if (!IsActiveSession(s) || !GameManager.GetLocalAircraft(out Aircraft local) ||
                !GameManager.GetLocalPlayer(out Player player) || player != s.Player ||
                player.Aircraft != local || !StaticEligible(local) ||
                !IsCompleteCurrentAuthority(local, player) || local.HasEjected() ||
                local.unitState == Unit.UnitState.Abandoned || local.unitState == Unit.UnitState.Returned ||
                local.NetworkHQ != s.Player.HQ)
                return false;
            leader = local;
            return true;
        }
        catch
        {
            return false;
        }
    }
    internal bool CombatEnabled => combatEnable != null && combatEnable.Value;
    internal bool SessionCanReconcile(CarrierSession session) => IsActiveSession(session) &&
        session.State == CarrierSessionState.Complete && !session.Busy && NetworkManagerNuclearOption.i != null &&
        NetworkManagerNuclearOption.i.Server.Active;
    internal bool SessionHasPlayerAssociation(CarrierSession session, Aircraft aircraft) =>
        !IsActiveSession(session) || HasAnyPlayerAssociation(aircraft, session.Player);
    internal bool SessionCanFallback(CarrierSession session, Aircraft drone)
    {
        try
        {
            return IsActiveSession(session) && !drone.disabled && !drone.HasEjected() &&
                     drone.unitState != Unit.UnitState.Abandoned && drone.unitState != Unit.UnitState.Returned &&
                     !HasAnyPlayerAssociation(drone, session.Player);
        }
        catch { return false; }
    }
    internal bool TryGetAuthoritativeSessionLeader(CarrierSession session, out Aircraft leader)
    {
        leader = null!;
        try
        {
            Aircraft? candidate = session.Player.Aircraft;
            if (!SessionCanReconcile(session) || session.Player.Owner == null || session.Player.HQ == null || candidate == null ||
                !IsCompleteCurrentAuthority(candidate, session.Player) || candidate.disabled || candidate.HasEjected() ||
                candidate.unitState == Unit.UnitState.Abandoned || candidate.unitState == Unit.UnitState.Returned ||
                candidate.NetworkHQ != session.Player.HQ) return false;
            leader = candidate; return true;
        }
        catch { return false; }
    }
    internal bool SessionEligibleForInstall(CarrierSession session, Aircraft drone, out string reason)
    {
        reason = "";
        if (!SessionCanReconcile(session) || !session.Roster.Contains(drone) || drone == session.Home)
        { reason = "session_or_roster"; return false; }
        if (!drone.IsServer || !drone.LocalSim) { reason = "not_authoritative_local_sim"; return false; }
        if (!NativeRecoveryGuard.TryEvaluate(drone, out reason)) return false;
        if (drone.pilots == null || drone.pilots.Length == 0 || drone.pilots[0] == null ||
            drone.pilots[0].pilotType != Pilot.PilotType.Plane) { reason = "pilot_type"; return false; }
        if (drone.autopilot is not AutopilotPlane) { reason = "autopilot_type"; return false; }
        if (drone.pilots[0].currentState is not AIPilotCombatModes) { reason = "native_state"; return false; }
        if (SessionHasPlayerAssociation(session, drone)) { reason = "player_association"; return false; }
        if (!TryGetAuthoritativeSessionLeader(session, out Aircraft leader) || !ManagedLeaderValid(leader, drone, out reason)) return false;
        if (session.Manager.CurrentMode == WingmanMode.Follow && !FollowLeaderValid(leader, drone, out reason)) return false;
        reason = "ok"; return true;
    }
    internal bool SessionRuntimeEligible(CarrierSession session, Aircraft drone, Aircraft leader, out string reason)
    {
        reason = "";
        if (!SessionCanReconcile(session) || !session.Roster.Contains(drone) || drone == leader || !drone.IsServer || !drone.LocalSim)
        { reason = "session_or_roster"; return false; }
        if (!NativeRecoveryGuard.TryEvaluate(drone, out reason)) return false;
        if (SessionHasPlayerAssociation(session, drone)) { reason = "player_association"; return false; }
        if (!TryGetAuthoritativeSessionLeader(session, out Aircraft authoritative) || leader != authoritative)
        { reason = "leader"; return false; }
        return FollowLeaderValid(leader, drone, out reason);
    }
    internal bool TryGetSessionCommandHq(CarrierSession session, out FactionHQ hq, out string reason)
    {
        hq = null!;
        if (!SessionCanReconcile(session) || session.Player.HQ == null) { reason = "session_not_ready"; return false; }
        hq = session.Player.HQ; reason = ""; return true;
    }
    private static bool ManagedLeaderValid(Aircraft leader, Aircraft drone, out string reason)
    {
        reason = "";
        try
        {
            if (!NativeRecoveryGuard.TryEvaluate(leader, out reason) || leader.rb == null || drone.rb == null)
            {
                return false;
            }
            if (!Finite(leader.radarAlt) || !Finite(leader.rb.velocity.x) || !Finite(leader.rb.velocity.y) ||
                !Finite(leader.rb.velocity.z))
            {
                reason = "leader_non_finite";
                return false;
            }
            reason = "ok";
            return true;
        }
        catch (Exception e)
        {
            reason = "leader_" + e.GetType().Name;
            return false;
        }
    }
    private static bool FollowLeaderValid(Aircraft leader, Aircraft drone, out string reason)
    {
        if (!ManagedLeaderValid(leader, drone, out reason))
            return false;
        try
        {
            Vector3 forward = Vector3.ProjectOnPlane(leader.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < .01f)
                forward = Vector3.ProjectOnPlane(leader.rb.velocity, Vector3.up);
            if (forward.sqrMagnitude < .01f)
            {
                reason = "leader_forward";
                return false;
            }
            reason = "ok";
            return true;
        }
        catch (Exception e)
        {
            reason = "leader_" + e.GetType().Name;
            return false;
        }
    }
    private void PreserveNativeAi(Aircraft aircraft) => Log("state=native_ai_preserved aircraft=" + NameOf(aircraft) +
                                                            " old_aircraft_ai=native");
    private bool TryReturnHomeToBase(CarrierSession s, Aircraft home, out string reason)
    {
        reason = "";
        if (s.HomeReturningToBase)
        {
            reason = "rtb_in_progress";
            return false;
        }
        if (!TryGetHomeRtbHandoff(s, home, out Pilot pilot, out PilotBaseState landing, out reason))
            return false;
        if (!s.Cruise.RestoreBeforeTargeting(home, "rtb"))
        {
            reason = "carrier_cruise_restore_failed";
            return false;
        }
        if (!HomeRtbStillEligible(s, home, out reason))
            return false;
        string? handoffException = null;
        try
        {
            pilot.SwitchState(landing);
        }
        catch (Exception e)
        {
            handoffException = e.GetType().Name;
        }
        if (pilot.currentState != landing)
        {
            reason = handoffException == null ? "rtb_handoff_failed" : "rtb_handoff_exception";
            if (handoffException != null)
                s.Context.Log("state=home_rtb_handoff_exception pid=" + PidOf(home) +
                              " native=AIHeloLandingState installed=false exception=" + handoffException);
            return false;
        }
        if (handoffException != null)
            s.Context.Log("state=home_rtb_handoff_exception pid=" + PidOf(home) +
                          " native=AIHeloLandingState installed=true exception=" + handoffException);
        s.HomeReturningToBase = true;
        s.Context.Log("state=home_rtb_handoff pid=" + PidOf(home) + " native=AIHeloLandingState");
        return true;
    }
    private bool TryCancelHomeReturnToBase(CarrierSession s, Aircraft home, out string reason)
    {
        reason = "";
        if (!IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy || s.ReleasePending ||
            GameManager.gameState != GameState.SinglePlayer || home != s.Home)
        {
            reason = "not_returning";
            return false;
        }
        if (!RtbCancellationLogic.CanCancel(s.HomeReturningToBase, home.disabled, home.HasEjected(),
                                            home.unitState == Unit.UnitState.Abandoned,
                                            home.unitState == Unit.UnitState.Returned, home.radarAlt, out reason))
            return false;
        Pilot pilot = home.pilots[0];
        Queue<Aircraft>? queue = pilot.AIHeloLandingState.landingPoint?.GetLandingQueue();
        if (!s.Cruise.TryResumeAfterRtbCancel(home) || !s.Cruise.IsCruisingCarrier(home))
        {
            reason = "carrier_cruise_resume_failed";
            return false;
        }
        if (queue != null)
        {
            Aircraft[] queued = queue.ToArray();
            queue.Clear();
            foreach (Aircraft aircraft in queued)
                if (aircraft != home)
                    queue.Enqueue(aircraft);
        }
        s.HomeReturningToBase = false;
        s.Context.Log("state=home_rtb_cancel_accepted pid=" + PidOf(home));
        return true;
    }
    private bool TryGetHomeRtbHandoff(CarrierSession s, Aircraft home, out Pilot pilot,
                                      out PilotBaseState landing, out string reason)
    {
        pilot = null!;
        landing = null!;
        reason = "";
        if (!IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy ||
            s.ReleasePending || home != s.Home)
        {
            reason = "session_not_ready";
            return false;
        }
        if (!TryGetActiveLeader(s, out Aircraft local))
        {
            reason = "session_or_player_association";
            return false;
        }
        if (local == home)
        {
            reason = "player_association";
            return false;
        }
        if (HasAnyPlayerAssociation(home, s.Player))
        {
            reason = "player_association";
            return false;
        }
        if (GameManager.gameState != GameState.SinglePlayer || NetworkManagerNuclearOption.i == null ||
            !NetworkManagerNuclearOption.i.Server.Active || !home.IsServer || !home.LocalSim)
        {
            reason = "authority";
            return false;
        }
        if (!NativeRecoveryGuard.TryEvaluate(home, out reason))
            return false;
        pilot = home.pilots[0];
        if (pilot.pilotType != Pilot.PilotType.Tiltwing || home.autopilot is not AutopilotTiltwing)
        {
            reason = "native_landing_unavailable";
            return false;
        }
        landing = pilot.AIHeloLandingState;
        if (landing == null)
        {
            reason = "native_landing_unavailable";
            return false;
        }
        return true;
    }
    private bool HomeRtbStillEligible(CarrierSession s, Aircraft home, out string reason)
    {
        reason = "";
        if (!IsActiveSession(s) || s.State != CarrierSessionState.Complete || s.Busy || s.ReleasePending || home != s.Home)
        {
            reason = "session_or_player_association";
            return false;
        }
        if (!TryGetActiveLeader(s, out Aircraft local))
        {
            reason = "session_or_player_association";
            return false;
        }
        if (local == home)
        {
            reason = "player_association";
            return false;
        }
        if (GameManager.gameState != GameState.SinglePlayer || NetworkManagerNuclearOption.i == null ||
            !NetworkManagerNuclearOption.i.Server.Active || !home.IsServer || !home.LocalSim)
        {
            reason = "authority";
            return false;
        }
        if (!NativeRecoveryGuard.TryEvaluate(home, out reason))
            return false;
        return true;
    }
    private bool PreflightServer(Player p, Aircraft c, out AircraftDefinition d, out int livery)
    {
        d = null!;
        livery = -1;
        if (!BlueprinterReady() || payload == null || !payload.Ready || NetworkManagerNuclearOption.i == null ||
            !NetworkManagerNuclearOption.i.Server.Active || p == null || p.Identity == null || p.Owner == null ||
            p.HQ == null || c == null || c.Identity == null || c.rb == null || c.NetworkHQ == null ||
            c.definition == null || c.definition.jsonKey != carrierKey.Value || c.Networkloadout == null ||
            !IsCompleteCurrentAuthority(c, p))
            return Fail("spawn_guard");
        if (!Finite(c.rb.velocity.x) || !Finite(c.rb.velocity.y) || !Finite(c.rb.velocity.z) ||
            !Finite(spawnBack.Value) || !Finite(spawnDown.Value) || !Finite(launchBoost.Value) || Spawner.i == null ||
            Encyclopedia.Lookup == null || !Encyclopedia.Lookup.TryGetValue(droneKey.Value, out UnitDefinition u) ||
            !(u is AircraftDefinition))
            return Fail("definition");
        d = (AircraftDefinition)u;
        if (d.unitPrefab == null || d.unitPrefab.GetComponent<Aircraft>() == null ||
            d.unitPrefab.GetComponent<NetworkIdentity>() == null || d.aircraftParameters == null ||
            d.aircraftParameters.liveries == null || d.aircraftParameters.liveries.Count == 0 ||
            !Finite(d.aircraftParameters.DefaultFuelLevel) || d.aircraftParameters.DefaultFuelLevel < 0f ||
            d.aircraftParameters.DefaultFuelLevel > 1f)
            return Fail("parameters");
        livery = d.aircraftParameters.GetFirstLiveryForFaction(c.NetworkHQ.faction);
        Log("state=preflight carrier=" + carrierKey.Value + " drone=" + droneKey.Value + " fuel=" +
            d.aircraftParameters.DefaultFuelLevel + " loadouts=" + (d.aircraftParameters.loadouts?.Count ?? 0));
        return livery >= 0 && livery < d.aircraftParameters.liveries.Count || Fail("livery");
    }
    private static bool Registered(Aircraft a)
    {
        try
        {
            return a != null && !a.disabled && a.gameObject.activeInHierarchy && a.Identity != null &&
                   a.Identity.IsSpawned && a.Identity.NetId != 0 && !a.persistentID.Equals(default(PersistentID)) &&
                   UnitRegistry.TryGetPersistentUnit(a.persistentID, out PersistentUnit u) && u != null && u.unit == a;
        }
        catch
        {
            return false;
        }
    }
    private Vector3 SpawnJoinPosition(Aircraft carrier, int index)
    {
        float back, lateral, down;
        if (index == 0)
        {
            back = 30f;
            lateral = -80f;
            down = 10f;
        }
        else if (index == 1)
        {
            back = 30f;
            lateral = 80f;
            down = 10f;
        }
        else if (index == 2)
        {
            back = 90f;
            lateral = -200f;
            down = 40f;
        }
        else if (index == 3)
        {
            back = 90f;
            lateral = 200f;
            down = 40f;
        }
        else
        {
            back = 150f + 60f * (index - 4);
            lateral = 0f;
            down = 50f;
        }
        Vector3 right = carrier.transform.right;
        if (!Finite(back) || !Finite(lateral) || !Finite(down) || !Finite(right.x) || !Finite(right.y) ||
            !Finite(right.z))
            return carrier.transform.position - carrier.transform.forward * spawnBack.Value -
                   carrier.transform.up * spawnDown.Value;
        return carrier.transform.position - carrier.transform.forward * (spawnBack.Value + back) + right * lateral -
               carrier.transform.up * (spawnDown.Value + down);
    }
    private static void AddCollisionPairs(SessionCollisionLease lease, Aircraft a, Aircraft b, Collider? excluded = null)
    {
        try
        {
            if (a == null || b == null || a == b)
                return;
            foreach (Collider x in a.GetComponentsInChildren<Collider>())
                foreach (Collider y in b.GetComponentsInChildren<Collider>())
                {
                    if (x == null || y == null || x == excluded || y == excluded)
                        continue;
                    Physics.IgnoreCollision(x, y, true);
                    lease.Pairs.Add(new SessionCollisionPair(x, y));
                }
        }
        catch
        {
        }
    }
    private IEnumerator IgnoreCollisions(CarrierSession s, SessionCollisionLease lease)
    {
        try
        {
            yield return new WaitForSeconds(5f);
        }
        finally
        {
            RestoreLease(s, lease);
        }
    }
    private static void RestoreLease(CarrierSession s, SessionCollisionLease lease)
    {
        foreach (SessionCollisionPair p in lease.Pairs)
            if (p.A != null && p.B != null)
                Physics.IgnoreCollision(p.A, p.B, false);
        lease.Pairs.Clear();
        s.CollisionLeases.Remove(lease);
    }
    private void RestoreLeases(CarrierSession s)
    {
        foreach (SessionCollisionLease lease in new List<SessionCollisionLease>(s.CollisionLeases))
        {
            if (lease.Coroutine != null)
                StopCoroutine(lease.Coroutine);
            RestoreLease(s, lease);
        }
    }
    private void CleanupSession(CarrierSession s, string reason)
    {
        if (IsActiveSession(s) && WingmanSessionTransport.i != null && s.Owner != null)
            try
            {
                WingmanSessionTransport.i.PublishSessionClosed(s, "session_closed");
            }
            catch (Exception e)
            {
                s.Context.Log("state=session_close_publish_failed error=" + e.GetType().Name);
            }

        if (pendingEmergency != null && ReferenceEquals(pendingEmergency.session, s))
            pendingEmergency = null;
        ++s.ReleaseGeneration;
        ++s.SwitchGeneration;
        if (s.ReleaseCoroutine != null)
            StopCoroutine(s.ReleaseCoroutine);
        if (s.SwitchCoroutine != null)
            StopCoroutine(s.SwitchCoroutine);
        s.ReleaseCoroutine = null;
        s.SwitchCoroutine = null;
        s.ReleasePending = false;
        s.Busy = false;
        s.HomeReturningToBase = false;
        pendingRemoteFqLoadoutPlans.Remove(s.Owner!);

        CarrierSessionState priorState = s.State;
        Aircraft[] roster = s.Roster.ToArray();
        var protectedPending = new HashSet<Aircraft>();
        foreach (Aircraft drone in roster)
            if (drone != null && DroneCarrierPortLogic.ShouldProtectPendingPlayerRecovery(
                DroneCarrierPortWeapon.IsPendingPlayerHandoffFor(drone))) protectedPending.Add(drone);
        ClearDroneSubscriptions(s, protectedPending);
        RestoreLeases(s);

        bool serverActive = NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
        foreach (Aircraft drone in roster)
        {
            if (drone == null || drone == s.Home)
                continue;
            if (protectedPending.Contains(drone))
            {
                DroneCarrierPortWeapon.CancelPendingPlayerHandoffFor(drone);
                continue;
            }
            if (sessionRegistry != null && !sessionRegistry.TryDetachDrone(s, drone, reason, out string detachReason))
                s.Context.Log("event=session_detach_failed drone_pid=" + PidOf(drone) + " reason=" + detachReason);
            bool safelyDespawn = (priorState == CarrierSessionState.PreSwitch || priorState == CarrierSessionState.Switching) &&
                                 CanSafelyDespawn(drone, s.Player);
            bool failClosedDespawn = MultiplayerCleanupLogic.ShouldFailClosedDespawn(
                GameManager.gameState == GameState.SinglePlayer, serverActive, HasAnyPlayerAssociation(drone, s.Player));
            if (safelyDespawn || failClosedDespawn)
                Despawn(drone);
        }

        s.Cruise.Cleanup(reason);
        s.Manager.Cleanup();
        s.Reporter.Cleanup();

        Aircraft? home = s.Home;
        if (home != null)
            sessionRegistry?.TryClearHome(s, home);
        if (sessionRegistry != null && !sessionRegistry.TryRemove(s, out string removeReason))
        {
            bool terminal = reason == "network_stopped" || reason == "network_changed" || reason == "scene_loaded" || reason == "destroy";
            if (terminal && sessionRegistry.ForceRemoveForTerminalTeardown(s))
                s.Context.Log("event=session_terminal_force_removed reason=" + reason);
            else s.Context.Log("event=session_cleanup_incomplete reason=" + removeReason);
        }

        if (s.Player != null && s.Player.IsLocalPlayer)
            mapPanel?.ResetScene();
        s.Context.Log("event=session_cleanup reason=" + reason);
    }

    private void CleanupAllSessions(string reason)
    {
        pendingRemoteFqLoadoutPlans.Clear();
        CarrierSession[] snapshot = sessionRegistry?.SnapshotSessions() ?? Array.Empty<CarrierSession>();
        foreach (CarrierSession s in snapshot)
            CleanupSession(s, reason);
        mapPanel?.ResetScene();
        Log("event=server_cleanup_all reason=" + reason);
        WingmanSessionTransport.CleanupRouter();
    }
    private void Fatal(CarrierSession s, Aircraft oldAircraft, Aircraft target)
    {
        OwnershipDiagnostics.Snapshot(Logger, "poisoned", "fatal", s.Player, oldAircraft, target);
        s.State = CarrierSessionState.Poisoned;
        s.Context.Log("state=fatal_switch_incomplete ownership=partial action=restart_mission");
        CleanupSession(s, "fatal_switch");
    }
    private void PruneRoster(CarrierSession s)
    {
        if (sessionRegistry == null)
            return;
        foreach (Aircraft aircraft in new List<Aircraft>(s.Roster))
        {
            if (aircraft == null || (!aircraft.disabled && !aircraft.HasEjected() &&
                                    aircraft.unitState != Unit.UnitState.Abandoned &&
                                    aircraft.unitState != Unit.UnitState.Returned))
                continue;
            UnsubscribeDrone(s, aircraft);
            if (!sessionRegistry.TryDetachDrone(s, aircraft, "prune", out string detachReason))
                s.Context.Log("event=session_detach_failed drone_pid=" + PidOf(aircraft) + " reason=" + detachReason);
        }
    }
    private void RosterSnapshot(CarrierSession s, string reason)
    {
        try
        {
            List<string> members = new List<string>();
            foreach (Aircraft aircraft in s.Roster)
                members.Add(RosterEntry(s, aircraft));
            s.Context.Log("state=roster_snapshot reason=" + reason + " home_pid=" + PidOf(s.Home) +
                          " members=" + string.Join("|", members));
        }
        catch (Exception e)
        {
            s.Context.Log("state=roster_snapshot reason=" + reason + " error=" + e.GetType().Name);
        }
    }
    private static string RosterEntry(CarrierSession s, Aircraft aircraft)
    {
        try
        {
            string pilotState = aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null &&
                                        aircraft.pilots[0].currentState != null
                                    ? aircraft.pilots[0].currentState.GetType().Name
                                    : "null";
            return "key=" + NameOf(aircraft) + ",pid=" + PidOf(aircraft) +
                   ",role=" + (aircraft == s.Home ? "home" : "drone") + ",registered=" + Registered(aircraft) +
                   ",disabled=" + aircraft.disabled + ",ejected=" + aircraft.HasEjected() +
                   ",unitState=" + aircraft.unitState + ",pilotState=" + pilotState;
        }
        catch (Exception e)
        {
            return "key=unavailable,error=" + e.GetType().Name;
        }
    }
    private void Despawn(Aircraft a)
    {
        try
        {
            if (a.Identity != null && a.Identity.IsSpawned)
                a.ServerObjectManager.Destroy(a.Identity, true);
            else
                Destroy(a.gameObject);
        }
        catch (Exception e)
        {
            Log("state=failed phase=despawn error=" + e.GetType().Name);
        }
    }
    private static string NameOf(Aircraft? a) => a == null || a.definition == null ? "unknown" : a.definition.jsonKey;
    private void SendLwChat(CarrierSession s, string message)
    {
        if (!enableWingmanStatusMessages.Value)
            return;
        try
        {
            if (s.Player == null || s.Player.Owner == null ||
                NetworkSceneSingleton<ChatManager>.i == null)
            {
                s.Context.Log("state=lw_chat_unavailable");
                return;
            }
            NetworkSceneSingleton<ChatManager>.i.RpcTargetServerMessage(s.Player.Owner, message, false);
        }
        catch (Exception e)
        {
            s.Context.Log("state=lw_chat_failed error=" + e.GetType().Name);
        }
    }
    private void SendLwChat(string message)
    {
        if (TryGetLocalSession(out CarrierSession s))
            SendLwChat(s, message);
    }
    private static string PidOf(Aircraft? a)
    {
        try
        {
            return a == null ? "none" : a.persistentID.ToString();
        }
        catch
        {
            return "unavailable";
        }
    }
    private static void Reject(CarrierSession s, string reason, string direction, Aircraft? from,
                               Aircraft? to) => s.Context.Log("state=switch_cycle_rejected reason=" + reason +
                                                              " direction=" + direction + " from=" + NameOf(from) +
                                                              " to=" + NameOf(to));
    private bool Fail(string reason)
    {
        Log("state=failed phase=preflight reason=" + reason);
        return false;
    }
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private bool BlueprinterReady() =>
        Chainloader.PluginInfos.TryGetValue("com.nikkorap.blueprinter", out PluginInfo i) && i.Instance != null &&
        i.Instance.GetType()
            .GetProperty("PatchingComplete", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(i.Instance, null) is bool b
        && b;
    private void Log(string text) => Logger.LogInfo("[LoyalWingman] " + text);
    private void InstallSessionTransportRegistration()
    {
        try
        {
            var target = AccessTools.Method(typeof(NetworkManagerNuclearOption),
                                            nameof(NetworkManagerNuclearOption.RegisterPrefabs));
            var postfix = AccessTools.Method(typeof(Plugin), nameof(RegisterSessionTransportPrefab));
            if (target == null || postfix == null)
            {
                Log("state=session_transport_unavailable reason=register_prefabs_target");
                return;
            }
            sessionTransportHarmony = new Harmony(Guid + ".session_transport");
            sessionTransportHarmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception e)
        {
            sessionTransportHarmony?.UnpatchSelf();
            sessionTransportHarmony = null;
            Log("state=session_transport_unavailable reason=" + e.GetType().Name);
        }
    }
    private static void RegisterSessionTransportPrefab(NetworkManagerNuclearOption __instance) =>
        WingmanSessionTransport.RegisterForManager(__instance);
    private void OnAnimatedPortMilestone(DroneCarrierPortWeapon port, long token, string milestone)
    {
        if (!ReferenceEquals(activeInstance, this) || port == null || !port.HasAnimatedProvider) return;
        if (milestone == "FullyExtended" && port.AcceptAnimatedMilestone(token, AnimatedPortMotion.ExtendingRelease, milestone, "FullyExtended"))
        {
            WeaponStation? station = null; Unit? owner = null; Unit? target = null; GlobalPosition aimpoint = default;
            bool accepted = port.Carrier != null && port.PortHardpoint != null && port.Station != null &&
                            port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) &&
                            PortLaunchAuthority(port, port.Carrier, port.Station) &&
                            port.CompleteLaunch(true, out station, out owner, out target, out aimpoint) && station != null && owner != null &&
                            port.ConsumeAnimatedNativeLaunch(token);
            if (!accepted)
            {
                TryRequestAnimatedRetract(port, token, AnimatedPortMotion.ExtendingRelease, AnimatedPortMotion.RetractingRelease, "release_pre_native");
                port.Cancel();
                return;
            }
            try { station!.LaunchMount(owner!, target, aimpoint); }
            catch (Exception exception)
            {
                bool releaseUnconsumed = port.HasDescriptor;
                if (releaseUnconsumed) port.Cancel();
                Log("state=animated_port_native_launch_failed error=" + exception.GetType().Name +
                    " release_unconsumed=" + releaseUnconsumed);
            }
            finally
            { TryRequestAnimatedRetract(port, token, AnimatedPortMotion.ExtendingRelease, AnimatedPortMotion.RetractingRelease, "release_native"); }
            return;
        }
        if (milestone == "FullyExtended" && port.AcceptAnimatedMilestone(token, AnimatedPortMotion.ExtendingCapture, milestone, "FullyExtended"))
        {
            Aircraft? candidate = port.ReservedCandidate;
            if (candidate == null || !port.EnterAnimatedCaptureWait(token)) return;
            AnimatedCaptureRecoveryDecision decision = DroneCarrierPortLogic.ResolveAnimatedCaptureRecovery(port.AnimatedCaptureSquaredDistance, port.AnimatedCaptureElapsed, true);
            if (decision == AnimatedCaptureRecoveryDecision.AbortRange || decision == AnimatedCaptureRecoveryDecision.AbortTimeout)
            { AbortAnimatedCaptureInternal(port, candidate, token, AnimatedPortMotion.WaitingCapture, decision == AnimatedCaptureRecoveryDecision.AbortRange ? "range" : "timeout"); return; }
            if (decision == AnimatedCaptureRecoveryDecision.BeginLock) TryBeginAnimatedCaptureLockInternal(port, candidate, token);
            else Log("state=port_fully_extended_waiting_capture candidate_pid=" + PidOf(candidate));
            return;
        }
        if (milestone == "CaptureLockComplete" && port.AcceptAnimatedMilestone(token, AnimatedPortMotion.LockingCapture, milestone, "CaptureLockComplete"))
        {
            if (!port.ConsumeAnimatedCaptureCompletion(token)) return;
            Aircraft? candidate = port.ReservedCandidate;
            if (candidate == null || candidate.rb == null || port.Station == null || port.Carrier == null || port.PortHardpoint == null ||
                !port.RecoveryEnabled || !NativeRecoveryGuard.TryEvaluate(candidate, out _) ||
                !port.IsExactAnimatedRuntimeIdentity(port.Carrier, port.PortHardpoint, port.Station) ||
                !port.HasAnimatedFinalOverlap(candidate.rb))
            { FailAnimatedCapture(port, candidate, token, AnimatedPortMotion.LockingCapture, "lock_complete_guard"); return; }
            if (port.IsPlayerReserved(candidate))
            {
                if (!TryGetLocalSession(out CarrierSession session) || !ReferenceEquals(session.Player.Aircraft, candidate) ||
                    !TryEvaluateDroneCarrierPortPlayerRecovery(port, candidate, true, out CarrierSession current, out _) || !ReferenceEquals(current, session))
                { FailAnimatedCapture(port, candidate, token, AnimatedPortMotion.LockingCapture, "lock_complete_player"); return; }
                session.SwitchCoroutine = StartCoroutine(CompleteDroneCarrierPortPlayerRecovery(port, candidate, session, session.SwitchGeneration, token));
            }
            else
            {
                if (!TryEvaluateDroneCarrierPortCapture(port, candidate, out _, true))
                { FailAnimatedCapture(port, candidate, token, AnimatedPortMotion.LockingCapture, "lock_complete_ai"); return; }
                StartCoroutine(CompleteDroneCarrierPortCapture(port, candidate, null, token));
            }
            return;
        }
        if (milestone == "FullyStowed") port.CloseAnimatedFullyStowed(token, milestone);
    }
    private void InstallCradleLaunchFilter()
    {
        try
        {
            MethodInfo? launchMount = AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.LaunchMount));
            MethodInfo? launchPrefix = AccessTools.Method(typeof(Plugin), nameof(FilterCradleLaunchMount));
            if (launchMount == null || launchPrefix == null)
                throw new MissingMethodException();
            cradleStationHarmony = new Harmony(Guid + ".cradle_launch_filter");
            cradleStationHarmony.Patch(launchMount, prefix: new HarmonyMethod(launchPrefix));
        }
        catch (Exception e) { Log("state=cradle_launch_filter_patch_failed reason=" + e.GetType().Name); }
    }
    private static bool FilterCradleLaunchMount(WeaponStation __instance, Unit owner, Unit target, GlobalPosition aimpoint,
                                                 int ___weaponIndex)
    {
        if (___weaponIndex < 0 || ___weaponIndex >= __instance.Weapons.Count)
            return true;
        Weapon current = __instance.Weapons[___weaponIndex];
        if (current is DroneCarrierPortWeapon port)
        {
            if (port.IsProviderOwnedButInert)
            {
                if (activeInstance != null && port.ConsumeInertReleaseDenialLog())
                    activeInstance.Log("state=drone_port_release_denied reason=provider_inert");
                return false;
            }
            if (activeInstance != null && port.ConsumeNativeBypass(PortLaunchAuthority(port, owner as Aircraft, __instance))) return true;
            if (activeInstance != null && port.BeginLaunch(__instance, owner, target, aimpoint, PortLaunchAuthority(port, owner as Aircraft, __instance)))
            {
                if (DroneCarrierPortLogic.IsPureLegacyReleaseEligible(port.HasAnimatedProvider, port.IsProviderOwnedButInert))
                    activeInstance.StartCoroutine(activeInstance.CompleteDroneCarrierPortLaunch(port));
                else
                {
                    long token = port.BeginAnimatedOperation(AnimatedPortMotion.ExtendingRelease);
                    if (token == 0 || !port.TryAnimatedExtend(token)) { port.ResetAnimatedOperation(); port.Cancel(); }
                }
            }
            return false;
        }
        if (current is not LoyalWingmanCradleWeapon) return true;
        if (owner.HasAuthority && activeInstance != null)
            try
            {
                activeInstance.PublishDirtyFqLoadoutPlan(
                    NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active);
            }
            catch { }
        return LoyalWingmanCradleWeapon.AdmitLocalLaunch(__instance, owner);
    }
    private void OnDestroy()
    {
        recoveryGuidance?.Dispose();
        recoveryGuidance = null;
        DroneCarrierPortWeapon.AnimatedMilestone -= OnAnimatedPortMilestone;
        DroneCarrierPortWeapon.ClearAll();
        NativeWeaponReleaseBridge.ClearReleaseCallback(this);
        if (ReferenceEquals(activeInstance, this)) activeInstance = null;
        SceneManager.sceneLoaded -= OnScene;
        CleanupAllSessions("destroy");
        sessionTransportHarmony?.UnpatchSelf();
        sessionTransportHarmony = null;
        cradleStationHarmony?.UnpatchSelf();
        cradleStationHarmony = null;
        WingmanSessionTransport.UnregisterAllManagers();
        fqCradleLoadoutUi?.Dispose();
        fqCradleLoadoutUi = null;
        ClearActiveFqLoadoutPlan("destroy");
        mapPanel?.Dispose();
        mapPanel = null;
    }
}

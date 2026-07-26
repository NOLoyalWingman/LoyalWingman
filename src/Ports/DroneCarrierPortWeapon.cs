using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LoyalWingman;

internal sealed class DroneCarrierPortWeapon : Weapon
{
    internal const float RecoveryTriggerRadius = 5f;
    private static readonly HashSet<DroneCarrierPortWeapon> livePorts = new HashSet<DroneCarrierPortWeapon>();
    private static readonly WaitForFixedUpdate fixedUpdateWait = new WaitForFixedUpdate();
    private static readonly HashSet<string> kestrelDockedVisualExcludedRendererPaths = new HashSet<string>(System.StringComparer.Ordinal)
    {
        "fuselage_F/gearhinge_F/gear_F_sprung",
        "fuselage_F/gearhinge_F/gear_F_sprung/gear_F_unsprung",
        "fuselage_F/gearhinge_F/gear_F_sprung/gear_F_unsprung/wheel_F",
        "fuselage_F/gearhinge_F/gear_F_sprung/gearLight_F",
        "fuselage_R/gearhingePivot_R/gearhinge_R/gear_R_sprung",
        "fuselage_R/gearhingePivot_R/gearhinge_R/gear_R_sprung/gear_R_unsprung",
        "fuselage_R/gearhingePivot_R/gearhinge_R/gear_R_sprung/gear_R_unsprung/axle_R/wheel_R",
        "fuselage_R/gearhingePivot_L/gearhinge_L/gear_L_sprung",
        "fuselage_R/gearhingePivot_L/gearhinge_L/gear_L_sprung/gear_L_unsprung",
        "fuselage_R/gearhingePivot_L/gearhinge_L/gear_L_sprung/gear_L_unsprung/axle_L/wheel_L"
    };
    internal sealed class Descriptor
    {
        internal readonly AircraftDefinition definition;
        internal readonly NuclearOption.SavedMission.Loadout loadout;
        internal readonly LiveryKey livery;
        internal readonly float includedBaselineValue, paidSurcharge;
        internal Descriptor(AircraftDefinition definition, NuclearOption.SavedMission.Loadout loadout, LiveryKey livery,
                            float includedBaselineValue, float paidSurcharge)
        { this.definition = definition; this.loadout = loadout; this.livery = livery; this.includedBaselineValue = includedBaselineValue; this.paidSurcharge = paidSurcharge; }
    }
    internal readonly struct MountedDroneLoadoutSnapshot
    {
        internal readonly Aircraft? carrier; internal readonly Hardpoint? hardpoint; internal readonly WeaponMount? mount;
        internal readonly WeaponStation? station; internal readonly Descriptor descriptor; internal readonly long revision;
        internal readonly NuclearOption.SavedMission.Loadout loadout;
        internal MountedDroneLoadoutSnapshot(Aircraft? carrier, Hardpoint? hardpoint, WeaponMount? mount, WeaponStation? station,
            Descriptor descriptor, long revision, NuclearOption.SavedMission.Loadout loadout)
        { this.carrier = carrier; this.hardpoint = hardpoint; this.mount = mount; this.station = station; this.descriptor = descriptor; this.revision = revision; this.loadout = loadout; }
    }

    private Aircraft? carrier;
    private Hardpoint? portHardpoint;
    private WeaponMount? attachedMount;
    private AnimatedDronePortBinding? animatedBinding;
    private Coroutine? bindCoroutine, animatedCaptureMonitor;
    private Aircraft? animatedCaptureCandidate;
    private float animatedCaptureStartedAt;
    private bool recoveryEnabled;
    private Aircraft? animatedProbeCandidate;
    private bool animatedProbeEnter, animatedProbeOverlap, animatedProbeExit;
    private long animatedOperationCounter, animatedOperationToken;
    private AnimatedPortMotion animatedMotion;
    private bool nativeLaunchIssued, captureCompletionConsumed;
    private SphereCollider? trigger;
    private GameObject? triggerObject;
    private GameObject? dockedVisualRoot;
    private AircraftDefinition? dockedVisualSource;
    private AircraftDefinition? failedDockedVisualSource;
    private Descriptor? descriptor, provisional;
    private Aircraft? reservedCandidate;
    private readonly Dictionary<Aircraft, HashSet<Collider>> triggerOccupancy = new Dictionary<Aircraft, HashSet<Collider>>();
    private Coroutine? releaseExclusionCondition;
    private Aircraft? releaseExcluded;
    private bool releaseBinding;
    private bool unresolvedEntryLogged;
    private bool providerOwnedButInert, inertReleaseDenialLogged;
    private Unit? savedOwner, savedTarget;
    private GlobalPosition savedAimpoint;
    private DroneCarrierPortState state;
    private long loadoutRevision;
    private bool loadoutApplyInProgress, loadoutRevisionExhaustionLogged;

    internal static event Action<DroneCarrierPortWeapon, long, string>? AnimatedMilestone;

    internal Aircraft? Carrier => carrier;
    internal Hardpoint? PortHardpoint => portHardpoint;
    internal WeaponMount? AttachedMount => attachedMount;
    internal DroneCarrierPortState State => state;
    internal bool HasDescriptor => descriptor != null;
    internal Aircraft? ReservedCandidate => reservedCandidate;
    internal WeaponStation? Station => weaponStation;
    internal Collider? RecoveryTrigger => trigger;
    internal float RecoveryRadius => trigger == null ? RecoveryTriggerRadius : trigger.radius;
    internal float LiveTriggerRadius => trigger == null ? RecoveryTriggerRadius : trigger.radius;
    internal bool HasAnimatedProvider => animatedBinding != null && animatedBinding.IsValid;
    internal bool IsProviderOwnedButInert => providerOwnedButInert;
    internal Transform? AnimatedLaunchPose => HasAnimatedProvider ? animatedBinding!.LaunchPose : null;
    internal Transform? AnimatedRecoveryPose => HasAnimatedProvider ? animatedBinding!.RecoveryPose : null;
    internal bool RecoveryEnabled => recoveryEnabled;
    internal long AnimatedOperationToken => animatedOperationToken;
    internal AnimatedPortMotion AnimatedMotion => animatedMotion;
    internal long LoadoutRevision => loadoutRevision;
    internal bool LoadoutApplyInProgress => loadoutApplyInProgress;
    internal bool IsInTrigger(Aircraft candidate) => triggerOccupancy.TryGetValue(candidate, out HashSet<Collider>? colliders) && colliders.Count > 0;
    internal bool IsReleaseCaptureSuppressed(Aircraft candidate)
    {
        if (!releaseBinding && releaseExcluded == null && releaseExclusionCondition != null)
            ClearReleaseExclusion();
        return DroneCarrierPortLogic.ShouldSuppressReleaseCapture(releaseBinding, ReferenceEquals(releaseExcluded, candidate));
    }
    internal bool IsCaptureProjectionUsable => state == DroneCarrierPortState.Empty && descriptor == null && provisional == null &&
                                               weaponStation != null && weaponStation.Weapons.Contains(this);
    internal bool IsPlayerReserved(Aircraft candidate) => state == DroneCarrierPortState.PlayerReserved && reservedCandidate == candidate;
    internal bool IsPlayerCallbackProjectionUsable(Aircraft candidate) => state == DroneCarrierPortState.PlayerReserved &&
        reservedCandidate == candidate && descriptor == null && provisional == null && weaponStation != null && weaponStation.Weapons.Contains(this);
    internal bool IsPendingPlayerHandoff(Aircraft candidate) => state == DroneCarrierPortState.PlayerHandoff && reservedCandidate == candidate;
    internal static bool TryGetNearestRecoveryGuidancePort(Aircraft candidate, Aircraft home,
                                                            out DroneCarrierPortWeapon port)
    {
        port = null!;
        DroneCarrierPortWeapon? reservedPort = null;
        float nearestSquaredDistance = float.PositiveInfinity;
        int nearestInstanceId = int.MaxValue, reservedInstanceId = int.MaxValue;
        foreach (DroneCarrierPortWeapon current in livePorts)
        {
            if (current == null) continue;
            int instanceId = current.GetInstanceID();
            if (ReferenceEquals(current.carrier, home) && current.IsPlayerReserved(candidate))
            {
                if (instanceId < reservedInstanceId) { reservedPort = current; reservedInstanceId = instanceId; }
                continue;
            }
            if (!TryGetRecoveryGuidanceSquaredDistance(current, candidate, home, false, out float squaredDistance) ||
                !DroneCarrierPortLogic.IsWithinAnimatedCapturePreparationRadiusSquared(squaredDistance)) continue;
            if (squaredDistance > nearestSquaredDistance ||
                (squaredDistance == nearestSquaredDistance && instanceId >= nearestInstanceId)) continue;
            port = current;
            nearestSquaredDistance = squaredDistance;
            nearestInstanceId = instanceId;
        }
        if (reservedPort != null)
        {
            port = null!;
            if (!TryGetRecoveryGuidanceSquaredDistance(reservedPort, candidate, home, true,
                    out float reservedSquaredDistance) ||
                !DroneCarrierPortLogic.IsWithinAnimatedCapturePreparationRadiusSquared(reservedSquaredDistance)) return false;
            port = reservedPort;
        }
        return port != null;
    }
    private static bool TryGetRecoveryGuidanceSquaredDistance(DroneCarrierPortWeapon current, Aircraft candidate,
                                                               Aircraft home, bool reserved, out float squaredDistance)
    {
        squaredDistance = float.PositiveInfinity;
        if (!current.isActiveAndEnabled || !ReferenceEquals(current.carrier, home) || !current.HasAnimatedProvider ||
            !current.recoveryEnabled || current.descriptor != null || current.provisional != null ||
            current.weaponStation == null || !current.weaponStation.Weapons.Contains(current) ||
            (reserved ? !current.IsPlayerReserved(candidate) : !current.IsCaptureProjectionUsable) ||
            current.IsReleaseCaptureSuppressed(candidate)) return false;
        Transform? pose = current.AnimatedRecoveryPose;
        if (pose == null || !pose.gameObject.activeInHierarchy) return false;
        squaredDistance = (candidate.transform.position - pose.position).sqrMagnitude;
        return !float.IsNaN(squaredDistance) && !float.IsInfinity(squaredDistance);
    }
    private bool IsPendingPlayerRecovery(Aircraft candidate) => (state == DroneCarrierPortState.PlayerReserved ||
        state == DroneCarrierPortState.PlayerHandoff) && reservedCandidate == candidate;
    internal static bool IsPendingPlayerHandoffFor(Aircraft candidate)
    {
        foreach (DroneCarrierPortWeapon port in livePorts)
            if (port != null && port.IsPendingPlayerRecovery(candidate)) return true;
        return false;
    }
    internal static void CancelPendingPlayerHandoffFor(Aircraft candidate)
    {
        foreach (DroneCarrierPortWeapon port in livePorts)
            if (port != null && port.IsPendingPlayerRecovery(candidate)) port.CancelPlayerHandoff();
    }

    public override void AttachToHardpoint(Aircraft aircraft, Hardpoint hardpoint, WeaponMount weaponMount)
    {
        ClearReleaseExclusion();
        ResetAnimatedOperation();
        if (reservedCandidate != null && IsPendingPlayerRecovery(reservedCandidate)) Plugin.CancelPendingAnimatedPortRecovery(this, reservedCandidate);
        provisional = null; reservedCandidate = null; savedOwner = savedTarget = null;
        if (descriptor != null) { AdvanceLoadoutRevision("rebind_clear"); descriptor = null; }
        StopDeferredBind();
        DisposeAnimatedBinding();
        DestroyDockedVisual();
        DestroyRuntimeTrigger();
        unresolvedEntryLogged = false;
        providerOwnedButInert = inertReleaseDenialLogged = false;
        base.AttachToHardpoint(aircraft, hardpoint, weaponMount);
        livePorts.Add(this);
        carrier = aircraft; portHardpoint = hardpoint; attachedMount = weaponMount; info = weaponMount.info;
        SphereCollider? templateTrigger = gameObject.GetComponent<SphereCollider>();
        if (templateTrigger != null) UnityEngine.Object.Destroy(templateTrigger);
        ResetAnimatedOperation();
        ResetAnimatedProbe();
        DisableTemplateVisuals();
        if (TryInitialDescriptor(aircraft, out Descriptor initial)) { descriptor = initial; AdvanceLoadoutRevision("initial"); }
        state = descriptor == null ? DroneCarrierPortState.Empty : DroneCarrierPortState.Loaded;
        SetVisual(); Rearm();
        if (ReferenceEquals(weaponMount, DroneCarrierPortRegistry.GenericMount))
            bindCoroutine = StartCoroutine(BindNextFrame(aircraft, hardpoint, weaponMount));
    }

    private void FixedUpdate()
    {
        if (!HasAnimatedProvider) return;
        Transform recoveryPose = animatedBinding!.RecoveryPose;
        if (triggerObject != null)
        {
            triggerObject.transform.SetPositionAndRotation(recoveryPose.position, recoveryPose.rotation);
            triggerObject.transform.localScale = Vector3.one;
        }
    }

    private IEnumerator BindNextFrame(Aircraft expectedCarrier, Hardpoint expectedHardpoint, WeaponMount expectedMount)
    {
        yield return null;
        bindCoroutine = null;
        WeaponStation? station = weaponStation;
        if (!ReferenceEquals(carrier, expectedCarrier) || !ReferenceEquals(portHardpoint, expectedHardpoint) ||
            !ReferenceEquals(attachedMount, expectedMount) || station == null || !station.Weapons.Contains(this) ||
            !ReferenceEquals(expectedMount, DroneCarrierPortRegistry.GenericMount)) yield break;

        if (AnimatedDronePortProvider.TryBind(expectedCarrier, expectedHardpoint, this, station,
                (token, milestone) => AnimatedMilestone?.Invoke(this, token, milestone), out AnimatedDronePortBinding? binding,
                out string reason))
        {
            animatedBinding = binding;
            ConfigureAnimatedTrigger();
            SetVisual();
        }
        else if (reason == "no_provider") ConfigureLegacyTrigger(expectedHardpoint);
        else
        {
            providerOwnedButInert = true;
            inertReleaseDenialLogged = false;
            Debug.LogWarning("[LoyalWingman] state=animated_port_inert reason=" + reason);
        }
    }

    private void ConfigureLegacyTrigger(Hardpoint hardpoint)
    {
        CreateTrigger("LW_DroneCarrierPortTrigger", hardpoint.transform, false);
        recoveryEnabled = true;
        trigger!.enabled = true;
    }

    private void ConfigureAnimatedTrigger()
    {
        CreateTrigger("LW_AnimatedDronePortRecoveryProbe", null, true);
        InitializeAnimatedProbe();
    }

    private void CreateTrigger(string name, Transform? parent, bool sceneRoot)
    {
        DestroyRuntimeTrigger();
        triggerObject = new GameObject(name);
        if (parent != null) triggerObject.transform.SetParent(parent, false);
        else if (sceneRoot) triggerObject.transform.SetParent(null, false);
        triggerObject.transform.localPosition = Vector3.zero;
        triggerObject.transform.localRotation = Quaternion.identity;
        triggerObject.transform.localScale = Vector3.one;
        if (sceneRoot) triggerObject.layer = 0;
        trigger = triggerObject.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.radius = sceneRoot ? DroneCarrierPortLogic.AnimatedCapturePreparationRadius : RecoveryTriggerRadius;
        trigger.enabled = false;
        triggerObject.AddComponent<DroneCarrierPortTriggerRelay>().Bind(this);
    }

    public override void Rearm() { ammo = descriptor == null ? 0 : 1; SetVisual(); }
    public override int GetAmmoLoaded() => ammo > 0 ? 1 : 0;
    public override int GetAmmoTotal() => ammo > 0 ? 1 : 0;
    public override int GetFullAmmo() => 1;

    internal bool BeginLaunch(WeaponStation station, Unit owner, Unit target, GlobalPosition aimpoint, bool authority)
    {
        if (loadoutApplyInProgress || providerOwnedButInert || descriptor == null || ammo <= 0 || carrier == null || weaponStation != station ||
            !DroneCarrierPortLogic.TryBegin(ref state, authority))
            return false;
        savedOwner = owner; savedTarget = target; savedAimpoint = aimpoint; state = DroneCarrierPortState.Extending;
        return true;
    }

    internal bool CompleteLaunch(bool authority, out WeaponStation? station, out Unit? owner, out Unit? target, out GlobalPosition aimpoint)
    {
        station = weaponStation; owner = savedOwner; target = savedTarget; aimpoint = savedAimpoint;
        if (loadoutApplyInProgress || providerOwnedButInert)
        {
            savedOwner = savedTarget = null;
            DroneCarrierPortLogic.Cancel(ref state, descriptor != null);
            return false;
        }
        if (station == null || owner == null || descriptor == null || !DroneCarrierPortLogic.TryComplete(ref state, authority))
            return false;
        savedOwner = savedTarget = null; return true;
    }

    internal bool ConsumeNativeBypass(bool authority)
    {
        if (loadoutApplyInProgress || providerOwnedButInert)
        {
            DroneCarrierPortLogic.Cancel(ref state, descriptor != null);
            return false;
        }
        bool accepted = DroneCarrierPortLogic.TryConsumeBypass(ref state, authority);
        if (!accepted && !authority && descriptor == null) DroneCarrierPortLogic.Reset(ref state);
        return accepted;
    }
    internal bool ConsumeInertReleaseDenialLog()
    {
        if (!providerOwnedButInert || inertReleaseDenialLogged) return false;
        inertReleaseDenialLogged = true;
        return true;
    }
    internal bool IsAcceptedRelease(WeaponStation station) => !loadoutApplyInProgress && state == DroneCarrierPortState.Empty && descriptor == null &&
                                                               station == weaponStation;

    public override void Fire(Unit owner, Unit target, Vector3 inheritedVelocity, WeaponStation station, GlobalPosition aimpoint)
    {
        if (loadoutApplyInProgress || providerOwnedButInert)
        {
            DroneCarrierPortLogic.Cancel(ref state, descriptor != null);
            return;
        }
        if (descriptor == null || carrier == null || station != weaponStation || !DroneCarrierPortLogic.TryConsumeAcceptedFire(ref state))
            return;
        Descriptor accepted = descriptor;
        DetachDockedVisualForRelease();
        descriptor = null; provisional = null; reservedCandidate = null; ammo = 0; SetVisual();
        AdvanceLoadoutRevision("consumed");
        Plugin.ReleaseDroneCarrierPort(this, accepted.definition, accepted.loadout, accepted.livery);
    }

    internal void OnPortTriggerEnter(Collider other)
    {
        Rigidbody? body = other.attachedRigidbody;
        Aircraft? candidate = body == null ? null : body.GetComponent<Aircraft>();
        if (candidate == null || candidate.rb != body)
        {
            if (!unresolvedEntryLogged)
            {
                unresolvedEntryLogged = true;
                Debug.Log("[LoyalWingman] state=port_trigger_unresolved_entry port_state=" + state +
                          " other=" + other.name + " other_layer=" + other.gameObject.layer +
                          " attached_rb_match=false");
            }
            return;
        }
        bool firstEntry = !triggerOccupancy.TryGetValue(candidate, out HashSet<Collider>? colliders) || colliders.Count == 0;
        if (colliders == null) triggerOccupancy[candidate] = colliders = new HashSet<Collider>();
        colliders.Add(other);
        if (firstEntry && trigger != null)
            Debug.Log("[LoyalWingman] state=port_trigger_entry port_state=" + state + " candidate_pid=" + candidate.persistentID +
                      " candidate_name=" + candidate.name + " trigger_center=" + trigger.transform.position +
                      " trigger_radius=" + RecoveryRadius + " trigger_layer=" + trigger.gameObject.layer +
                      " other=" + other.name + " other_layer=" + other.gameObject.layer + " attached_rb_match=" +
                      (candidate.rb == body) + " release_suppressed=" + IsReleaseCaptureSuppressed(candidate));
        if (animatedBinding != null)
        {
            if (!HasAnimatedProvider) return;
            if (!recoveryEnabled) return;
            if (AnimatedRecoveryPose == null) return;
        }
        if (descriptor != null || reservedCandidate != null || IsReleaseCaptureSuppressed(candidate))
            return;
        Plugin.TryCaptureDroneCarrierPort(this, candidate);
    }

    internal void OnPortTriggerExit(Collider other)
    {
        Rigidbody? body = other.attachedRigidbody;
        Aircraft? candidate = body == null ? null : body.GetComponent<Aircraft>();
        if (candidate != null && candidate.rb == body && triggerOccupancy.TryGetValue(candidate, out HashSet<Collider>? colliders))
        {
            colliders.Remove(other);
            if (colliders.Count == 0)
            {
                triggerOccupancy.Remove(candidate);
                if (ReferenceEquals(releaseExcluded, candidate) && !HasAnimatedProvider) ClearReleaseExclusion();
                if (reservedCandidate == candidate && animatedBinding == null) Cancel();
            }
        }
    }

    private void InitializeAnimatedProbe()
    {
        recoveryEnabled = false;
        const int defaultLayer = 0;
        const string defaultLayerName = "Default";
        if (trigger == null || AnimatedRecoveryPose == null || LayerMask.NameToLayer(defaultLayerName) != defaultLayer ||
            LayerMask.LayerToName(defaultLayer) != defaultLayerName)
        {
            Debug.LogWarning("[LoyalWingman] state=animated_port_recovery_inert reason=default_layer");
            return;
        }
        Transform recoveryPose = animatedBinding!.RecoveryPose;
        triggerObject!.transform.SetPositionAndRotation(recoveryPose.position, recoveryPose.rotation);
        triggerObject.transform.localScale = Vector3.one;
        trigger.enabled = true;
        Debug.Log("[LoyalWingman] state=animated_port_recovery_probe enabled=true recovery_enabled=false");
    }

    private void ProbeAnimatedRecovery(Aircraft candidate, Rigidbody body)
    {
        const int defaultLayer = 0;
        const string supportedPayloadKey = "kestrel";
        if (candidate.definition == null || candidate.definition.jsonKey != supportedPayloadKey || candidate.rb != body ||
            !NativeRecoveryGuard.TryEvaluate(candidate, out string reason)) { ResetAnimatedProbe(); return; }
        bool any = false;
        foreach (Collider collider in candidate.GetComponentsInChildren<Collider>(true))
        {
            if (collider == null || !collider.enabled || collider.attachedRigidbody != body) continue;
            any = true;
            if (Physics.GetIgnoreLayerCollision(defaultLayer, collider.gameObject.layer)) { ResetAnimatedProbe(); return; }
        }
        if (!any || !HasAnimatedFinalOverlap(body)) { ResetAnimatedProbe(); return; }
        animatedProbeCandidate = candidate;
        animatedProbeEnter = animatedProbeOverlap = true;
        Debug.Log("[LoyalWingman] state=animated_port_recovery_probe_enter candidate=" + candidate.persistentID + " guard=" + reason);
    }

    private void CompleteAnimatedProbeExit(Aircraft candidate, Rigidbody body)
    {
        animatedProbeExit = ReferenceEquals(candidate.rb, body);
        if (!animatedProbeExit || !animatedProbeEnter || !animatedProbeOverlap) { ResetAnimatedProbe(); return; }
        recoveryEnabled = true;
        Debug.Log("[LoyalWingman] state=animated_port_recovery_enabled candidate=" + candidate.persistentID);
        ResetAnimatedProbe();
    }

    private void ResetAnimatedProbe() { animatedProbeCandidate = null; animatedProbeEnter = animatedProbeOverlap = animatedProbeExit = false; }

    internal bool HasAnimatedFinalOverlap(Rigidbody body)
    {
        if (!HasAnimatedProvider || body == null) return false;
        foreach (Collider collider in Physics.OverlapSphere(animatedBinding!.RecoveryPose.position, RecoveryTriggerRadius, ~0,
                     QueryTriggerInteraction.Collide))
            if (collider != null && collider.attachedRigidbody == body) return true;
        return false;
    }

    internal bool IsExactAnimatedRuntimeIdentity(Aircraft expectedCarrier, Hardpoint expectedHardpoint, WeaponStation expectedStation)
    {
        return HasAnimatedProvider && ReferenceEquals(carrier, expectedCarrier) && ReferenceEquals(portHardpoint, expectedHardpoint) &&
            ReferenceEquals(weaponStation, expectedStation) && ReferenceEquals(attachedMount, DroneCarrierPortRegistry.GenericMount) &&
            expectedStation.Weapons.Contains(this);
    }

    internal long BeginAnimatedOperation(AnimatedPortMotion motion)
    {
        if (!HasAnimatedProvider || motion == AnimatedPortMotion.None || animatedOperationToken != 0) return 0;
        if (animatedOperationCounter == long.MaxValue) return 0;
        animatedOperationCounter++;
        animatedOperationToken = animatedOperationCounter;
        animatedMotion = motion;
        nativeLaunchIssued = captureCompletionConsumed = false;
        return animatedOperationToken;
    }
    internal bool AcceptAnimatedMilestone(long token, AnimatedPortMotion expectedMotion, string milestone, string expectedMilestone) =>
        HasAnimatedProvider && DroneCarrierPortLogic.AcceptAnimatedPortMilestone(animatedOperationToken, token, animatedMotion,
            expectedMotion, milestone, expectedMilestone);
    internal bool AdvanceAnimatedMotion(long token, AnimatedPortMotion expectedMotion, AnimatedPortMotion nextMotion, string milestone,
                                       string expectedMilestone)
    {
        if (!AcceptAnimatedMilestone(token, expectedMotion, milestone, expectedMilestone)) return false;
        animatedMotion = nextMotion;
        return true;
    }
    internal bool ConsumeAnimatedNativeLaunch(long token) => HasAnimatedProvider && token == animatedOperationToken &&
        DroneCarrierPortLogic.TryConsumeAnimatedPortOnce(ref nativeLaunchIssued);
    internal bool ConsumeAnimatedCaptureCompletion(long token) => HasAnimatedProvider && token == animatedOperationToken &&
        DroneCarrierPortLogic.TryConsumeAnimatedPortOnce(ref captureCompletionConsumed);
    internal bool BeginAnimatedRetract(long token, AnimatedPortMotion expectedMotion, AnimatedPortMotion retractMotion) =>
        HasAnimatedProvider && token == animatedOperationToken && animatedMotion == expectedMotion &&
        (retractMotion == AnimatedPortMotion.RetractingRelease || retractMotion == AnimatedPortMotion.RetractingCapture) &&
        (animatedMotion = retractMotion) == retractMotion;
    internal bool CloseAnimatedFullyStowed(long token, string milestone)
    {
        if (!HasAnimatedProvider || !DroneCarrierPortLogic.CloseAnimatedPortFullyStowed(animatedOperationToken, token, animatedMotion,
                milestone)) return false;
        ResetAnimatedOperation();
        return true;
    }
    internal void ResetAnimatedOperation()
    {
        if (animatedCaptureMonitor != null) StopCoroutine(animatedCaptureMonitor);
        animatedCaptureMonitor = null; animatedCaptureCandidate = null; animatedCaptureStartedAt = 0f;
        animatedOperationToken = 0; animatedMotion = AnimatedPortMotion.None; nativeLaunchIssued = captureCompletionConsumed = false;
    }
    internal bool TryAnimatedExtend(long token) => HasAnimatedProvider && animatedBinding!.TryExtend(token);
    internal bool TryAnimatedCaptureLock(long token) => HasAnimatedProvider && animatedBinding!.TryCaptureLock(token);
    internal bool TryAnimatedRetract(long token) => HasAnimatedProvider && animatedBinding!.TryRetract(token);

    internal void BeginAnimatedCaptureMonitor(long token, Aircraft candidate)
    {
        if (!HasAnimatedProvider || token != animatedOperationToken || candidate != reservedCandidate) return;
        if (animatedCaptureMonitor != null) StopCoroutine(animatedCaptureMonitor);
        animatedCaptureCandidate = candidate; animatedCaptureStartedAt = Time.timeSinceLevelLoad;
        animatedCaptureMonitor = StartCoroutine(AnimatedCaptureMonitor(token, candidate));
    }
    internal bool EnterAnimatedCaptureWait(long token)
    {
        if (!DroneCarrierPortLogic.TryEnterAnimatedCaptureWait(animatedOperationToken, token, ref animatedMotion, "Extended")) return false;
        animatedCaptureStartedAt = Time.timeSinceLevelLoad;
        return true;
    }
    internal bool TryBeginAnimatedCaptureLock(long token) =>
        DroneCarrierPortLogic.TryBeginAnimatedCaptureLock(animatedOperationToken, token, ref animatedMotion);
    internal float AnimatedCaptureElapsed => Time.timeSinceLevelLoad - animatedCaptureStartedAt;
    internal float AnimatedCaptureSquaredDistance => animatedCaptureCandidate == null || !HasAnimatedProvider ? float.PositiveInfinity :
        DroneCarrierPortLogic.NormalizeAnimatedCaptureSquaredDistance(
            (animatedCaptureCandidate.transform.position - animatedBinding!.RecoveryPose.position).sqrMagnitude,
            IsInTrigger(animatedCaptureCandidate));
    internal float AnimatedCaptureRawSquaredDistance => animatedCaptureCandidate == null || !HasAnimatedProvider ? float.PositiveInfinity :
        (animatedCaptureCandidate.transform.position - animatedBinding!.RecoveryPose.position).sqrMagnitude;
    internal bool AnimatedCaptureTriggerOccupied => animatedCaptureCandidate != null && IsInTrigger(animatedCaptureCandidate);
    internal Vector3 AnimatedRecoveryPosePosition => HasAnimatedProvider ? animatedBinding!.RecoveryPose.position : Vector3.zero;
    private IEnumerator AnimatedCaptureMonitor(long token, Aircraft candidate)
    {
        yield return new WaitForFixedUpdate();
        while (HasAnimatedProvider && token == animatedOperationToken && candidate == reservedCandidate &&
               (animatedMotion == AnimatedPortMotion.ExtendingCapture || animatedMotion == AnimatedPortMotion.WaitingCapture))
        {
            AnimatedCaptureRecoveryDecision decision = DroneCarrierPortLogic.ResolveAnimatedCaptureRecovery(
                DroneCarrierPortLogic.NormalizeAnimatedCaptureSquaredDistance(
                    (candidate.transform.position - animatedBinding!.RecoveryPose.position).sqrMagnitude, IsInTrigger(candidate)),
                Time.timeSinceLevelLoad - animatedCaptureStartedAt, animatedMotion == AnimatedPortMotion.WaitingCapture);
            if (decision == AnimatedCaptureRecoveryDecision.AbortRange || decision == AnimatedCaptureRecoveryDecision.AbortTimeout)
            { Plugin.AbortAnimatedCapture(this, candidate, token, animatedMotion, decision == AnimatedCaptureRecoveryDecision.AbortRange ? "range" : "timeout"); yield break; }
            if (animatedMotion == AnimatedPortMotion.WaitingCapture && decision == AnimatedCaptureRecoveryDecision.BeginLock)
            { Plugin.TryBeginAnimatedCaptureLock(this, candidate, token); yield break; }
            yield return new WaitForFixedUpdate();
        }
    }

    internal void BeginReleaseExclusion()
    {
        ClearReleaseExclusion();
        if (HasAnimatedProvider) recoveryEnabled = false;
        releaseBinding = true;
    }
    internal void BindReleaseExclusion(Aircraft candidate)
    {
        releaseBinding = false;
        releaseExcluded = candidate;
        releaseExclusionCondition = StartCoroutine(ReleaseExclusionCondition(candidate));
    }
    internal void CancelReleaseExclusion() => ClearReleaseExclusion();
    private IEnumerator ReleaseExclusionCondition(Aircraft candidate)
    {
        yield return new WaitForFixedUpdate();
        while (ReferenceEquals(releaseExcluded, candidate))
        {
            Rigidbody? candidateBody = candidate == null ? null : candidate.rb;
            bool valid = candidateBody != null;
            bool occupied = valid && IsInTrigger(candidate!);
            bool geometryOverlap = valid && HasTriggerGeometryOverlap(candidateBody!);
            if (!valid)
            {
                releaseExcluded = null;
                releaseExclusionCondition = null;
                yield break;
            }
            if (!occupied && !geometryOverlap)
            {
                if (HasAnimatedProvider) recoveryEnabled = true;
                releaseExcluded = null;
                releaseExclusionCondition = null;
                yield break;
            }
            yield return new WaitForFixedUpdate();
        }
    }
    private bool HasTriggerGeometryOverlap(Rigidbody body)
    {
        if (trigger == null || !trigger.enabled) return false;
        float radius = RecoveryRadius;
        return (body.position - trigger.transform.position).sqrMagnitude <= radius * radius;
    }
    private void ClearReleaseExclusion()
    {
        if (releaseExclusionCondition != null) StopCoroutine(releaseExclusionCondition);
        releaseExclusionCondition = null;
        releaseExcluded = null;
        releaseBinding = false;
    }

    internal bool Reserve(Aircraft candidate) { if (descriptor != null || reservedCandidate != null) return false; reservedCandidate = candidate; return true; }
    internal bool ReservePlayerRecovery(Aircraft candidate)
    {
        if (descriptor != null || reservedCandidate != null || !IsInTrigger(candidate) || !IsCaptureProjectionUsable ||
            !DroneCarrierPortLogic.TryReservePlayerRecovery(ref state)) return false;
        reservedCandidate = candidate; return true;
    }
    internal bool BuildProvisional(Aircraft candidate)
    {
        if (candidate != reservedCandidate || !HasRecoveryPresence(candidate) || candidate.definition == null || candidate.Networkloadout == null) return false;
        NuclearOption.SavedMission.Loadout copied = new NuclearOption.SavedMission.Loadout { weapons = new List<WeaponMount>(candidate.Networkloadout.weapons) };
        if (!TryCreateBaselineDescriptor(candidate.definition, copied, candidate.NetworkLiveryKey, out Descriptor recovered)) return false;
        provisional = recovered;
        return true;
    }
    internal void CommitCapture()
    {
        if (provisional == null) return;
        descriptor = provisional; provisional = null; reservedCandidate = null; state = DroneCarrierPortState.Loaded; AdvanceLoadoutRevision("recovery"); SetVisual(); weaponStation?.Rearm();
    }
    internal bool BeginPlayerHandoff(Aircraft candidate)
    {
        if (!IsPlayerCallbackProjectionUsable(candidate) || !HasRecoveryPresence(candidate) || candidate.definition == null ||
            candidate.Networkloadout == null || !DroneCarrierPortLogic.TryBeginPlayerHandoff(ref state)) return false;
        NuclearOption.SavedMission.Loadout copied = new NuclearOption.SavedMission.Loadout { weapons = new List<WeaponMount>(candidate.Networkloadout.weapons) };
        if (!TryCreateBaselineDescriptor(candidate.definition, copied, candidate.NetworkLiveryKey, out Descriptor recovered)) return false;
        provisional = recovered;
        return true;
    }
    internal void CommitPlayerHandoff()
    {
        if (state != DroneCarrierPortState.PlayerHandoff || provisional == null) return;
        if (!DroneCarrierPortLogic.TryCommitPlayerHandoff(ref state)) return;
        descriptor = provisional; provisional = null; reservedCandidate = null; AdvanceLoadoutRevision("player_recovery"); SetVisual(); weaponStation?.Rearm();
    }
    internal void CancelPlayerHandoff()
    {
        if (state != DroneCarrierPortState.PlayerReserved && state != DroneCarrierPortState.PlayerHandoff) return;
        provisional = null; reservedCandidate = null; DroneCarrierPortLogic.Reset(ref state); Rearm(); SetVisual();
    }
    internal void Cancel() { provisional = null; reservedCandidate = null; savedOwner = savedTarget = null; DroneCarrierPortLogic.Cancel(ref state, descriptor != null); Rearm(); SetVisual(); }
    private bool HasRecoveryPresence(Aircraft candidate) => animatedBinding == null ? IsInTrigger(candidate) :
        HasAnimatedProvider && recoveryEnabled && candidate.rb != null && HasAnimatedFinalOverlap(candidate.rb);
    private void ResetLive()
    {
        if (descriptor != null) AdvanceLoadoutRevision("terminal_clear");
        descriptor = provisional = null; reservedCandidate = null; savedOwner = savedTarget = null; savedAimpoint = default;
        triggerOccupancy.Clear(); ClearReleaseExclusion(); ResetAnimatedOperation(); ResetAnimatedProbe(); ammo = 0; DroneCarrierPortLogic.Reset(ref state); SetVisual();
    }
    private void TerminalReset()
    {
        StopDeferredBind();
        if (reservedCandidate != null && IsPendingPlayerRecovery(reservedCandidate)) Plugin.CancelPendingAnimatedPortRecovery(this, reservedCandidate);
        ResetLive(); unresolvedEntryLogged = false; DestroyDockedVisual();
        DisposeAnimatedBinding();
        DestroyRuntimeTrigger();
    }
    internal void ClearAfterDetachedDestroyFailure()
    {
        StopDeferredBind();
        ResetLive();
        DisposeAnimatedBinding();
        DestroyRuntimeTrigger();
    }
    internal static void ClearAll() { foreach (DroneCarrierPortWeapon port in livePorts) if (port != null) port.TerminalReset(); }
    private void OnDestroy() { TerminalReset(); DestroyRuntimeTrigger(); livePorts.Remove(this); }
    private void DestroyRuntimeTrigger()
    {
        if (trigger != null) trigger.enabled = false;
        if (triggerObject != null) { triggerObject.SetActive(false); UnityEngine.Object.Destroy(triggerObject); }
        trigger = null; triggerObject = null;
        recoveryEnabled = false;
        triggerOccupancy.Clear();
        ResetAnimatedProbe();
    }
    private void StopDeferredBind()
    {
        if (bindCoroutine != null) StopCoroutine(bindCoroutine);
        bindCoroutine = null;
    }
    private void DisposeAnimatedBinding()
    {
        animatedBinding?.Dispose();
        animatedBinding = null;
        ResetAnimatedOperation();
        ResetAnimatedProbe();
        recoveryEnabled = false;
        providerOwnedButInert = inertReleaseDenialLogged = false;
    }
    private void SetVisual()
    {
        DisableTemplateVisuals();
        if (descriptor == null || carrier == null || portHardpoint == null || descriptor.definition.unitPrefab == null)
        {
            DestroyDockedVisual();
            return;
        }

        AircraftDefinition source = descriptor.definition;
        if (dockedVisualRoot != null && ReferenceEquals(dockedVisualSource, source))
        {
            dockedVisualRoot.transform.SetParent(animatedBinding != null ? null : portHardpoint.transform, true);
            PositionDockedVisual(dockedVisualRoot.transform, source);
            dockedVisualRoot.SetActive(true);
            return;
        }

        DestroyDockedVisual();
        int sourceMeshCount = 0, sourceSkinnedCount = 0;
        try
        {
            GameObject sourcePrefab = source.unitPrefab;
            sourceMeshCount = sourcePrefab.GetComponentsInChildren<MeshRenderer>(true).Length;
            sourceSkinnedCount = sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            GameObject? visual = BuildDockedVisual(sourcePrefab, source.jsonKey, out int copiedRendererCount);
            if (visual == null || copiedRendererCount == 0)
            {
                if (visual != null) UnityEngine.Object.Destroy(visual);
                LogDockedVisualFailure(source, sourceMeshCount, sourceSkinnedCount, "no_safe_renderer");
                return;
            }

            dockedVisualRoot = visual;
            dockedVisualSource = source;
            visual.transform.SetParent(portHardpoint.transform, false);
            PositionDockedVisual(visual.transform, source);
            failedDockedVisualSource = null;
            visual.SetActive(true);
        }
        catch (System.Exception e)
        {
            DestroyDockedVisual();
            LogDockedVisualFailure(source, sourceMeshCount, sourceSkinnedCount, e.GetType().Name);
        }
    }

    private void DisableTemplateVisuals()
    {
        foreach (Renderer visual in GetComponentsInChildren<Renderer>(true))
            if (visual != null) visual.enabled = false;
    }

    private void PositionDockedVisual(Transform visual, AircraftDefinition source)
    {
        if (carrier == null || portHardpoint == null) return;
        if (animatedBinding != null)
        {
            if (!HasAnimatedProvider) return;
            Transform launchPose = animatedBinding!.LaunchPose;
            if (!ReferenceEquals(visual.parent, launchPose)) visual.SetParent(launchPose, false);
            visual.localPosition = Vector3.zero;
            visual.localRotation = Quaternion.identity;
            visual.localScale = Vector3.one;
            return;
        }
        visual.position = portHardpoint.transform.position - carrier.transform.up * source.spawnOffset.y +
                          carrier.transform.forward * source.spawnOffset.z;
        visual.rotation = carrier.transform.rotation;
    }

    private static GameObject? BuildDockedVisual(GameObject sourcePrefab, string sourceKey, out int copiedRendererCount)
    {
        copiedRendererCount = 0;
        Transform sourceRoot = sourcePrefab.transform;
        GameObject visualRoot = new GameObject(sourceRoot.name);
        try
        {
            visualRoot.SetActive(false);
            visualRoot.layer = sourcePrefab.layer;
            visualRoot.transform.localPosition = sourceRoot.localPosition;
            visualRoot.transform.localRotation = sourceRoot.localRotation;
            visualRoot.transform.localScale = sourceRoot.localScale;

            Dictionary<Transform, Transform> transformMap = new Dictionary<Transform, Transform>
            {
                [sourceRoot] = visualRoot.transform
            };
            CopyTransformHierarchy(sourceRoot, visualRoot.transform, transformMap);

            HashSet<Renderer> lodRenderers = new HashSet<Renderer>();
            HashSet<Renderer> lodZeroRenderers = new HashSet<Renderer>();
            foreach (LODGroup lodGroup in sourcePrefab.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = lodGroup.GetLODs();
                for (int index = 0; index < lods.Length; index++)
                {
                    foreach (Renderer renderer in lods[index].renderers)
                    {
                        if (renderer == null) continue;
                        lodRenderers.Add(renderer);
                        if (index == 0) lodZeroRenderers.Add(renderer);
                    }
                }
            }

            foreach (MeshRenderer sourceRenderer in sourcePrefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!ShouldCopyRenderer(sourceRenderer, lodRenderers, lodZeroRenderers)) continue;
                if (System.String.Equals(sourceKey, "kestrel", System.StringComparison.Ordinal))
                {
                    string rendererPath = sourceRenderer.transform.name;
                    Transform? parent = sourceRenderer.transform.parent;
                    while (parent != null && parent != sourceRoot)
                    {
                        rendererPath = parent.name + "/" + rendererPath;
                        parent = parent.parent;
                    }
                    if (parent == sourceRoot && kestrelDockedVisualExcludedRendererPaths.Contains(rendererPath)) continue;
                }
                MeshFilter? sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null) continue;
                Transform targetTransform = transformMap[sourceRenderer.transform];
                targetTransform.gameObject.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
                MeshRenderer targetRenderer = targetTransform.gameObject.AddComponent<MeshRenderer>();
                CopyRendererSettings(sourceRenderer, targetRenderer);
                copiedRendererCount++;
            }

            foreach (SkinnedMeshRenderer sourceRenderer in sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sourceRenderer.sharedMesh == null || !ShouldCopyRenderer(sourceRenderer, lodRenderers, lodZeroRenderers)) continue;
                Transform targetTransform = transformMap[sourceRenderer.transform];
                SkinnedMeshRenderer targetRenderer = targetTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
                targetRenderer.sharedMesh = sourceRenderer.sharedMesh;
                targetRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                targetRenderer.localBounds = sourceRenderer.localBounds;
                Transform[] sourceBones = sourceRenderer.bones;
                Transform[] targetBones = new Transform[sourceBones.Length];
                for (int index = 0; index < sourceBones.Length; index++)
                    if (sourceBones[index] != null) transformMap.TryGetValue(sourceBones[index], out targetBones[index]);
                targetRenderer.bones = targetBones;
                if (sourceRenderer.rootBone != null && transformMap.TryGetValue(sourceRenderer.rootBone, out Transform targetRootBone))
                    targetRenderer.rootBone = targetRootBone;
                CopyRendererSettings(sourceRenderer, targetRenderer);
                copiedRendererCount++;
            }

            if (copiedRendererCount == 0)
            {
                visualRoot.SetActive(false);
                return visualRoot;
            }
            return visualRoot;
        }
        catch
        {
            visualRoot.SetActive(false);
            UnityEngine.Object.Destroy(visualRoot);
            throw;
        }
    }

    private static void CopyTransformHierarchy(Transform source, Transform target,
                                               Dictionary<Transform, Transform> transformMap)
    {
        for (int index = 0; index < source.childCount; index++)
        {
            Transform sourceChild = source.GetChild(index);
            GameObject targetObject = new GameObject(sourceChild.name);
            targetObject.layer = sourceChild.gameObject.layer;
            Transform targetChild = targetObject.transform;
            targetChild.SetParent(target, false);
            targetChild.localPosition = sourceChild.localPosition;
            targetChild.localRotation = sourceChild.localRotation;
            targetChild.localScale = sourceChild.localScale;
            targetObject.SetActive(sourceChild.gameObject.activeSelf);
            transformMap[sourceChild] = targetChild;
            CopyTransformHierarchy(sourceChild, targetChild, transformMap);
        }
    }

    private static bool ShouldCopyRenderer(Renderer renderer, HashSet<Renderer> lodRenderers,
                                           HashSet<Renderer> lodZeroRenderers) =>
        !lodRenderers.Contains(renderer) || lodZeroRenderers.Contains(renderer);

    private static void CopyRendererSettings(Renderer source, Renderer target)
    {
        target.sharedMaterials = source.sharedMaterials;
        target.enabled = source.enabled;
        target.shadowCastingMode = source.shadowCastingMode;
        target.receiveShadows = source.receiveShadows;
    }

    private void DestroyDockedVisual()
    {
        if (dockedVisualRoot != null)
        {
            dockedVisualRoot.SetActive(false);
            dockedVisualRoot.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(dockedVisualRoot);
        }
        dockedVisualRoot = null;
        dockedVisualSource = null;
    }

    private void DetachDockedVisualForRelease()
    {
        if (animatedBinding == null || dockedVisualRoot == null) return;
        dockedVisualRoot.transform.SetParent(null, true);
    }

    private void LogDockedVisualFailure(AircraftDefinition source, int meshCount, int skinnedCount, string reason)
    {
        if (ReferenceEquals(failedDockedVisualSource, source)) return;
        failedDockedVisualSource = source;
        Debug.LogWarning("[LoyalWingman] docked_visual_unavailable source=" + source.name + " mesh=" + meshCount +
                         " skinned=" + skinnedCount + " reason=" + reason);
    }

    private static bool TryInitialDescriptor(Aircraft carrier, out Descriptor descriptor)
    {
        descriptor = null!;
        if (Encyclopedia.Lookup == null || !Encyclopedia.Lookup.TryGetValue("kestrel", out UnitDefinition unit) ||
            unit is not AircraftDefinition definition || definition.aircraftParameters?.loadouts == null ||
            definition.aircraftParameters.loadouts.Count <= 1 || definition.aircraftParameters.loadouts[1]?.weapons == null ||
            carrier.NetworkHQ == null) return false;
        return TryCreateBaselineDescriptor(definition, new NuclearOption.SavedMission.Loadout
        { weapons = new List<WeaponMount>(definition.aircraftParameters.loadouts[1].weapons) },
        new LiveryKey(definition.aircraftParameters.GetFirstLiveryForFaction(carrier.NetworkHQ.faction)), out descriptor);
    }

    internal static bool TryGetLoadoutValue(AircraftDefinition definition, NuclearOption.SavedMission.Loadout loadout,
                                            out float value)
    {
        value = 0f;
        Aircraft? prefab = definition == null || definition.unitPrefab == null ? null : definition.unitPrefab.GetComponent<Aircraft>();
        if (prefab == null || prefab.weaponManager == null || prefab.weaponManager.hardpointSets == null || loadout == null ||
            loadout.weapons == null || loadout.weapons.Count != prefab.weaponManager.hardpointSets.Length) return false;
        for (int index = 0; index < loadout.weapons.Count; index++)
        {
            WeaponMount? weapon = loadout.weapons[index];
            if (weapon == null) continue;
            HardpointSet? set = prefab.weaponManager.hardpointSets[index];
            if (set == null || set.hardpoints == null || !MountedDroneLoadoutLogic.TryAccumulateCandidateValue(value,
                    set.hardpoints.Count, weapon.emptyCost, weapon.info == null ? 0f : weapon.info.costPerRound, weapon.ammo, out value)) return false;
        }
        return true;
    }

    private static bool TryCreateBaselineDescriptor(AircraftDefinition definition, NuclearOption.SavedMission.Loadout loadout,
                                                    LiveryKey livery, out Descriptor descriptor)
    {
        descriptor = null!;
        if (!TryGetLoadoutValue(definition, loadout, out float baseline)) return false;
        descriptor = new Descriptor(definition, loadout, livery, baseline, 0f);
        return true;
    }

    internal static bool TryCreateAppliedDescriptor(Descriptor existing, NuclearOption.SavedMission.Loadout candidate,
                                                    float newPaidSurcharge, out Descriptor descriptor)
    {
        descriptor = null!;
        if (existing == null || candidate == null || float.IsNaN(newPaidSurcharge) || float.IsInfinity(newPaidSurcharge) || newPaidSurcharge < 0f)
            return false;
        descriptor = new Descriptor(existing.definition, candidate, existing.livery, existing.includedBaselineValue, newPaidSurcharge);
        return true;
    }
    internal static bool TryCreateFreeAppliedDescriptor(Descriptor existing, NuclearOption.SavedMission.Loadout candidate,
                                                        out Descriptor descriptor)
    {
        descriptor = null!;
        if (existing == null || candidate == null || !TryGetLoadoutValue(existing.definition, candidate, out float baseline)) return false;
        descriptor = new Descriptor(existing.definition, candidate, existing.livery, baseline, 0f);
        return true;
    }

    internal bool TryGetLoadoutSnapshot(out MountedDroneLoadoutSnapshot snapshot, out string reason)
    {
        snapshot = default;
        if (loadoutApplyInProgress) { reason = "port_busy"; return false; }
        if (providerOwnedButInert) { reason = "port_inert"; return false; }
        if (provisional != null || reservedCandidate != null) { reason = "port_busy"; return false; }
        if (descriptor == null || state == DroneCarrierPortState.Empty) { reason = "port_not_loaded"; return false; }
        if (descriptor.loadout == null || descriptor.loadout.weapons == null) { reason = "definition_invalid"; return false; }
        if (state != DroneCarrierPortState.Loaded || carrier == null || portHardpoint == null || attachedMount == null || weaponStation == null || !weaponStation.Weapons.Contains(this)) { reason = "port_not_bound"; return false; }
        snapshot = new MountedDroneLoadoutSnapshot(carrier, portHardpoint, attachedMount, weaponStation, descriptor, loadoutRevision,
            new NuclearOption.SavedMission.Loadout { weapons = new List<WeaponMount>(descriptor.loadout.weapons) });
        reason = "ok";
        return true;
    }

    internal bool TryEnterLoadoutApply(MountedDroneLoadoutSnapshot expected)
    {
        if (loadoutApplyInProgress || expected.descriptor == null || !ReferenceEquals(descriptor, expected.descriptor) ||
            expected.revision != loadoutRevision || state != DroneCarrierPortState.Loaded || provisional != null || reservedCandidate != null ||
            !ReferenceEquals(carrier, expected.carrier) || !ReferenceEquals(portHardpoint, expected.hardpoint) ||
            !ReferenceEquals(attachedMount, expected.mount) || !ReferenceEquals(weaponStation, expected.station))
            return false;
        loadoutApplyInProgress = true;
        return true;
    }

    internal bool CommitLoadoutApply(MountedDroneLoadoutSnapshot expected, Descriptor appliedDescriptor, long nextRevision)
    {
        if (!loadoutApplyInProgress || expected.descriptor == null || appliedDescriptor == null || !ReferenceEquals(descriptor, expected.descriptor) ||
            expected.revision != loadoutRevision || loadoutRevision == long.MaxValue || nextRevision != loadoutRevision + 1) return false;
        descriptor = appliedDescriptor;
        loadoutRevision = nextRevision;
        Debug.Log("[LoyalWingman] state=mounted_loadout_apply_ready revision=" + loadoutRevision);
        return true;
    }

    internal void EndLoadoutApply() { loadoutApplyInProgress = false; }
    internal void ProjectCommittedLoadout()
    {
        try { SetVisual(); weaponStation?.Rearm(); }
        catch (Exception e) { Debug.LogError("[LoyalWingman] state=mounted_loadout_projection_failed reason=" + e.GetType().Name); }
    }

    private void AdvanceLoadoutRevision(string reason)
    {
        if (loadoutRevision == long.MaxValue)
        {
            if (!loadoutRevisionExhaustionLogged) { loadoutRevisionExhaustionLogged = true; Debug.LogError("[LoyalWingman] state=mounted_loadout_revision_exhausted reason=" + reason); }
            return;
        }
        loadoutRevision++;
        Debug.Log("[LoyalWingman] state=mounted_loadout_revision reason=" + reason + " revision=" + loadoutRevision);
    }
}

internal sealed class DroneCarrierPortTriggerRelay : MonoBehaviour
{
    private DroneCarrierPortWeapon? owner;
    internal void Bind(DroneCarrierPortWeapon value) { owner = value; }
    private void OnTriggerEnter(Collider other) { owner?.OnPortTriggerEnter(other); }
    private void OnTriggerExit(Collider other) { owner?.OnPortTriggerExit(other); }
}

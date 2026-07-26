using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace LoyalWingman;

internal static class AnimatedDronePortProvider
{
    private const BindingFlags PublicInstanceDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static readonly List<ProviderService> services = new();
    private static readonly Dictionary<AircraftDefinition, HashSet<object>> definitionProviders =
        new Dictionary<AircraftDefinition, HashSet<object>>(ReferenceIdentityComparer<AircraftDefinition>.Instance);
    private static ManualLogSource? log;

    internal static bool Install(WeaponMount portMount, ManualLogSource installLog)
    {
        log = installLog;
        Refresh();
        bool accepted = true;
        foreach (ProviderService service in services)
        {
            HashSet<AircraftDefinition> before = DefinitionsContainingMount(portMount);
            try
            {
                if (!(bool)service.Install.Invoke(service.Instance, new object[] { portMount })!)
                {
                    accepted = false;
                    log.LogWarning("[LoyalWingman] animated port provider rejected install");
                }
            }
            catch (Exception exception)
            {
                accepted = false;
                log.LogWarning("[LoyalWingman] animated port provider install failed: " + exception.GetType().Name);
            }
            finally
            {
                HashSet<AircraftDefinition> after = DefinitionsContainingMount(portMount);
                foreach (AircraftDefinition definition in after)
                {
                    if (before.Contains(definition)) continue;
                    if (!definitionProviders.TryGetValue(definition, out HashSet<object>? providers))
                    {
                        providers = new HashSet<object>(ReferenceIdentityComparer<object>.Instance);
                        definitionProviders.Add(definition, providers);
                    }
                    providers.Add(service.Instance);
                }
            }
        }
        return accepted;
    }

    internal static bool TryBind(Aircraft aircraft, Hardpoint hardpoint, Weapon weapon, WeaponStation station,
                                 Action<long, string> milestone, out AnimatedDronePortBinding? binding, out string reason)
    {
        binding = null;
        reason = "no_provider";
        if (services.Count == 0 || ServicesAreStale()) Refresh();

        AircraftDefinition? definition = aircraft.definition;
        HashSet<object>? expectedProviders = null;
        int expectedProviderCount = definition != null && definitionProviders.TryGetValue(definition, out expectedProviders)
            ? expectedProviders.Count
            : 0;
        AnimatedProviderBindDecision decision = DroneCarrierPortLogic.ResolveAnimatedProviderBind(expectedProviderCount, false, 0);
        if (expectedProviderCount == 0)
            return decision == AnimatedProviderBindDecision.Animated;
        if (expectedProviderCount > 1)
        {
            reason = "provider_ambiguous";
            return decision == AnimatedProviderBindDecision.Animated;
        }

        object expectedInstance = null!;
        foreach (object instance in expectedProviders!) { expectedInstance = instance; break; }
        ProviderService? expectedService = null;
        foreach (ProviderService service in services)
            if (ReferenceEquals(service.Instance, expectedInstance)) { expectedService = service; break; }
        if (expectedService == null)
        {
            reason = "provider_invalid";
            decision = DroneCarrierPortLogic.ResolveAnimatedProviderBind(1, false, 0);
            return decision == AnimatedProviderBindDecision.Animated;
        }

        if (!TryBindService(expectedService, aircraft, hardpoint, weapon, station, milestone,
                out AnimatedDronePortBinding? expectedBinding))
        {
            reason = "provider_invalid";
            decision = DroneCarrierPortLogic.ResolveAnimatedProviderBind(1, false, 0);
            return decision == AnimatedProviderBindDecision.Animated;
        }

        var additionalBindings = new List<AnimatedDronePortBinding>();
        foreach (ProviderService service in services)
        {
            if (ReferenceEquals(service.Instance, expectedInstance)) continue;
            if (TryBindService(service, aircraft, hardpoint, weapon, station, milestone,
                    out AnimatedDronePortBinding? additionalBinding))
                additionalBindings.Add(additionalBinding!);
        }

        decision = DroneCarrierPortLogic.ResolveAnimatedProviderBind(1, true, additionalBindings.Count);
        if (decision == AnimatedProviderBindDecision.Animated)
        {
            binding = expectedBinding;
            reason = string.Empty;
            return true;
        }
        expectedBinding!.Dispose();
        foreach (AnimatedDronePortBinding additionalBinding in additionalBindings) additionalBinding.Dispose();
        reason = "provider_ambiguous";
        return false;
    }

    private static bool TryBindService(ProviderService service, Aircraft aircraft, Hardpoint hardpoint, Weapon weapon,
                                       WeaponStation station, Action<long, string> milestone,
                                       out AnimatedDronePortBinding? binding)
    {
        binding = null;
        Component? controller;
        try
        {
            controller = (Component?)service.Bind.Invoke(service.Instance, new object[] { aircraft, hardpoint, weapon, station });
        }
        catch (Exception exception)
        {
            Log("animated port provider bind failed: " + exception.GetType().Name);
            return false;
        }
        if (ReferenceEquals(controller, null))
        {
            Log("animated port provider returned no controller");
            return false;
        }
        if (controller == null || !AnimatedDronePortBinding.TryCreate(controller, milestone, out binding) || binding is null)
        {
            Log("animated port provider returned an invalid controller");
            binding = null;
            return false;
        }
        return true;
    }

    private static HashSet<AircraftDefinition> DefinitionsContainingMount(WeaponMount mount)
    {
        var definitions = new HashSet<AircraftDefinition>(ReferenceIdentityComparer<AircraftDefinition>.Instance);
        if (Encyclopedia.Lookup == null) return definitions;
        foreach (UnitDefinition unit in Encyclopedia.Lookup.Values)
        {
            if (unit is not AircraftDefinition definition) continue;
            bool contains = false;
            if (definition.unitPrefab != null)
                foreach (WeaponManager manager in definition.unitPrefab.GetComponentsInChildren<WeaponManager>(true))
                {
                    if (manager?.hardpointSets == null) continue;
                    foreach (HardpointSet set in manager.hardpointSets)
                    {
                        if (set?.weaponOptions == null) continue;
                        foreach (WeaponMount option in set.weaponOptions)
                            if (ReferenceEquals(option, mount)) { contains = true; break; }
                        if (contains) break;
                    }
                    if (contains) break;
                }
            if (!contains && definition.aircraftParameters?.loadouts != null)
                foreach (NuclearOption.SavedMission.Loadout loadout in definition.aircraftParameters.loadouts)
                {
                    if (loadout?.weapons == null) continue;
                    foreach (WeaponMount weapon in loadout.weapons)
                        if (ReferenceEquals(weapon, mount)) { contains = true; break; }
                    if (contains) break;
                }
            if (contains) definitions.Add(definition);
        }
        return definitions;
    }

    private static void Refresh()
    {
        services.Clear();
        foreach (var plugin in Chainloader.PluginInfos.Values)
        {
            object? instance = plugin.Instance;
            if (instance == null) continue;
            Type type = instance.GetType();
            PropertyInfo? version = type.GetProperty("AnimatedDronePortProviderVersion", PublicInstanceDeclared);
            MethodInfo? install = type.GetMethod("TryInstallAnimatedDronePort", PublicInstanceDeclared, null,
                new[] { typeof(WeaponMount) }, null);
            MethodInfo? bind = type.GetMethod("TryBindAnimatedDronePort", PublicInstanceDeclared, null,
                new[] { typeof(Aircraft), typeof(Hardpoint), typeof(Weapon), typeof(WeaponStation) }, null);
            if (version == null || version.PropertyType != typeof(int) || version.GetIndexParameters().Length != 0 ||
                version.GetGetMethod(false) == null || install?.ReturnType != typeof(bool) || bind?.ReturnType != typeof(Component)) continue;
            try
            {
                if ((int)version.GetValue(instance)! == 1) services.Add(new ProviderService(instance, install, bind));
            }
            catch (Exception exception)
            {
                Log("animated port provider discovery failed: " + exception.GetType().Name);
            }
        }
    }

    private static bool ServicesAreStale()
    {
        foreach (ProviderService service in services)
            if (service.Instance is UnityEngine.Object unityObject && unityObject == null) return true;
        return false;
    }

    private static void Log(string message) => log?.LogWarning("[LoyalWingman] " + message);

    private sealed class ProviderService
    {
        internal readonly object Instance;
        internal readonly MethodInfo Install;
        internal readonly MethodInfo Bind;
        internal ProviderService(object instance, MethodInfo install, MethodInfo bind)
        { Instance = instance; Install = install; Bind = bind; }
    }
}

internal sealed class AnimatedDronePortBinding : IDisposable
{
    private const BindingFlags PublicInstanceDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private readonly Component controller;
    private readonly MethodInfo request;
    private readonly EventInfo milestone;
    private readonly string portId;
    private readonly string bindingId;
    private readonly Action<string, string, long, string> callback;
    private bool disposed;

    public Transform LaunchPose { get; }
    public Transform RecoveryPose { get; }
    public bool IsValid => !disposed && controller != null && LaunchPose != null && RecoveryPose != null;

    private AnimatedDronePortBinding(Component controller, MethodInfo request, EventInfo milestone, string portId, string bindingId,
                                     Transform launchPose, Transform recoveryPose, Action<long, string> forward)
    {
        this.controller = controller;
        this.request = request;
        this.milestone = milestone;
        this.portId = portId;
        this.bindingId = bindingId;
        LaunchPose = launchPose;
        RecoveryPose = recoveryPose;
        callback = (eventPortId, eventBindingId, token, name) =>
        {
            if (eventPortId == this.portId && eventBindingId == this.bindingId) forward(token, name);
        };
        milestone.AddEventHandler(controller, callback);
    }

    internal static bool TryCreate(Component controller, Action<long, string> forward, out AnimatedDronePortBinding? binding)
    {
        binding = null;
        Type type = controller.GetType();
        PropertyInfo? version = type.GetProperty("AnimatedDronePortControllerVersion", PublicInstanceDeclared);
        PropertyInfo? portId = type.GetProperty("PortId", PublicInstanceDeclared);
        PropertyInfo? bindingId = type.GetProperty("BindingId", PublicInstanceDeclared);
        PropertyInfo? launchPose = type.GetProperty("LaunchPose", PublicInstanceDeclared);
        PropertyInfo? recoveryPose = type.GetProperty("RecoveryPose", PublicInstanceDeclared);
        MethodInfo? request = type.GetMethod("TryRequest", PublicInstanceDeclared, null,
            new[] { typeof(string), typeof(string), typeof(long), typeof(string) }, null);
        EventInfo? milestone = type.GetEvent("Milestone", PublicInstanceDeclared);
        if (!Readable(version, typeof(int)) || !Readable(portId, typeof(string)) || !Readable(bindingId, typeof(string)) ||
            !Readable(launchPose, typeof(Transform)) || !Readable(recoveryPose, typeof(Transform)) ||
            request?.ReturnType != typeof(bool) || milestone?.EventHandlerType != typeof(Action<string, string, long, string>) ||
            milestone.GetAddMethod(false) == null || milestone.GetRemoveMethod(false) == null) return false;
        try
        {
            if ((int)version!.GetValue(controller)! != 1) return false;
            string? opaquePortId = (string?)portId!.GetValue(controller);
            string? opaqueBindingId = (string?)bindingId!.GetValue(controller);
            Transform? launch = (Transform?)launchPose!.GetValue(controller);
            Transform? recovery = (Transform?)recoveryPose!.GetValue(controller);
            if (string.IsNullOrEmpty(opaquePortId) || string.IsNullOrEmpty(opaqueBindingId) || launch == null || recovery == null ||
                launch == recovery) return false;
            binding = new AnimatedDronePortBinding(controller, request, milestone, opaquePortId, opaqueBindingId, launch, recovery, forward);
            return true;
        }
        catch { return false; }
    }

    public bool TryExtend(long operationToken) => Request(operationToken, "Extend");
    public bool TryCaptureLock(long operationToken) => Request(operationToken, "CaptureLock");
    public bool TryRetract(long operationToken) => Request(operationToken, "Retract");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { milestone.RemoveEventHandler(controller, callback); }
        catch { }
    }

    private bool Request(long operationToken, string command)
    {
        if (!IsValid) return false;
        try { return (bool)request.Invoke(controller, new object[] { portId, bindingId, operationToken, command })!; }
        catch { return false; }
    }

    private static bool Readable(PropertyInfo? property, Type type) => property != null && property.PropertyType == type &&
        property.GetIndexParameters().Length == 0 && property.GetGetMethod(false) != null;
}

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LoyalWingman;

internal sealed class FqCradleLoadoutUi : IDisposable
{
    private const string HarmonyId = Plugin.Guid + ".fq_cradle_loadout_ui";
    private static FqCradleLoadoutUi? installed;
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private readonly Harmony harmony = new Harmony(HarmonyId);
    private AccessTools.FieldRef<LoadoutSelector, Aircraft>? aircraftField;
    private AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>>? weaponSelectorsField;
    private AccessTools.FieldRef<LoadoutSelector, Transform>? backgroundTransformField;
    private LoadoutSelector? selector;
    private Aircraft? preview;
    private FqCradleLoadoutPanel? panel;
    private int selectedCradleCount;
    private bool patched;

    internal FqCradleLoadoutUi(Plugin plugin, ManualLogSource log)
    {
        this.plugin = plugin;
        this.log = log;
        TryInstall();
    }

    private void TryInstall()
    {
        try
        {
            var target = AccessTools.Method(typeof(LoadoutSelector), nameof(LoadoutSelector.UpdateWeapons),
                                            new[] { typeof(bool) });
            var postfix = AccessTools.Method(typeof(FqCradleLoadoutUi), nameof(UpdateWeaponsPostfix));
            var menuDestroy = AccessTools.Method(typeof(AircraftSelectionMenu), "OnDestroy", Type.EmptyTypes);
            var menuDestroyPrefix = AccessTools.Method(typeof(FqCradleLoadoutUi), nameof(MenuDestroyPrefix));
            if (target == null || postfix == null || menuDestroy == null || menuDestroyPrefix == null)
            {
                log.LogError("[LoyalWingman] state=fq_loadout_ui_unavailable reason=target");
                return;
            }
            aircraftField = AccessTools.FieldRefAccess<LoadoutSelector, Aircraft>("aircraft");
            weaponSelectorsField =
                AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");
            backgroundTransformField = AccessTools.FieldRefAccess<LoadoutSelector, Transform>("backgroundTransform");
            if (aircraftField == null || weaponSelectorsField == null || backgroundTransformField == null)
            {
                log.LogError("[LoyalWingman] state=fq_loadout_ui_unavailable reason=fields");
                return;
            }
            installed = this;
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            harmony.Patch(menuDestroy, prefix: new HarmonyMethod(menuDestroyPrefix));
            patched = true;
            log.LogInfo("[LoyalWingman] state=fq_loadout_ui_installed");
        }
        catch (Exception e)
        {
            installed = null;
            harmony.UnpatchSelf();
            log.LogError("[LoyalWingman] state=fq_loadout_ui_unavailable reason=" + e.GetType().Name);
        }
    }

    private static void UpdateWeaponsPostfix(LoadoutSelector __instance)
    {
        try
        {
            installed?.AfterUpdateWeapons(__instance);
        }
        catch (Exception e)
        {
            installed?.log.LogError("[LoyalWingman] state=fq_loadout_ui_error error=" + e.GetType().Name);
        }
    }

    private static void MenuDestroyPrefix()
    {
        try
        {
            installed?.ClearPage();
        }
        catch (Exception e)
        {
            installed?.log.LogError("[LoyalWingman] state=fq_loadout_ui_error error=" + e.GetType().Name);
        }
    }

    private void AfterUpdateWeapons(LoadoutSelector updatedSelector)
    {
        if (aircraftField == null || weaponSelectorsField == null || backgroundTransformField == null)
            return;
        Aircraft updatedPreview = aircraftField(updatedSelector);
        int cradleCount = updatedPreview == null ? 0 : plugin.GetSelectedFqCradleCount(updatedPreview);
        if (cradleCount <= 0)
        {
            if (selector != null || preview != null || panel != null)
            {
                DestroyPanel();
                selector = null;
                preview = null;
                selectedCradleCount = 0;
            }
            plugin.ClearActiveFqLoadoutPlan("cradle_deselected");
            return;
        }

        bool sameSelection = selector == updatedSelector && preview == updatedPreview && selectedCradleCount == cradleCount;
        if (sameSelection && panel != null && panel.Alive)
            return;

        if (sameSelection && panel != null)
        {
            CarrierFqLoadoutPlan retainedPlan = panel.PlanCopy;
            bool retainedExpanded = panel.Expanded;
            DestroyPanel();
            panel = FqCradleLoadoutPanel.TryCreate(plugin, retainedPlan, weaponSelectorsField(updatedSelector),
                                                   backgroundTransformField(updatedSelector), log,
                                                   retainedExpanded);
            return;
        }

        DestroyPanel();
        selector = updatedSelector;
        preview = updatedPreview;
        selectedCradleCount = cradleCount;
        if (!plugin.TryCreateDefaultFqLoadoutPlan(cradleCount, out CarrierFqLoadoutPlan? plan, out string reason) || plan == null)
        {
            plugin.ClearActiveFqLoadoutPlan("cradle_default_failed");
            log.LogError("[LoyalWingman] state=fq_loadout_ui_unavailable reason=" + reason);
            return;
        }
        plugin.SetActiveFqLoadoutPlan(plan);
        panel = FqCradleLoadoutPanel.TryCreate(plugin, plan, weaponSelectorsField(updatedSelector),
                                               backgroundTransformField(updatedSelector), log,
                                               expanded: false);
    }

    private void DestroyPanel()
    {
        panel?.Dispose();
        panel = null;
    }

    private void ClearPage()
    {
        DestroyPanel();
        selector = null;
        preview = null;
        selectedCradleCount = 0;
    }

    internal void ClearScene()
    {
        ClearPage();
    }

    public void Dispose()
    {
        ClearScene();
        if (installed == this)
            installed = null;
        if (patched)
        {
            harmony.UnpatchSelf();
            patched = false;
        }
    }
}

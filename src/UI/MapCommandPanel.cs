using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LoyalWingman;

// Local-only full-map command surface. DynamicMap remains authoritative for map input and target selection.
internal sealed class MapCommandPanel
{
    private static readonly Color Amber = C(212, 165, 87), Red = C(216, 93, 89), DarkRed = C(86, 31, 31);
    private readonly Plugin plugin;
    private readonly Action<string> log;
    private readonly List<Aircraft> roster = new List<Aircraft>();
    private readonly Aircraft?[] activeSlots = new Aircraft?[WingmanGroupLogic.GroupSize];
    private readonly Dictionary<long, LostEntry> lostEntries = new Dictionary<long, LostEntry>();
    private readonly List<int> visibleGroupIds = new List<int>();
    private readonly HashSet<Aircraft> selection = new HashSet<Aircraft>();
    private DynamicMap? map;
    private GameplayUI? gameplay;
    private GameObject? canvasObject, root, damageRoot;
    private Transform? overlaysRoot;
    private Ui.Element? panel, feedbackBox, commandBox, directMissionDivider, missionRecoveryDivider;
    private RectTransform? panelRect;
    private Ui.TextElement? flightLabel, flightCount, groupLabel, feedback, selectionSummary, targetSummary, seadTargetSummary,
                            antiShipTargetSummary, candidateSummary, emptyStatus, emptyPrompt;
    private RowView? homeRow;
    private readonly RowView?[] droneRows = new RowView?[4];
    private Ui.ButtonElement? groupPrevious, groupNext, takeControl, auto, follow, loiter, cruise, a2a, sead, asuw, cas, strike, cap, standDown, rtb, clearTarget;
    private readonly List<OverlayView> selectedOverlays = new List<OverlayView>();
    private OverlayView? candidateOverlay, a2aTargetOverlay, seadTargetOverlay, antiShipTargetOverlay;
    private Ui.EntrySlot? entrySlot;
    private Aircraft? selected, home;
    private Unit? candidate;
    private Aircraft? a2aTarget;
    private Unit? seadTarget;
    private Ship? antiShipTarget;
    private bool a2aTargetsMultiple, seadTargetsMultiple, antiShipTargetsMultiple;
    private bool casProjectionSelected, casProjectionAvailable, casProjectionMultiple;
    private GlobalPosition casCenter;
    private bool capProjectionSelected, capProjectionAvailable, capProjectionMultiple;
    private GlobalPosition capAnchor;
    private bool strikeProjectionAvailable, strikeProjectionMultiple;
    private int strikeTargetCount;
    private Aircraft? damageAircraft;
    private readonly List<object> damageParts = new List<object>();
    private int currentGroupId, pendingCandidateFrame = -1, pointCandidateFrame = -1;
    private uint observedSessionId;
    private bool observedSessionLocal;
    private readonly HashSet<Aircraft> observedNativeManagedSelection = new HashSet<Aircraft>();
    private Aircraft? observedNativeManagedPrimary;
    private bool hasObservedNativeManagedSelection;
    private float nextRefresh, feedbackUntil;
    private bool visible, hasSession;
    private Color rowColor, raisedColor, buttonColor, disabledBackground, hudGreen, mutedGreen, disabledGreen;
    private GameObject? chatVisual, chatInput;
    private Component? chatCanvasGroup;
    private float chatAlpha;
    private bool chatInteractable, chatBlocksRaycasts, chatGroupAdded, chatHidden, chatBindingRejected;
    private MapClickRelay? mapClickRelay;
    private bool mapBackgroundRaycast, mapRelayEnabled, hasPointCandidate;
    private GlobalPosition pointCandidate;
    private MapWaypoint? pointWaypoint;
    private GameObject? pointMarker, pointVector;

    internal MapCommandPanel(Plugin plugin, Action<string> log) { this.plugin = plugin; this.log = log; }

    internal void Tick(bool enabled)
    {
        BindMap();
        SetMapRelayEnabled(enabled);
        bool mapOpen = map != null && DynamicMap.mapMaximized;
        if (entrySlot != null) entrySlot.SetLoyalWingman(enabled, mapOpen);
        bool screenVisible = enabled && mapOpen && entrySlot != null && entrySlot.IsScreenActive;
        UpdateChatVisual(screenVisible);
        if (screenVisible != visible)
        {
            visible = screenVisible; candidate = null; pendingCandidateFrame = -1; nextRefresh = 0f;
            if (visible)
            {
                ObserveGroupSession();
                ReadCandidate();
                if (candidate == null) pendingCandidateFrame = Time.frameCount + 1;
                Refresh();
            }
            else { HideSelectedOverlays(); observedSessionId = 0; observedSessionLocal = false; ResetNativeManagedObservation(); ResetGroupState(true); }
        }
        if (!enabled || !mapOpen) { hasPointCandidate = false; DestroyPointMarker(); }
        else if (!screenVisible) DestroyPointMarker();
        if (!enabled || !mapOpen || !screenVisible || root == null) return;
        bool sessionNow = plugin.MapHasSession;
        if (sessionNow != hasSession) { nextRefresh = 0f; Refresh(); }
        if (hasSession && plugin.MapSelectAllPressed) SelectAll();
        Layout(); if (hasSession) PollMapCandidate(); UpdateOverlays(); UpdatePointMarker();
        if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .1f; Refresh(); }
    }

    internal void ResetScene()
    {
        UnbindMap(); DestroyUi(); selected = home = null; candidate = null; a2aTarget = null; seadTarget = null; antiShipTarget = null; a2aTargetsMultiple = seadTargetsMultiple = antiShipTargetsMultiple = false;
        selection.Clear(); roster.Clear(); hasPointCandidate = false; ResetCasProjection(); ResetCapProjection(); ResetStrikeProjection();
        ResetGroupState(false);
    }
    internal void Dispose() => ResetScene();

    private void BindMap()
    {
        DynamicMap? current = SceneSingleton<DynamicMap>.i; GameplayUI? currentUi = SceneSingleton<GameplayUI>.i;
        if (ReferenceEquals(current, map) && ReferenceEquals(currentUi, gameplay) &&
            !Ui.IsDestroyed(current) && !Ui.IsDestroyed(currentUi)) return;
        UnbindMap(); DestroyUi(); map = current; gameplay = currentUi;
        if (map == null || gameplay == null) return;
        entrySlot = Ui.BindEntrySlot(gameplay, log, out string bindReason);
        BindMapRelay();
        if (entrySlot == null) log("state=map_panel_entry_unavailable reason=" + bindReason);
        else if (!EnsureUi()) log("state=map_panel_entry_unavailable reason=left_screen_3_install_failed");
    }
    private void UnbindMap()
    {
        Ui.EntrySlot? staleEntry = entrySlot;
        try
        {
            RestoreChatVisual(true); UnbindMapRelay(); DestroyPointMarker();
        }
        finally
        {
            try { staleEntry?.Dispose(); }
            finally
            {
                entrySlot = null; map = null; gameplay = null; visible = false; hasPointCandidate = false;
            }
        }
    }

    private void BindMapRelay()
    {
        if (map == null || mapClickRelay != null) return;
        mapBackgroundRaycast = map.mapBackground.raycastTarget;
        mapClickRelay = map.mapBackground.gameObject.AddComponent<MapClickRelay>();
        mapClickRelay.owner = this;
    }
    private void SetMapRelayEnabled(bool enabled)
    {
        if (map == null || mapClickRelay == null || mapRelayEnabled == enabled) return;
        mapRelayEnabled = enabled; map.mapBackground.raycastTarget = enabled || mapBackgroundRaycast;
        if (!enabled) { hasPointCandidate = false; DestroyPointMarker(); }
    }
    private void UnbindMapRelay()
    {
        if (map != null && mapClickRelay != null) map.mapBackground.raycastTarget = mapBackgroundRaycast;
        if (mapClickRelay != null) UnityEngine.Object.Destroy(mapClickRelay);
        mapClickRelay = null; mapRelayEnabled = false;
    }
    private void MapBackgroundClicked(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left || !mapRelayEnabled || !visible || map == null || !DynamicMap.mapMaximized) return;
        if (panelRect != null && Ui.Contains(panelRect, eventData.position)) return;
        if (!map.TryGetCursorCoordinates(out pointCandidate)) return;
        hasPointCandidate = true; candidate = null; pendingCandidateFrame = -1; pointCandidateFrame = Time.frameCount; nextRefresh = 0f;
        UpdatePointMarker();
    }

    private void UpdateChatVisual(bool drawerActive)
    {
        if (!drawerActive)
        {
            RestoreChatVisual(false);
            return;
        }
        if (!BindChatVisual())
            return;
        if (chatInput != null && chatInput.activeSelf)
        {
            RestoreChatVisual(false);
            return;
        }
        if (chatHidden || chatCanvasGroup == null)
            return;
        // Hide only the chat artwork while the drawer is active; opening the input must restore native interaction.
        chatAlpha = (float)(Ui.GetValue(chatCanvasGroup, "alpha") ?? 1f);
        chatInteractable = (bool)(Ui.GetValue(chatCanvasGroup, "interactable") ?? true);
        chatBlocksRaycasts = (bool)(Ui.GetValue(chatCanvasGroup, "blocksRaycasts") ?? true);
        Ui.SetValue(chatCanvasGroup, "alpha", 0f);
        Ui.SetValue(chatCanvasGroup, "interactable", false);
        Ui.SetValue(chatCanvasGroup, "blocksRaycasts", false);
        chatHidden = true;
    }

    private bool BindChatVisual()
    {
        if (chatVisual != null && chatCanvasGroup != null)
            return true;
        if (chatBindingRejected || gameplay == null)
            return false;
        try
        {
            MessageUI messageUi = (MessageUI)typeof(GameplayUI).GetField("MessageUI", BindingFlags.Instance | BindingFlags.Public)!.GetValue(gameplay)!;
            chatVisual = (GameObject)typeof(MessageUI).GetField("messageBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(messageUi)!;
            ChatBox chat = (ChatBox)typeof(MessageUI).GetField("chat", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(messageUi)!;
            chatInput = chat.gameObject;
            if (chatVisual == null || chatInput == null || chatVisual.GetComponent<MessageUI>() != null || chatVisual.GetComponent<ChatBox>() != null)
                throw new InvalidOperationException("chat_visual_not_pure");
            Type canvasGroupType = Type.GetType("UnityEngine.CanvasGroup, UnityEngine.UIModule", true)!;
            chatCanvasGroup = chatVisual.GetComponent(canvasGroupType);
            if (chatCanvasGroup == null)
            {
                chatCanvasGroup = chatVisual.AddComponent(canvasGroupType);
                chatGroupAdded = true;
            }
            log("state=map_chat_visual_bound visual=" + ScenePath(chatVisual.transform) + " input=" + ScenePath(chatInput.transform) +
                " input_inside_visual=" + chatInput.transform.IsChildOf(chatVisual.transform));
            return true;
        }
        catch (Exception e)
        {
            chatBindingRejected = true;
            chatVisual = chatInput = null;
            chatCanvasGroup = null;
            log("state=map_chat_visual_unavailable reason=" + e.GetType().Name + " detail=" + e.Message);
            return false;
        }
    }

    private void RestoreChatVisual(bool release)
    {
        if (chatHidden && chatCanvasGroup != null)
        {
            // Restore the captured CanvasGroup values rather than assuming the native chat defaults.
            Ui.SetValue(chatCanvasGroup, "alpha", chatAlpha);
            Ui.SetValue(chatCanvasGroup, "interactable", chatInteractable);
            Ui.SetValue(chatCanvasGroup, "blocksRaycasts", chatBlocksRaycasts);
            chatHidden = false;
        }
        if (!release)
            return;
        if (chatGroupAdded && chatCanvasGroup != null)
            UnityEngine.Object.Destroy(chatCanvasGroup);
        chatVisual = chatInput = null;
        chatCanvasGroup = null;
        chatGroupAdded = chatBindingRejected = false;
    }

    private bool EnsureUi()
    {
        if (root != null) return true; if (gameplay == null) return false;
        if (!Ui.CaptureTextStyle(entrySlot!, gameplay, log)) return false;
        if (!Ui.CapturePalette(entrySlot!, log, out hudGreen, out _)) return false;
        rowColor = new Color(0f, 0f, 0f, .55f);
        raisedColor = new Color(0f, 0f, 0f, .68f); buttonColor = new Color(0f, 0f, 0f, .55f);
        disabledBackground = new Color(0f, 0f, 0f, .30f);
        mutedGreen = new Color(hudGreen.r, hudGreen.g, hudGreen.b, hudGreen.a * .62f);
        disabledGreen = new Color(hudGreen.r, hudGreen.g, hudGreen.b, hudGreen.a * .32f);
        canvasObject = new GameObject("LW_MapCommandCanvas", typeof(RectTransform));
        canvasObject.transform.SetParent(Ui.GameplayCanvas(gameplay).transform, false);
        Stretch(canvasObject.GetComponent<RectTransform>());
        root = Ui.Image("LW_MapCommandRoot", canvasObject.transform, Color.clear, false).gameObject; Stretch(root.GetComponent<RectTransform>());
        Ui.Element overlays = Ui.Image("MapHighlights", root.transform, Color.clear, false); Stretch(overlays.rect);
        overlaysRoot = overlays.gameObject.transform;
        panel = Ui.Image("CommandPanel", root.transform, Color.clear, true); panelRect = panel.rect;
        if (!entrySlot!.ApplyTargetPanelStyle(panel, log)) { DestroyUi(); return false; }
        Ui.SetValue(panel.graphic, "raycastTarget", false);
        Ui.TextElement title = Ui.Text("WINGMAN CONTROL", panel.gameObject.transform, 15, hudGreen, "MiddleLeft"); SetRect(title.rect, 12, 5, 145, 22);
        targetSummary = Ui.Text("", panel.gameObject.transform, 11, hudGreen, "MiddleRight");
        seadTargetSummary = Ui.Text("", panel.gameObject.transform, 11, hudGreen, "MiddleRight");
        antiShipTargetSummary = Ui.Text("", panel.gameObject.transform, 11, hudGreen, "MiddleRight");
        candidateSummary = Ui.Text("", panel.gameObject.transform, 10, Amber, "MiddleRight");
        emptyStatus = Ui.Text("NO ACTIVE WINGMAN SESSION", panel.gameObject.transform, 14, hudGreen, "MiddleLeft");
        emptyPrompt = Ui.Text("RELEASE A WINGMAN TO BEGIN", panel.gameObject.transform, 11, mutedGreen, "MiddleLeft");
        homeRow = BuildRow("HomeRow", panel.gameObject.transform, SelectHome);
        flightLabel = Ui.Text("G1 FQS", panel.gameObject.transform, 12, mutedGreen, "MiddleLeft");
        flightCount = Ui.Text("0 / 4", panel.gameObject.transform, 12, mutedGreen, "MiddleRight");
        groupPrevious = Ui.Button("PreviousGroup", panel.gameObject.transform, "<", 20, buttonColor, hudGreen, PreviousGroup);
        groupLabel = Ui.Text("GROUP G1", panel.gameObject.transform, 12, hudGreen, "MiddleCenter");
        groupNext = Ui.Button("NextGroup", panel.gameObject.transform, ">", 20, buttonColor, hudGreen, NextGroup);
        for (int i = 0; i < 4; i++) { int slot = i; droneRows[i] = BuildRow("FQ" + (i + 1), panel.gameObject.transform, () => SelectSlot(slot)); }
        feedbackBox = Ui.Image("ImmediateFeedback", panel.gameObject.transform, raisedColor, true);
        feedback = Ui.Text("SELECT AN AIRCRAFT", feedbackBox.gameObject.transform, 12, mutedGreen, "MiddleLeft");
        commandBox = Ui.Image("CommandBar", panel.gameObject.transform, raisedColor, true);
        selectionSummary = Ui.Text("NO AIRCRAFT SELECTED", commandBox.gameObject.transform, 12, hudGreen, "MiddleLeft");
        directMissionDivider = Ui.Image("DirectMissionDivider", commandBox.gameObject.transform, mutedGreen, false);
        missionRecoveryDivider = Ui.Image("MissionRecoveryDivider", commandBox.gameObject.transform, mutedGreen, false);
        takeControl = Ui.Button("TakeControl", commandBox.gameObject.transform, "TAKE CONTROL", 26, buttonColor, hudGreen, TakeControl);
        auto = Ui.Button("Auto", commandBox.gameObject.transform, "AUTO", 26, buttonColor, hudGreen, () => SetMode(DroneModeOverride.Auto));
        follow = Ui.Button("Follow", commandBox.gameObject.transform, "FOLLOW", 26, buttonColor, hudGreen, () => SetMode(DroneModeOverride.Follow));
        loiter = Ui.Button("Loiter", commandBox.gameObject.transform, "LOITER", 26, buttonColor, hudGreen, ApplyLeaderLoiter);
        cruise = Ui.Button("Cruise", commandBox.gameObject.transform, "CRUISE", 26, buttonColor, hudGreen, CruiseToPoint);
        a2a = Ui.Button("A2A", commandBox.gameObject.transform, "A2A", 26, buttonColor, hudGreen, AssignA2A);
        sead = Ui.Button("Sead", commandBox.gameObject.transform, "SEAD", 26, buttonColor, hudGreen, AssignSead);
        asuw = Ui.Button("Asuw", commandBox.gameObject.transform, "ASUW", 26, buttonColor, hudGreen, AssignAntiShip);
        cas = Ui.Button("Cas", commandBox.gameObject.transform, "CAS", 26, buttonColor, hudGreen, AssignCas);
        strike = Ui.Button("Strike", commandBox.gameObject.transform, "STRIKE", 26, buttonColor, hudGreen, AssignStrike);
        cap = Ui.Button("Cap", commandBox.gameObject.transform, "CAP", 26, buttonColor, hudGreen, AssignCap);
        standDown = Ui.Button("StandDown", commandBox.gameObject.transform, "STAND DOWN", 26, buttonColor, hudGreen, StandDown);
        rtb = Ui.Button("Rtb", commandBox.gameObject.transform, "RTB", 26, buttonColor, hudGreen, ReturnToBase);
        clearTarget = Ui.Button("ClearTarget", commandBox.gameObject.transform, "CLEAR MISSION", 26, buttonColor, hudGreen, ClearMissionTarget);
        candidateOverlay = BuildOverlay("CandidateTarget", overlays.gameObject.transform, Amber);
        a2aTargetOverlay = BuildOverlay("A2ATarget", overlays.gameObject.transform, Amber);
        seadTargetOverlay = BuildOverlay("SeadTarget", overlays.gameObject.transform, Amber);
        antiShipTargetOverlay = BuildOverlay("AntiShipTarget", overlays.gameObject.transform, Amber);
        root!.SetActive(false); if (!entrySlot!.PrepareScreen(canvasObject, root)) { DestroyUi(); return false; }
        Layout(); return true;
    }

    private void Layout()
    {
        if (panelRect == null || panel == null || root == null || entrySlot == null) return;
        RectTransform rootRect = root.GetComponent<RectTransform>();
        Vector3[] corners = new Vector3[4]; entrySlot.rect.GetWorldCorners(corners);
        Vector3 entryLeftWorld = (corners[0] + corners[1]) * .5f;
        float scale = Ui.CanvasScale(gameplay!), centerX = rootRect.rect.center.x;
        float rootLeft = rootRect.rect.xMin - centerX, rootRight = rootRect.rect.xMax - centerX;
        float entryLeft = rootRect.InverseTransformPoint(entryLeftWorld).x - centerX;
        float horizontalInset = 8f / scale, verticalInset = 16f / scale;
        float right = Mathf.Min(entryLeft - 12f / scale, rootRight - horizontalInset);
        float availableWidth = Mathf.Max(1f, right - (rootLeft + horizontalInset));
        float availableHeight = Mathf.Max(1f, rootRect.rect.height - verticalInset * 2f);
        float h = Mathf.Min(hasSession ? 974f : 160f, availableHeight);
        float w = Mathf.Min(390f, availableWidth);
        h = Mathf.Min(availableHeight, Snap(h, scale)); w = Mathf.Min(availableWidth, Snap(w, scale));
        panelRect.anchorMin = panelRect.anchorMax = new Vector2(.5f, .5f); panelRect.pivot = new Vector2(1, .5f);
        panelRect.anchoredPosition = new Vector2(right, 0); panelRect.sizeDelta = new Vector2(w, h);
        if (!hasSession) { SetRect(emptyStatus!.rect, 14, 54, w - 28, 28); SetRect(emptyPrompt!.rect, 14, 92, w - 28, 22); return; }
        float targetW = Mathf.Max(1f, w - 157f);
        SetRect(targetSummary!.rect, 145, 4, targetW, 17); SetRect(seadTargetSummary!.rect, 145, 21, targetW, 17);
        SetRect(antiShipTargetSummary!.rect, 145, 38, targetW, 17); SetRect(candidateSummary!.rect, 145, 56, targetW, 17);
        float rowH = Screen.height >= 900 ? 82 : 72; LayoutRow(homeRow, 10, 82, w - 20, rowH);
        float pagerTop = 86 + rowH;
        SetRect(flightLabel!.rect, 12, pagerTop, 58, 22); LayoutButton(groupPrevious, 78, pagerTop, 24, 22);
        SetRect(groupLabel!.rect, 106, pagerTop, 82, 22); LayoutButton(groupNext, 192, pagerTop, 24, 22);
        SetRect(flightCount!.rect, 220, pagerTop, w - 232, 22);
        float commandH = 278, feedbackH = 38, commandTop = h - commandH - 10, feedbackTop = commandTop - feedbackH - 8, listTop = 112 + rowH;
        float listH = Mathf.Max(120, feedbackTop - listTop - 8), fqH = Mathf.Min(rowH, (listH - 15) / 4);
        for (int i = 0; i < 4; i++) LayoutRow(droneRows[i], 10, listTop + i * (fqH + 5), w - 20, fqH);
        SetRect(feedbackBox!.rect, 10, feedbackTop, w - 20, feedbackH); SetRect(feedback!.rect, 10, 0, w - 40, feedbackH);
        SetRect(commandBox!.rect, 10, commandTop, w - 20, commandH); SetRect(selectionSummary!.rect, 10, 4, w - 40, 24);
        LayoutDamage(scale);
        float buttonW = (w - 44) / 3f;
        LayoutButton(takeControl, 8, 120, buttonW, 26); LayoutButton(auto, 12 + buttonW, 120, buttonW, 26);
        LayoutButton(follow, 16 + buttonW * 2, 120, buttonW, 26);
        LayoutButton(loiter, 8, 148, buttonW, 26); LayoutButton(cruise, 12 + buttonW, 148, buttonW, 26);

        SetRect(directMissionDivider!.rect, 8, 178, w - 36, 1);
        LayoutButton(a2a, 8, 182, buttonW, 26); LayoutButton(sead, 12 + buttonW, 182, buttonW, 26);
        LayoutButton(asuw, 16 + buttonW * 2, 182, buttonW, 26);
        LayoutButton(cas, 8, 210, buttonW, 26); LayoutButton(strike, 12 + buttonW, 210, buttonW, 26);
        LayoutButton(cap, 16 + buttonW * 2, 210, buttonW, 26);

        SetRect(missionRecoveryDivider!.rect, 8, 240, w - 36, 1);
        LayoutButton(clearTarget, 8, 244, buttonW, 26); LayoutButton(standDown, 12 + buttonW, 244, buttonW, 26);
        LayoutButton(rtb, 16 + buttonW * 2, 244, buttonW, 26);
    }

    private void Refresh()
    {
        ObserveGroupSession();
        bool ready = plugin.MapGetRoster(roster, out home); hasSession = plugin.MapHasSession; SetSessionUi(hasSession);
        if (!hasSession)
        {
            selected = home = null; candidate = null; a2aTarget = null; seadTarget = null; antiShipTarget = null; a2aTargetsMultiple = seadTargetsMultiple = antiShipTargetsMultiple = false;
            selection.Clear(); ResetCasProjection(); ResetCapProjection(); ResetStrikeProjection(); ResetGroupState(false); DestroyDamageDisplay(); UpdateOverlays(); Layout(); return;
        }
        int activeTotal = ProjectGroups(); GameManager.GetLocalAircraft(out Aircraft local);
        selection.RemoveWhere(a => a == null || a.disabled || a != home &&
                                      (!roster.Contains(a) || !plugin.MapIsRegistered(a)));
        if (selected == null || !selection.Contains(selected)) selected = FirstSelection();
        UpdateAirToAirTarget(); UpdateSeadTarget(); UpdateAntiShipTarget(); UpdateCasProjection(); UpdateCapProjection(); UpdateStrikeProjection();
        targetSummary!.gameObject.SetActive(a2aTarget != null || a2aTargetsMultiple);
        targetSummary!.Value = a2aTargetsMultiple ? "A2A MULTIPLE" : a2aTarget == null ? "" : "A2A " + TargetName(a2aTarget);
        seadTargetSummary!.gameObject.SetActive(seadTarget != null || seadTargetsMultiple);
        seadTargetSummary!.Value = seadTargetsMultiple ? "SEAD MULTIPLE" : seadTarget == null ? "" : "SEAD  " + TargetName(seadTarget);
        antiShipTargetSummary!.gameObject.SetActive(antiShipTarget != null || antiShipTargetsMultiple);
        antiShipTargetSummary!.Value = antiShipTargetsMultiple ? "ASUW MULTIPLE" : antiShipTarget == null ? "" : "ASUW  " + TargetName(antiShipTarget);
        candidateSummary!.Value = hasPointCandidate ? "POINT CANDIDATE" : capProjectionMultiple ? "CAP MULTIPLE" : casProjectionMultiple ? "CAS MULTIPLE" :
                                  strikeProjectionMultiple ? "STRIKE MULTIPLE" : strikeProjectionAvailable ? "STRIKE " + strikeTargetCount :
                                  candidate != null ? CandidateText(candidate) : capProjectionAvailable ? "CAP AREA" : casProjectionAvailable ? "CAS AREA" : "";
        int pageActive = 0;
        for (int i = 0; i < activeSlots.Length; i++)
        {
            Aircraft? a = activeSlots[i]; LostEntry? lost = a == null ? LostForSlot(i) : null;
            if (a != null) pageActive++;
            WingmanMission mission = WingmanMission.None;
            if (a != null) plugin.MapTryGetMission(a, out mission);
            UpdateRow(droneRows[i]!, a ?? lost?.displayReference,
                      a != null ? FqLabel(a) : lost?.displayReference != null ? FqLabel(lost.displayReference) :
                      "G" + (currentGroupId + 1) + " FQ" + (i + 1), lost != null, mission);
        }
        UpdateRow(homeRow!, home, "HOME", false, WingmanMission.None); flightCount!.Value = "GROUP " + pageActive + "/4 · TOTAL " + activeTotal; UpdatePager(); UpdateCommands(ready, local);
        UpdateDamageDisplay(SingleSelection());
        if (Time.unscaledTime >= feedbackUntil && candidate == null) { feedback!.Value = ready ? DefaultHint(local) : "NO ACTIVE WINGMAN SESSION"; feedback.Color = mutedGreen; }
    }

    private int ProjectGroups()
    {
        Array.Clear(activeSlots, 0, activeSlots.Length);
        float now = Time.unscaledTime; int activeTotal = 0;
        HashSet<long> activeKeys = new HashSet<long>(); List<LostEntry> activeEntries = new List<LostEntry>();
        foreach (Aircraft a in roster)
        {
            if (a == null || a == home || a.disabled || !plugin.MapIsRegistered(a)) continue;
            activeTotal++;
            if (!plugin.MapTryGetGroup(a, out int groupId, out int groupSlot) || groupId < 0 || groupSlot < 0) continue;
            long key = GroupSlotKey(groupId, groupSlot);
            bool changed = !lostEntries.TryGetValue(key, out LostEntry entry) || entry.displayReference != a;
            if (entry == null) { entry = new LostEntry(); lostEntries[key] = entry; }
            entry.groupId = groupId; entry.groupSlot = groupSlot; entry.displayReference = a; entry.expiresAt = 0f;
            activeKeys.Add(key); activeEntries.Add(entry);
            if ((groupSlot < 0 || groupSlot >= activeSlots.Length) && changed)
                log("state=map_group_slot_invalid group=" + groupId + " slot=" + groupSlot);
        }
        List<long> remove = new List<long>();
        foreach (KeyValuePair<long, LostEntry> pair in lostEntries)
        {
            LostEntry entry = pair.Value;
            if (activeKeys.Contains(pair.Key)) continue;
            if (entry.expiresAt <= 0f) entry.expiresAt = now + 1.5f;
            if (now >= entry.expiresAt) remove.Add(pair.Key);
        }
        foreach (long key in remove) lostEntries.Remove(key);
        visibleGroupIds.Clear();
        foreach (LostEntry entry in lostEntries.Values)
            if (!visibleGroupIds.Contains(entry.groupId)) visibleGroupIds.Add(entry.groupId);
        visibleGroupIds.Sort();
        int projectedGroupId = visibleGroupIds.Count == 0 ? 0 :
                               visibleGroupIds.Contains(currentGroupId) ? currentGroupId : visibleGroupIds[0];
        if (currentGroupId != projectedGroupId)
        {
            currentGroupId = projectedGroupId;
            SaveGroupPage();
        }
        foreach (LostEntry entry in activeEntries)
            if (entry.groupId == currentGroupId && entry.groupSlot >= 0 && entry.groupSlot < activeSlots.Length && activeSlots[entry.groupSlot] == null)
                activeSlots[entry.groupSlot] = entry.displayReference;
        return activeTotal;
    }

    private static long GroupSlotKey(int groupId, int groupSlot) => ((long)groupId << 32) | (uint)groupSlot;

    private void UpdateRow(RowView v, Aircraft? a, string label, bool lost, WingmanMission mission)
    {
        v.aircraft = lost ? null : a; v.button.Interactable = a != null && !lost; v.name.Value = label; v.type.Value = a == null ? "NOT DEPLOYED" : TypeOf(a);
        v.status.Value = lost ? "LOST" : a == null ? "EMPTY" : StateOf(a, mission);
        Ui.Color(v.background, lost ? DarkRed : rowColor);
        bool rowSelected = a != null && !lost && selection.Contains(a);
        Ui.Color(v.selection, rowSelected ? hudGreen : Color.clear); Ui.Color(v.assignment, mission != WingmanMission.None ? Amber : Color.clear);
        if (a == null) { v.metrics.Value = "HP -- · FUEL --"; v.ammo.Value = "WEAPONS --"; v.threat.Value = ""; return; }
        int[] ammo = Ammo(a);
        v.metrics.Value = "HP " + Percent(Health(a)) + " · FUEL " + Percent(Fuel(a));
        v.ammo.Value = MapAmmoLogic.Format(ammo); int threats = Threats(a);
        v.threat.Value = threats > 0 ? "IN " + threats.ToString("00") : ""; v.threat.Color = threats > 0 ? Red : mutedGreen;
    }

    private void UpdateCommands(bool ready, Aircraft? local)
    {
        List<Aircraft> selectedDrones = SelectedDrones(), drones = SelectedCommandDrones(); Aircraft? single = SingleSelection();
        selectionSummary!.Value = SelectionSummary(selectedDrones);
        bool remoteModeOnly = plugin.MapModeOnlyRemoteSession;
        bool singleSelection = single != null, targetIsCurrent = single != null && single == local;
        bool validFq = single != null && single != home && IsSelectableFq(single, local);
        bool takeControlFq = TakeControlAdmissionLogic.CanTakeControl(singleSelection, validFq, targetIsCurrent,
                                                                      remoteModeOnly);
        bool takeControlHome = singleSelection && single == home && !targetIsCurrent && !remoteModeOnly &&
                               !plugin.MapIsReturningToBase(single!);
        if (remoteModeOnly)
        {
            SetEnabled(takeControl!, false);
            SetEnabled(auto!, ready && drones.Count > 0); SetEnabled(follow!, ready && drones.Count > 0); SetEnabled(loiter!, ready && drones.Count > 0);
            SetEnabled(cruise!, false); SetEnabled(a2a!, false); SetEnabled(sead!, false); SetEnabled(asuw!, false); SetEnabled(cas!, false); SetEnabled(strike!, false); SetEnabled(cap!, false);
            SetEnabled(standDown!, false); SetEnabled(rtb!, false); SetEnabled(clearTarget!, false);
        }
        else
        {
            SetEnabled(takeControl!, ready && (takeControlFq || takeControlHome));
            SetEnabled(auto!, ready && drones.Count > 0); SetEnabled(follow!, ready && drones.Count > 0); SetEnabled(loiter!, ready && drones.Count > 0);
            SetEnabled(cruise!, ready && drones.Count > 0 && hasPointCandidate);
            SetEnabled(a2a!, ready && plugin.MapCombatEnabled && drones.Count > 0);
            SetEnabled(sead!, ready && plugin.MapCombatEnabled && drones.Count > 0);
            SetEnabled(asuw!, ready && plugin.MapCombatEnabled && drones.Count > 0);
            SetEnabled(cas!, ready && plugin.MapCombatEnabled && drones.Count > 0 && hasPointCandidate);
            SetEnabled(strike!, ready && plugin.MapCombatEnabled && drones.Count > 0 && CaptureStrikeTargets().Count > 0);
            SetEnabled(cap!, ready && plugin.MapCombatEnabled && drones.Count > 0 && hasPointCandidate);
            SetEnabled(standDown!, ready && (selectedDrones.Count > 0 || home != null && selection.Contains(home) && plugin.MapIsReturningToBase(home)));
            SetEnabled(rtb!, ready && SelectedRtbAircraft().Count > 0);
            SetEnabled(clearTarget!, ready && TryGetCommonMission(drones, out _));
        }
    }

    private string DefaultHint(Aircraft? local)
    {
        int count = SelectionCount(); if (count == 0) return "SELECT AN AIRCRAFT";
        if (SelectedRtbAircraft().TrueForAll(plugin.MapIsReturningToBase)) return "RETURNING TO BASE";
        if (count == 1 && SingleSelection() == local) return "AIRCRAFT UNDER PLAYER CONTROL";
        if (selected == home) return "HOME SELECTED";
        return candidate != null ? "TARGET CANDIDATE · CHOOSE MISSION" : "COMMAND READY";
    }

    private void PollMapCandidate()
    {
        if (pendingCandidateFrame >= 0 && Time.frameCount >= pendingCandidateFrame) { pendingCandidateFrame = -1; ReadCandidate(); nextRefresh = 0f; }
        DynamicMap? current = map;
        if (current != null && Ui.MouseButtonUp(0) && Time.frameCount != pointCandidateFrame && current.IsCursorInMapRectangle() && panelRect != null &&
           !Ui.Contains(panelRect, Ui.MousePosition))
        {
            pendingCandidateFrame = Time.frameCount + 1;
            nextRefresh = 0f;
        }
    }

    private void ReadCandidate()
    {
        try
        {
            ObserveGroupSession();
            Unit? newest = null; Aircraft? primary = null; int primaryGroupId = 0; uint managedSessionId = 0;
            HashSet<Aircraft> managed = new HashSet<Aircraft>();
            if (GameManager.GetLocalAircraft(out Aircraft local) && !local.disabled && local.weaponManager != null)
            {
                List<Unit> targets = local.weaponManager.GetTargetList();
                if (targets.Count > 0) newest = targets[0];
                for (int i = 0; i < targets.Count; i++)
                    AddNativeManaged(targets[i], managed, ref primary, ref primaryGroupId, ref managedSessionId);
            }
            else if (map != null)
            {
                for (int i = map.selectedIcons.Count - 1; i >= 0; i--)
                {
                    if (map.selectedIcons[i] is not UnitMapIcon icon || icon == null || !icon.gameObject.activeInHierarchy) continue;
                    Unit? unit = icon.unit;
                    if (unit == null || unit.disabled || !unit.gameObject.activeInHierarchy) continue;
                    newest ??= unit;
                    AddNativeManaged(unit, managed, ref primary, ref primaryGroupId, ref managedSessionId);
                }
            }
            bool selectionChanged = SyncNativeManagedSelection(managed, primary, primaryGroupId, managedSessionId);
            bool newestIsManaged = newest is Aircraft newestAircraft && managed.Contains(newestAircraft);
            if (!newestIsManaged)
            {
                candidate = newest;
                if (candidate != null) { hasPointCandidate = false; DestroyPointMarker(); }
            }
            if (selectionChanged) Refresh();
        }
        catch { }
    }

    private void AddNativeManaged(Unit? unit, HashSet<Aircraft> managed, ref Aircraft? primary, ref int primaryGroupId,
                                  ref uint managedSessionId)
    {
        if (unit is not Aircraft aircraft || !plugin.MapTryGetManagedFq(aircraft, out uint sessionId, out int groupId) ||
            !managed.Add(aircraft)) return;
        if (primary == null) { primary = aircraft; primaryGroupId = groupId; managedSessionId = sessionId; }
    }

    private bool SyncNativeManagedSelection(HashSet<Aircraft> managed, Aircraft? primary, int primaryGroupId, uint managedSessionId)
    {
        bool previousNonempty = hasObservedNativeManagedSelection && observedNativeManagedSelection.Count > 0;
        bool changed = !hasObservedNativeManagedSelection || !observedNativeManagedSelection.SetEquals(managed) ||
                       !ReferenceEquals(observedNativeManagedPrimary, primary);
        if (!changed) return false;
        observedNativeManagedSelection.Clear();
        foreach (Aircraft aircraft in managed) observedNativeManagedSelection.Add(aircraft);
        observedNativeManagedPrimary = primary; hasObservedNativeManagedSelection = true;
        if (managed.Count == 0)
        {
            if (!previousNonempty) return false;
            selection.Clear(); selected = null; return true;
        }
        if (primary == null || managedSessionId == 0) return false;
        if (observedSessionId != managedSessionId) ObserveGroupSession();
        selection.Clear();
        foreach (Aircraft aircraft in managed) selection.Add(aircraft);
        selected = primary; currentGroupId = primaryGroupId; SaveGroupPage(); nextRefresh = 0f;
        return true;
    }

    private void ResetNativeManagedObservation()
    {
        observedNativeManagedSelection.Clear(); observedNativeManagedPrimary = null; hasObservedNativeManagedSelection = false;
    }

    private List<Unit> CaptureStrikeTargets()
    {
        if (GameManager.GetLocalAircraft(out Aircraft local) && !local.disabled && local.weaponManager != null)
            return new List<Unit>(local.weaponManager.GetTargetList());
        List<Unit> targets = new List<Unit>();
        if (map == null) return targets;
        for (int i = 0; i < map.selectedIcons.Count; i++)
            if (map.selectedIcons[i] is UnitMapIcon icon && icon != null && icon.gameObject.activeInHierarchy && icon.unit != null)
                targets.Add(icon.unit);
        return targets;
    }

    private void PreviousGroup() => NavigateGroup(-1);
    private void NextGroup() => NavigateGroup(1);
    private void NavigateGroup(int direction)
    {
        int index = visibleGroupIds.IndexOf(currentGroupId), next = index + direction;
        if (index < 0 || next < 0 || next >= visibleGroupIds.Count) return;
        currentGroupId = visibleGroupIds[next]; SaveGroupPage(); Refresh();
    }

    private void ObserveGroupSession()
    {
        if (!plugin.MapTryGetGroupPage(out uint sessionId, out int savedGroupId, out bool local))
        {
            if (observedSessionId != 0) { selection.Clear(); selected = null; }
            if (observedSessionId != 0 || hasObservedNativeManagedSelection) ResetNativeManagedObservation();
            observedSessionId = 0; observedSessionLocal = false; currentGroupId = 0;
            return;
        }
        if (sessionId == observedSessionId && local == observedSessionLocal) return;
        ResetNativeManagedObservation();
        selection.Clear(); selected = null; observedSessionId = sessionId; observedSessionLocal = local;
        currentGroupId = savedGroupId;
    }
    private void SaveGroupPage()
    {
        if (observedSessionLocal && observedSessionId != 0)
            plugin.MapSetGroupPage(observedSessionId, currentGroupId);
    }
    private void UpdatePager()
    {
        int index = visibleGroupIds.IndexOf(currentGroupId);
        flightLabel!.Value = "G" + (currentGroupId + 1) + " FQS";
        groupLabel!.Value = "G" + (currentGroupId + 1) + " · " + (index + 1) + "/" + visibleGroupIds.Count;
        SetEnabled(groupPrevious!, index > 0); SetEnabled(groupNext!, index >= 0 && index < visibleGroupIds.Count - 1);
    }
    private void ClearDroneSelection()
    {
        bool keepHome = home != null && selection.Contains(home);
        selection.Clear();
        if (keepHome) selection.Add(home!);
        selected = keepHome ? home : null;
    }
    private void ResetGroupState(bool clearSelection)
    {
        Array.Clear(activeSlots, 0, activeSlots.Length); lostEntries.Clear(); visibleGroupIds.Clear(); currentGroupId = 0;
        if (clearSelection) { selection.Clear(); selected = null; }
        if (groupLabel != null) groupLabel.Value = "GROUP G1";
    }
    // Selection is roster-wide; paging only changes the four rows being shown.
    private bool IsOnCurrentPage(Aircraft aircraft) => roster.Contains(aircraft);
    private LostEntry? LostForSlot(int groupSlot)
    {
        LostEntry? result = null; float now = Time.unscaledTime;
        foreach (LostEntry entry in lostEntries.Values)
            if (entry.groupId == currentGroupId && entry.groupSlot == groupSlot && entry.expiresAt > now)
                result = entry;
        return result;
    }

    private void SelectHome()
    {
        if (home == null) return;
        if (!Ui.ControlHeld) { selection.Clear(); selection.Add(home); selected = home; }
        else if (!selection.Add(home)) { selection.Remove(home); if (selected == home) selected = FirstSelection(); }
        else selected = home;
        Refresh();
    }
    private void SelectSlot(int i)
    {
        Aircraft? drone = activeSlots[i]; if (drone == null) return;
        if (!Ui.ControlHeld) { selection.Clear(); selection.Add(drone); selected = drone; }
        else if (!selection.Add(drone)) { selection.Remove(drone); if (selected == drone) selected = FirstSelection(); }
        else selected = drone;
        Refresh();
    }
    private void SelectAll()
    {
        selection.Clear(); GameManager.GetLocalAircraft(out Aircraft local);
        foreach (Aircraft drone in roster) if (IsSelectableFq(drone, local)) selection.Add(drone);
        selected = FirstSelection(); Refresh();
    }
    private void TakeControl()
    {
        Aircraft? target = SingleSelection(); if (target == null) return;
        if (plugin.MapTakeControl(target, out string reason)) ShowFeedback("TAKING CONTROL OF " + FqLabel(target), hudGreen); else ShowFeedback(Reason(reason), Amber);
    }
    private void SetMode(DroneModeOverride mode)
    {
        List<Aircraft> drones = SelectedCommandDrones(), failures = new List<Aircraft>(); List<string> reasons = new List<string>();
        int installed = 0, sent = 0;
        foreach (Aircraft drone in drones)
            if (plugin.MapSetMode(drone, mode, out string reason))
            {
                if (reason == "sent") sent++;
                else installed++;
            }
            else { failures.Add(drone); reasons.Add(reason); }
        string action = mode.ToString().ToUpperInvariant() + (sent > 0 ? " REQUEST SENT" : "");
        ShowBatchFeedback(action, installed + sent, drones.Count, failures, reasons,
                          sent > 0 ? Amber : installed > 0 ? hudGreen : Amber);
    }
    private void CruiseToPoint()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0 || !hasPointCandidate) return;
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapCruiseToPoint(drone, pointCandidate, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) hasPointCandidate = false;
        ShowBatchFeedback("CRUISE", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); UpdatePointMarker();
    }
    private void ApplyLeaderLoiter()
    {
        if (plugin.MapModeOnlyRemoteSession) { SetMode(DroneModeOverride.Loiter); return; }
        List<Aircraft> drones = SelectedCommandDrones(), failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapClearLoiterHere(drone, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        ShowBatchFeedback("LOITER", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); UpdatePointMarker();
    }
    private void AssignA2A()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0) return;
        if (candidate is not Aircraft target) { ShowFeedback("SELECT A HOSTILE AIRCRAFT", Amber); return; }
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignAirToAir(drone, target, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) candidate = null;
        ShowBatchFeedback("A2A", success, drones.Count, failures, reasons, Amber, true);
    }
    private void AssignSead()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0) return;
        if (candidate == null) { ShowFeedback("SELECT A HOSTILE SAM", Amber); return; }
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignSead(drone, candidate, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) candidate = null;
        ShowBatchFeedback("SEAD", success, drones.Count, failures, reasons, Amber, true);
    }
    private void AssignAntiShip()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0) return;
        if (candidate == null) ReadCandidate();
        if (candidate is not Ship ship) { ShowFeedback("SELECT A HOSTILE SURFACE SHIP", Amber); return; }
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignAntiShip(drone, ship, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) candidate = null;
        ShowBatchFeedback("ASUW", success, drones.Count, failures, reasons, Amber, true);
    }
    private void AssignCas()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0 || !hasPointCandidate) return;
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignCas(drone, pointCandidate, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) hasPointCandidate = false;
        ShowBatchFeedback("CAS", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber, true); Refresh(); UpdatePointMarker();
    }
    private void AssignCap()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0 || !hasPointCandidate) return;
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignCap(drone, pointCandidate, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        if (success > 0) hasPointCandidate = false;
        ShowBatchFeedback("CAP", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber, true); Refresh(); UpdatePointMarker();
    }
    private void AssignStrike()
    {
        List<Aircraft> drones = SelectedCommandDrones(); if (drones.Count == 0) return;
        List<Unit> targets = CaptureStrikeTargets(); if (targets.Count == 0) return;
        List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
        foreach (Aircraft drone in drones) if (plugin.MapAssignStrike(drone, targets, out string reason)) success++; else { failures.Add(drone); reasons.Add(reason); }
        ShowBatchFeedback("STRIKE", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber, true); Refresh();
    }
    private void StandDown()
    {
        List<Aircraft> drones = SelectedDrones(), failures = new List<Aircraft>(); List<string> reasons = new List<string>();
        int success = 0, cancelSuccess = 0, total = 0;
        foreach (Aircraft drone in drones)
        {
            total++;
            if (plugin.MapIsReturningToBase(drone))
            {
                if (plugin.MapCancelReturnToBase(drone, out string reason)) { success++; cancelSuccess++; }
                else { failures.Add(drone); reasons.Add(reason); }
            }
            else if (plugin.MapStandDown(drone, out string reason)) success++;
            else { failures.Add(drone); reasons.Add(reason); }
        }
        if (home != null && selection.Contains(home) && plugin.MapIsReturningToBase(home))
        {
            total++;
            if (plugin.MapCancelReturnToBase(home, out string reason)) { success++; cancelSuccess++; }
            else { failures.Add(home); reasons.Add(reason); }
        }
        plugin.MapReportRtbCancelled(cancelSuccess);
        ShowBatchFeedback("STAND DOWN", success, total, failures, reasons, success > 0 ? hudGreen : Amber);
        Refresh();
    }
    private void ReturnToBase()
    {
        List<Aircraft> aircraft = SelectedRtbAircraft(), failures = new List<Aircraft>();
        List<string> reasons = new List<string>(); int success = 0;
        // FQ handoffs are synchronous state changes; route Home last so the backend can see this batch returning.
        foreach (Aircraft target in aircraft)
            if (target != home)
            {
                if (plugin.MapTryReturnToBase(target, out string reason)) success++;
                else { failures.Add(target); reasons.Add(reason); }
            }
        if (home != null && aircraft.Contains(home))
        {
            if (plugin.MapTryReturnToBase(home, out string reason)) success++;
            else { failures.Add(home); reasons.Add(reason); }
        }
        plugin.MapReportRtbOrdered(success);
        ShowBatchFeedback("RTB", success, aircraft.Count, failures, reasons, success > 0 ? hudGreen : Amber);
        Refresh();
    }
    private void ClearMissionTarget()
    {
        List<Aircraft> drones = SelectedCommandDrones();
        if (!TryGetCommonMission(drones, out WingmanMission mission)) { ShowFeedback("SELECT ONE SHARED MISSION", Amber); return; }
        if (mission == WingmanMission.AirToAir)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearAirToAir(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR A2A", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); return;
        }
        if (mission == WingmanMission.Sead)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearSead(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR SEAD", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); return;
        }
        if (mission == WingmanMission.AntiShip)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearAntiShip(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR ASUW", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); return;
        }
        if (mission == WingmanMission.Cas)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearCas(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR CAS", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); UpdatePointMarker(); return;
        }
        if (mission == WingmanMission.Cap)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearCap(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR CAP", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); UpdatePointMarker(); return;
        }
        if (mission == WingmanMission.Strike)
        {
            List<Aircraft> failures = new List<Aircraft>(); List<string> reasons = new List<string>(); int success = 0;
            foreach (Aircraft drone in drones) if (plugin.MapClearStrike(drone, out string clearReason)) success++; else { failures.Add(drone); reasons.Add(clearReason); }
            ShowBatchFeedback("CLEAR STRIKE", success, drones.Count, failures, reasons, success > 0 ? hudGreen : Amber); Refresh(); return;
        }
        ShowFeedback("SELECT ONE SHARED MISSION", Amber);
    }

    private void UpdateAirToAirTarget()
    {
        a2aTarget = null; a2aTargetsMultiple = false;
        List<string?> ids = new List<string?>(); Aircraft? projectedTarget = null;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.AirToAir) continue;
            if (!plugin.MapTryGetAirToAirTarget(drone, out Aircraft target) || target == null)
            {
                ids.Add(null);
                continue;
            }
            ids.Add(target.persistentID.ToString()); projectedTarget ??= target;
        }
        if (ids.Count == 0) return;
        if (CombatCommandLogic.IsSingleTargetProjection(ids, out string? commonId) && commonId != null &&
            projectedTarget != null)
            a2aTarget = projectedTarget;
        else
            a2aTargetsMultiple = true;
    }

    private void UpdateSeadTarget()
    {
        seadTarget = null; seadTargetsMultiple = false; bool found = false;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.Sead) continue;
            if (!plugin.MapTryGetSeadTarget(drone, out Unit target) || target == null)
            { seadTarget = null; return; }
            if (!found) { seadTarget = target; found = true; }
            else if (seadTarget != target) { seadTarget = null; seadTargetsMultiple = true; return; }
        }
    }

    private void UpdateAntiShipTarget()
    {
        antiShipTarget = null; antiShipTargetsMultiple = false; bool found = false;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.AntiShip) continue;
            if (!plugin.MapTryGetAntiShipTarget(drone, out Ship target) || target == null)
            { antiShipTarget = null; return; }
            if (!found) { antiShipTarget = target; found = true; }
            else if (antiShipTarget != target) { antiShipTarget = null; antiShipTargetsMultiple = true; return; }
        }
    }

    private void UpdateCasProjection()
    {
        ResetCasProjection(); bool found = false;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.Cas) continue;
            casProjectionSelected = true;
            if (!plugin.MapTryGetCasCenter(drone, out GlobalPosition center)) return;
            if (!found) { casCenter = center; found = true; }
            else if (!casCenter.Equals(center)) { casProjectionMultiple = true; return; }
        }
        casProjectionAvailable = found;
    }
    private void ResetCasProjection()
    {
        casProjectionSelected = casProjectionAvailable = casProjectionMultiple = false; casCenter = default;
    }

    private void UpdateCapProjection()
    {
        ResetCapProjection(); bool found = false;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.Cap) continue;
            capProjectionSelected = true;
            if (!plugin.MapTryGetCapAnchor(drone, out GlobalPosition anchor, out _)) return;
            if (!found) { capAnchor = anchor; found = true; }
            else if (!capAnchor.Equals(anchor)) { capProjectionMultiple = true; return; }
        }
        capProjectionAvailable = found;
    }
    private void ResetCapProjection()
    {
        capProjectionSelected = capProjectionAvailable = capProjectionMultiple = false; capAnchor = default;
    }

    private void UpdateStrikeProjection()
    {
        ResetStrikeProjection(); PersistentID[]? commonTargets = null;
        foreach (Aircraft drone in SelectedDrones())
        {
            if (SnapshotMission(drone) != WingmanMission.Strike) continue;
            if (!plugin.MapTryGetStrikeTargetSet(drone, out PersistentID[] targets)) return;
            if (commonTargets == null) commonTargets = targets;
            else
            {
                if (commonTargets.Length != targets.Length) { strikeProjectionMultiple = true; return; }
                for (int i = 0; i < commonTargets.Length; i++)
                    if (commonTargets[i].Id != targets[i].Id) { strikeProjectionMultiple = true; return; }
            }
        }
        if (commonTargets != null) { strikeProjectionAvailable = true; strikeTargetCount = commonTargets.Length; }
    }
    private void ResetStrikeProjection()
    {
        strikeProjectionAvailable = strikeProjectionMultiple = false; strikeTargetCount = 0;
    }

    private Aircraft? FirstSelection()
    {
        if (home != null && selection.Contains(home)) return home;
        foreach (Aircraft drone in roster) if (drone != null && drone != home && selection.Contains(drone)) return drone;
        return null;
    }
    private Aircraft? SingleSelection()
    {
        return selection.Count == 1 ? FirstSelection() : null;
    }
    private int SelectionCount() => selection.Count;
    private List<Aircraft> SelectedDrones()
    {
        List<Aircraft> result = new List<Aircraft>();
        foreach (Aircraft drone in roster)
            if (drone != null && drone != home && !drone.disabled && plugin.MapIsRegistered(drone) && selection.Contains(drone))
                result.Add(drone);
        return result;
    }
    private List<Aircraft> SelectedRtbAircraft()
    {
        List<Aircraft> result = SelectedDrones();
        if (home != null && !home.disabled && selection.Contains(home)) result.Add(home);
        return result;
    }
    private List<Aircraft> SelectedCommandDrones()
    {
        List<Aircraft> result = SelectedDrones();
        result.RemoveAll(plugin.MapIsReturningToBase);
        if (plugin.MapModeOnlyRemoteSession) result.RemoveAll(drone => !plugin.MapModeKnown(drone));
        return result;
    }
    private bool IsSelectableFq(Aircraft? drone, Aircraft? local) => drone != null && !drone.disabled && drone != local && roster.Contains(drone) && plugin.MapIsRegistered(drone);
    private bool TryGetCommonMission(List<Aircraft> drones, out WingmanMission mission)
    {
        List<WingmanMission> values = new List<WingmanMission>();
        for (int i = 0; i < drones.Count; i++)
        {
            if (!plugin.MapTryGetMission(drones[i], out WingmanMission value))
            {
                mission = WingmanMission.None;
                return false;
            }
            values.Add(value);
        }
        return CombatCommandLogic.TryGetCommonMission(values, out mission);
    }
    private string SelectionSummary(List<Aircraft> drones)
    {
        bool homeSelected = home != null && selection.Contains(home);
        if (homeSelected && drones.Count == 0) return "HOME SELECTED · " + StateOf(home!, WingmanMission.None);
        if (drones.Count == 0) return "NO AIRCRAFT SELECTED";
        if (!homeSelected && drones.Count == 1) return FqLabel(drones[0]) + " SELECTED · " + StateOf(drones[0], SnapshotMission(drones[0]));
        int returning = 0;
        foreach (Aircraft aircraft in selection) if (plugin.MapIsReturningToBase(aircraft)) returning++;
        if (returning == selection.Count) return selection.Count + " AIRCRAFT SELECTED · RETURNING";
        string returningText = returning > 0 ? " · " + returning + " RTB" : "";
        DroneModeOverride firstMode = DroneModeOverride.Auto; WingmanMission firstMission = WingmanMission.None;
        bool haveFirst = false, mixedMode = false, mixedMission = false; int statusCount = 0;
        foreach (Aircraft drone in drones)
        {
            if (plugin.MapModeOnlyRemoteSession && !plugin.MapModeKnown(drone)) continue;
            if (!plugin.MapTryGetStatus(drone, out _, out DroneModeOverride mode, out _, out _, out _)) continue;
            WingmanMission mission = SnapshotMission(drone);
            statusCount++;
            if (!haveFirst) { firstMode = mode; firstMission = mission; haveFirst = true; }
            else { mixedMode |= mode != firstMode; mixedMission |= mission != firstMission; }
        }
        string stateText = mixedMission || statusCount != drones.Count ? "MIXED" : firstMission != WingmanMission.None
            ? MissionText(firstMission) : mixedMode ? "MIXED" : firstMode.ToString().ToUpperInvariant();
        TryGetConfirmedPoint(out _, out bool mixedHere);
        string countText = homeSelected ? selection.Count + " AIRCRAFT SELECTED" : drones.Count + " FQ SELECTED";
        return countText + " · " + stateText + returningText + (mixedHere ? " · MIXED HERE" : "");
    }
    private void ShowBatchFeedback(string action, int success, int total, List<Aircraft> failures, List<string> reasons, Color color, bool aggregateReasons = false)
    {
        string value = action + " " + success + "/" + total;
        if (failures.Count > 0)
        {
            List<string> details = new List<string>();
            if (aggregateReasons)
            {
                List<string> orderedReasons = new List<string>();
                Dictionary<string, int> counts = new Dictionary<string, int>();
                Dictionary<string, Aircraft> firstFailures = new Dictionary<string, Aircraft>();
                for (int i = 0; i < failures.Count; i++)
                    if (counts.TryGetValue(reasons[i], out int count)) counts[reasons[i]] = count + 1;
                    else { orderedReasons.Add(reasons[i]); counts.Add(reasons[i], 1); firstFailures.Add(reasons[i], failures[i]); }
                foreach (string reason in orderedReasons)
                    details.Add(counts[reason] > 1 ? counts[reason] + "× " + BatchReason(reason) :
                                AircraftLabel(firstFailures[reason]) + " " + BatchReason(reason));
            }
            else
                for (int i = 0; i < failures.Count; i++) details.Add(AircraftLabel(failures[i]) + " " + BatchReason(reasons[i]));
            value += " · " + string.Join(", ", details);
        }
        ShowFeedback(value, color);
    }
    private string FqLabel(Aircraft drone)
    {
        return plugin.MapTryGetFqPresentationLabel(drone, out string label) ? label : ShortName(drone);
    }
    private string AircraftLabel(Aircraft aircraft) => aircraft == home ? "HOME" : FqLabel(aircraft);
    private static string BatchReason(string reason) => reason == "rtb_in_progress" ? "ALREADY RETURNING" : reason.Replace('_', ' ').ToUpperInvariant();

    private bool TryGetConfirmedPoint(out GlobalPosition point, out bool mixed)
    {
        point = default; mixed = false; List<Aircraft> drones = SelectedDrones(); int found = 0;
        foreach (Aircraft drone in drones) if (plugin.MapTryGetLoiterHere(drone, out GlobalPosition value))
            {
                if (found == 0) point = value; else if (!point.Equals(value)) mixed = true;
                found++;
            }
        if (found > 0 && found != drones.Count) mixed = true;
        return found == drones.Count && found > 0 && !mixed;
    }
    private void UpdatePointMarker()
    {
        if (!visible || map == null || !DynamicMap.mapMaximized) { DestroyPointMarker(); return; }
        GlobalPosition point;
        if (hasPointCandidate) point = pointCandidate;
        else if (capProjectionSelected)
        {
            if (!capProjectionAvailable || capProjectionMultiple) { DestroyPointMarker(); return; }
            point = capAnchor;
        }
        else if (casProjectionSelected)
        {
            if (!casProjectionAvailable || casProjectionMultiple) { DestroyPointMarker(); return; }
            point = casCenter;
        }
        else if (!TryGetConfirmedPoint(out point, out _)) { DestroyPointMarker(); return; }
        if (pointMarker == null)
        {
            pointMarker = UnityEngine.Object.Instantiate(map.mapWaypoint, map.iconLayer.transform);
            pointVector = UnityEngine.Object.Instantiate(map.mapWaypointVector, map.iconLayer.transform); pointVector.SetActive(false);
            Type graphicType = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI", true)!;
            foreach (object graphic in pointMarker.GetComponentsInChildren(graphicType, true)) Ui.SetValue(graphic, "raycastTarget", false);
        }
        Vector3 global = point.AsVector3() * map.mapDisplayFactor;
        pointMarker.transform.localPosition = new Vector3(global.x, global.z, 0f);
        Vector3 local = pointMarker.transform.localPosition, world = pointMarker.transform.position;
        if (pointWaypoint == null) pointWaypoint = new MapWaypoint(world, local, pointMarker, pointVector!);
        else { pointWaypoint.waypointPosition = world; pointWaypoint.previousWaypoint = local; pointWaypoint.PlaceMarker(); }
        pointVector!.SetActive(false);
    }
    private void DestroyPointMarker()
    {
        if (pointMarker != null) UnityEngine.Object.Destroy(pointMarker);
        if (pointVector != null) UnityEngine.Object.Destroy(pointVector);
        pointMarker = pointVector = null; pointWaypoint = null;
    }

    private void UpdateOverlays()
    {
        if (overlaysRoot == null) return;
        List<Aircraft> selectedDrones = SelectedDrones();
        int selectedCount = selectedDrones.Count + (home != null && selection.Contains(home) ? 1 : 0);
        while (selectedOverlays.Count < selectedCount)
            selectedOverlays.Add(BuildOverlay("SelectedAircraft" + selectedOverlays.Count, overlaysRoot, hudGreen));

        int index = 0;
        if (home != null && selection.Contains(home)) PositionOverlay(selectedOverlays[index++], home, "", hudGreen, 0f);
        foreach (Aircraft drone in selectedDrones) PositionOverlay(selectedOverlays[index++], drone, "", hudGreen, 0f);
        while (index < selectedOverlays.Count) PositionOverlay(selectedOverlays[index++], null, "", hudGreen, 0f);
        float candidateOffset = OverlayOffset(candidate, 0, candidate, a2aTarget, seadTarget, antiShipTarget);
        float a2aOffset = OverlayOffset(a2aTarget, 1, candidate, a2aTarget, seadTarget, antiShipTarget);
        float seadOffset = OverlayOffset(seadTarget, 2, candidate, a2aTarget, seadTarget, antiShipTarget);
        float asuwOffset = OverlayOffset(antiShipTarget, 3, candidate, a2aTarget, seadTarget, antiShipTarget);
        PositionOverlay(candidateOverlay, candidate, candidate == null ? "" : CandidateKind(candidate), Amber, candidateOffset);
        PositionOverlay(a2aTargetOverlay, a2aTarget, "A2A TARGET", Amber, a2aOffset);
        PositionOverlay(seadTargetOverlay, seadTarget, "SEAD TARGET", Amber, seadOffset);
        PositionOverlay(antiShipTargetOverlay, antiShipTarget, "ASUW TARGET", Amber, asuwOffset);
    }
    private static float OverlayOffset(Unit? unit, int order, Unit? candidateUnit, Unit? a2aUnit, Unit? seadUnit, Unit? asuwUnit)
    {
        if (unit == null) return 0f;
        int count = 0, index = 0;
        if (candidateUnit == unit) { count++; if (order > 0) index++; }
        if (a2aUnit == unit) { count++; if (order > 1) index++; }
        if (seadUnit == unit) { count++; if (order > 2) index++; }
        if (asuwUnit == unit) count++;
        return count > 1 ? (index - (count - 1) * .5f) * (count == 2 ? 16f : 14f) : 0f;
    }
    private void HideSelectedOverlays()
    {
        foreach (OverlayView overlay in selectedOverlays) overlay.root.gameObject.SetActive(false);
    }
    private void PositionOverlay(OverlayView? view, Unit? unit, string label, Color color, float verticalOffset)
    {
        if (view == null) return;
        if (unit == null || !DynamicMap.TryGetMapIcon(unit, out UnitMapIcon icon) || icon == null || icon.transform is not RectTransform iconRect)
        { view.root.gameObject.SetActive(false); return; }
        Vector3[] corners = new Vector3[4]; iconRect.GetWorldCorners(corners);
        float minX = corners[0].x, maxX = corners[0].x, minY = corners[0].y, maxY = corners[0].y;
        for (int i = 1; i < 4; i++) { minX = Mathf.Min(minX, corners[i].x); maxX = Mathf.Max(maxX, corners[i].x); minY = Mathf.Min(minY, corners[i].y); maxY = Mathf.Max(maxY, corners[i].y); }
        float scaleX = Mathf.Max(.01f, Mathf.Abs(view.root.lossyScale.x)), scaleY = Mathf.Max(.01f, Mathf.Abs(view.root.lossyScale.y));
        float marginX = 6f / scaleX, marginY = 6f / scaleY, lengthX = 10f / scaleX, lengthY = 10f / scaleY;
        float thicknessX = 2f / scaleX, thicknessY = 2f / scaleY;
        float width = (maxX - minX) / scaleX + marginX * 2f, height = (maxY - minY) / scaleY + marginY * 2f;
        view.root.gameObject.SetActive(true); view.root.transform.position = new Vector3((minX + maxX) * .5f, (minY + maxY) * .5f + verticalOffset, corners[0].z);
        view.root.sizeDelta = new Vector2(width, height);
        view.label.gameObject.SetActive(!string.IsNullOrEmpty(label)); view.label.Value = label; view.label.Color = color;
        view.label.rect.anchorMin = view.label.rect.anchorMax = new Vector2(.5f, .5f); view.label.rect.pivot = new Vector2(.5f, .5f);
        view.label.rect.anchoredPosition = new Vector2(0f, -height * .5f - 11f / scaleY); view.label.rect.sizeDelta = new Vector2(150f / scaleX, 18f / scaleY);
        float left = -width * .5f, right = width * .5f, bottom = -height * .5f, top = height * .5f;
        SetOverlayLine(view.lines[0], left + lengthX * .5f, top - thicknessY * .5f, lengthX, thicknessY, color);
        SetOverlayLine(view.lines[1], left + thicknessX * .5f, top - lengthY * .5f, thicknessX, lengthY, color);
        SetOverlayLine(view.lines[2], right - lengthX * .5f, top - thicknessY * .5f, lengthX, thicknessY, color);
        SetOverlayLine(view.lines[3], right - thicknessX * .5f, top - lengthY * .5f, thicknessX, lengthY, color);
        SetOverlayLine(view.lines[4], left + lengthX * .5f, bottom + thicknessY * .5f, lengthX, thicknessY, color);
        SetOverlayLine(view.lines[5], left + thicknessX * .5f, bottom + lengthY * .5f, thicknessX, lengthY, color);
        SetOverlayLine(view.lines[6], right - lengthX * .5f, bottom + thicknessY * .5f, lengthX, thicknessY, color);
        SetOverlayLine(view.lines[7], right - thicknessX * .5f, bottom + lengthY * .5f, thicknessX, lengthY, color);
    }
    private static void SetOverlayLine(Ui.Element line, float x, float y, float width, float height, Color color)
    { line.rect.anchoredPosition = new Vector2(x, y); line.rect.sizeDelta = new Vector2(width, height); Ui.Color(line, color); }
    private OverlayView BuildOverlay(string name, Transform parent, Color color)
    {
        Ui.Element root = Ui.Image(name, parent, Color.clear, false); Ui.Element[] lines = new Ui.Element[8];
        for (int i = 0; i < lines.Length; i++) { lines[i] = Ui.Image("Corner", root.gameObject.transform, color, false); lines[i].rect.anchorMin = lines[i].rect.anchorMax = new Vector2(.5f, .5f); }
        Ui.TextElement label = Ui.Text("", root.gameObject.transform, 10, color, "MiddleCenter"); label.gameObject.SetActive(false);
        root.gameObject.SetActive(false); return new OverlayView(root.rect, lines, label);
    }

    private RowView BuildRow(string name, Transform parent, Action click)
    {
        Ui.Element bg = Ui.Image(name, parent, rowColor, true); Ui.ButtonElement button = Ui.ButtonOn(bg, click);
        Ui.Element selection = Ui.Image("Selection", bg.gameObject.transform, Color.clear, false), assignment = Ui.Image("Assignment", bg.gameObject.transform, Color.clear, false);
        Ui.TextElement n = Ui.Text("", bg.gameObject.transform, 16, hudGreen, "UpperLeft"), type = Ui.Text("", bg.gameObject.transform, 11, mutedGreen, "UpperLeft");
        Ui.TextElement status = Ui.Text("", bg.gameObject.transform, 12, hudGreen, "UpperRight");
        Ui.TextElement metrics = Ui.Text("", bg.gameObject.transform, 11, mutedGreen, "LowerLeft"), ammo = Ui.Text("", bg.gameObject.transform, 11, hudGreen, "UpperRight");
        Ui.TextElement threat = Ui.Text("", bg.gameObject.transform, 11, Red, "LowerRight");
        return new RowView(bg, button, n, type, status, metrics, ammo, threat, selection, assignment);
    }
    private void LayoutRow(RowView? v, float x, float y, float w, float h)
    {
        if (v == null) return; SetRect(v.background.rect, x, y, w, h); SetRect(v.selection.rect, 0, 0, 3, h); SetRect(v.assignment.rect, w - 3, 0, 3, h);
        float lineH = Mathf.Max(18f, Mathf.Min(20f, (h - 8f) / 3f)), gap = Mathf.Max(1f, (h - lineH * 3f) / 4f);
        float y1 = gap, y2 = y1 + lineH + gap, y3 = y2 + lineH + gap;
        SetRect(v.name.rect, 11, y1, 112, lineH); SetRect(v.status.rect, w - 171, y1, 159, lineH);
        SetRect(v.type.rect, 11, y2, 104, lineH); SetRect(v.ammo.rect, w - 225, y2, 213, lineH);
        SetRect(v.metrics.rect, 11, y3, 235, lineH); SetRect(v.threat.rect, w - 71, y3, 59, lineH);
    }
    private static void LayoutButton(Ui.ButtonElement? b, float x, float y, float w, float h) { if (b != null) SetRect(b.background.rect, x, y, w, h); }
    private void SetEnabled(Ui.ButtonElement b, bool value) { b.Interactable = value; Ui.Color(b.background, value ? buttonColor : disabledBackground); b.label.Color = value ? hudGreen : disabledGreen; }

    private void SetSessionUi(bool value)
    {
        emptyStatus!.gameObject.SetActive(!value); emptyPrompt!.gameObject.SetActive(!value);
        targetSummary!.gameObject.SetActive(value); seadTargetSummary!.gameObject.SetActive(value);
        antiShipTargetSummary!.gameObject.SetActive(value); candidateSummary!.gameObject.SetActive(value);
        homeRow!.background.gameObject.SetActive(value); flightLabel!.gameObject.SetActive(value); flightCount!.gameObject.SetActive(value);
        groupPrevious!.background.gameObject.SetActive(value); groupLabel!.gameObject.SetActive(value); groupNext!.background.gameObject.SetActive(value);
        for (int i = 0; i < 4; i++) droneRows[i]!.background.gameObject.SetActive(value);
        feedbackBox!.gameObject.SetActive(value); commandBox!.gameObject.SetActive(value);
    }

    private void UpdateDamageDisplay(Aircraft? aircraft)
    {
        if (aircraft == null || aircraft != home && !roster.Contains(aircraft))
        {
            DestroyDamageDisplay();
            return;
        }
        if (damageAircraft == aircraft && damageRoot != null)
            return;
        DestroyDamageDisplay();
        try
        {
            MethodInfo getParameters = typeof(Aircraft).GetMethod("GetAircraftParameters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            object parameters = getParameters.Invoke(aircraft, null)!;
            FieldInfo prefabField = parameters.GetType().GetField("StatusDisplay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            GameObject prefab = (GameObject)prefabField.GetValue(parameters)!;
            damageRoot = UnityEngine.Object.Instantiate(prefab, commandBox!.gameObject.transform);
            damageRoot.name = "SelectedAircraftNativeStatusDisplay";
            damageRoot.transform.SetAsFirstSibling();
            // This cloned native display is visual-only and must not intercept command-panel input.
            Type graphicType = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI", true)!;
            foreach (object graphic in damageRoot.GetComponentsInChildren(graphicType, true))
                Ui.SetValue(graphic, "raycastTarget", false);
            // Subscribe the selected aircraft directly; Initialize would bind this prefab through the native HUD path.
            StatusDisplay display = damageRoot.GetComponent<StatusDisplay>();
            display.enabled = false;
            FieldInfo displaysField = typeof(StatusDisplay).GetField("statusDisplays", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (object partDisplay in (System.Collections.IEnumerable)displaysField.GetValue(display)!)
            {
                partDisplay.GetType().GetMethod("DamageSubscribe")!.Invoke(partDisplay, new object[] { aircraft, display });
                damageParts.Add(partDisplay);
                InitializeNativePart(partDisplay, aircraft);
            }
            Component background = (Component)typeof(StatusDisplay).GetField("aircraftBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(display)!;
            Ui.SetValue(background, "color", Color.white);
            foreach (GameObject indicator in (System.Collections.IEnumerable)typeof(StatusDisplay).GetField("failureIndicators", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(display)!) indicator.SetActive(false);
            damageAircraft = aircraft;
            LayoutDamage(Ui.CanvasScale(gameplay!));
        }
        catch (Exception e)
        {
            DestroyDamageDisplay();
            log("state=map_native_status_unavailable reason=" + e.GetType().Name);
        }
    }

    private static void InitializeNativePart(object partDisplay, Aircraft aircraft)
    {
        Type type = partDisplay.GetType();
        Component image = (Component)type.GetField("partImage")!.GetValue(partDisplay)!;
        UnitPart? match = null;
        foreach (UnitPart part in aircraft.partLookup)
            if (part != null && part.gameObject.name == image.gameObject.name)
            {
                match = part;
                break;
            }
        if (match == null)
            return;
        float threshold = (float)type.GetField("redStatusThreshold")!.GetValue(partDisplay)!;
        float condition = match.IsDetached() ? 0f : Mathf.Max((match.hitPoints - threshold) / (100f - threshold), 0f);
        type.GetField("displayCondition")!.SetValue(partDisplay, condition);
        Color color = (Color)Ui.GetValue(image, "color")!;
        if (match.IsDetached())
            color = new Color(.7f, 0f, .25f);
        else
        {
            color.g = Mathf.Min(condition * 2f, 1f);
            color.a = 1f - condition;
        }
        Ui.SetValue(image, "color", color);
    }

    private void LayoutDamage(float scale)
    {
        if (damageRoot == null)
            return;
        RectTransform r = damageRoot.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(.5f, 1f);
        r.pivot = new Vector2(.5f, .5f);
        r.localScale = Vector3.one;
        r.anchoredPosition = new Vector2(0, -Snap(76f, scale));
    }

    private void DestroyDamageDisplay()
    {
        foreach (object part in damageParts)
            try
            {
                part.GetType().GetMethod("DamageUnsubscribe")!.Invoke(part, null);
            }
            catch
            {
            }
        damageParts.Clear();
        if (damageRoot != null)
            UnityEngine.Object.Destroy(damageRoot);
        damageRoot = null;
        damageAircraft = null;
    }

    private static float Health(Aircraft a) { try { List<UnitPart> p = a.GetAllParts(); float sum = 0; int n = 0; foreach (UnitPart x in p) if (x != null) { sum += x.IsDetached() ? 0 : Mathf.Clamp01(x.hitPoints / 100); n++; } return n == 0 ? 0 : sum / n; } catch { return 0; } }
    private static float Fuel(Aircraft a) { try { return Mathf.Clamp01(a.GetFuelLevel()); } catch { return 0; } }
    private static int[] Ammo(Aircraft a)
    {
        int[] result = new int[Enum.GetValues(typeof(MapAmmoKind)).Length];
        try
        {
            foreach (WeaponStation station in a.weaponStations)
            {
                if (station == null || station.WeaponInfo == null) continue;
                WeaponInfo info = station.WeaponInfo;
                GameObject? prefab = info.weaponPrefab;
                string? seeker = prefab?.GetComponent<MissileSeeker>()?.GetSeekerType().ToString();
                MapAmmoInput input = new MapAmmoInput(
                    info.nuclear, info.glideBomb, info.bomb, info.gun, station.Cargo || info.cargo, info.sling, info.troops,
                    prefab?.GetComponent<ARMSeeker>() != null, prefab?.GetComponent<OpticalSeekerCruiseMissile>() != null,
                    prefab?.GetComponent<OpticalSeeker>() != null, info.targetRequirements.lineOfSight, info.laserGuided, prefab?.GetComponent<LaserSeeker>() != null,
                    prefab?.GetComponent<IRSeeker>() != null, prefab?.GetComponent<ARHSeeker>() != null, seeker, station.Ammo,
                    info.jammer);
                if (MapAmmoLogic.TryGetContribution(input, out MapAmmoKind category, out int amount)) result[(int)category] += amount;
            }
        }
        catch { }
        return result;
    }
    private static int Threats(Aircraft a) { try { int n = 0; MissileWarning w = a.GetMissileWarningSystem(); if (w != null) foreach (Missile m in w.knownMissiles) if (m != null && m.targetID == a.persistentID) n++; return n; } catch { return 0; } }
    private WingmanMission SnapshotMission(Aircraft aircraft)
    {
        return plugin.MapTryGetMission(aircraft, out WingmanMission mission) ? mission : WingmanMission.None;
    }
    private string StateOf(Aircraft a, WingmanMission mission)
    {
        try
        {
            if (plugin.MapModeOnlyRemoteSession && a != home && plugin.MapIsRegistered(a))
            {
                if (!plugin.MapModeKnown(a)) return "UNKNOWN MODE";
                if (plugin.MapTryGetStatus(a, out _, out DroneModeOverride remoteMode, out _, out _, out _))
                {
                    if (remoteMode == DroneModeOverride.Auto) return "AUTO";
                    if (remoteMode == DroneModeOverride.Follow) return "FOLLOW";
                    if (remoteMode == DroneModeOverride.Loiter) return "LOITER";
                }
                return "LOST";
            }
            if (plugin.MapIsReturningToBase(a)) return "RETURNING";
            if (GameManager.GetLocalAircraft(out Aircraft local) && local == a) return "CONTROLLED";
            if (plugin.MapTryGetStatus(a, out _, out DroneModeOverride mode, out _, out bool defending, out _))
            {
                if (defending) return "DEFENDING";
                if (mission != WingmanMission.None) return MissionText(mission);
                if (plugin.MapTryGetCruisePhase(a, out bool cruising) && cruising) return "CRUISE";
                if (mode == DroneModeOverride.Follow) return "FOLLOW"; if (mode == DroneModeOverride.Loiter) return "LOITER";
                return plugin.MapCurrentMode == WingmanMode.Follow ? "FOLLOW" : "LOITER";
            }
            return a == home ? "LOITER" : "LOST";
        }
        catch { return "LOST"; }
    }

    private void ShowFeedback(string value, Color color, float duration = 3) { if (feedback == null) return; feedback.Value = value; feedback.Color = color; feedbackUntil = Time.unscaledTime + duration; }
    private static string Reason(string r) => r switch { "already_controlled" => "ALREADY UNDER PLAYER CONTROL", "cooldown" => "AIRCRAFT SWITCH COOLDOWN ACTIVE", "missile_defense" => "MISSILE DEFENSE CURRENTLY HAS CONTROL", "player_association" => "RELEASE PLAYER CONTROL FIRST", "no_target" => "SELECT A TARGET ON THE MAP", "no_aam" => "NO IR/ARH MISSILES AVAILABLE", "no_compatible_station" => "NO COMPATIBLE WEAPON", "no_ammo" => "MISSION WEAPON EMPTY", "invalid_target" => "TARGET IS NO LONGER VALID", "target_already_designated" => "CLEAR CURRENT MISSION TARGET FIRST", "not_hostile" => "SELECTED UNIT IS NOT A VALID HOSTILE", "not_registered" => "SELECT AN FQ-106 FIRST", _ => "COMMAND UNAVAILABLE · " + r.Replace('_', ' ').ToUpperInvariant() };
    private static string MissionText(WingmanMission mission) => mission == WingmanMission.AirToAir ? "A2A" : mission == WingmanMission.Sead ? "SEAD" : mission == WingmanMission.AntiShip ? "ASUW" : mission == WingmanMission.Cas ? "CAS" : mission == WingmanMission.Strike ? "STRIKE" : mission == WingmanMission.Cap ? "CAP" : "NONE";
    private static string CandidateKind(Unit unit)
    {
        try { return unit is Ship ? "SURFACE SHIP" : unit is Aircraft ? "AIRCRAFT" : unit.HasRadarEmission() ? "EMITTER" : "UNIT"; }
        catch { return "UNIT"; }
    }
    private static string CandidateText(Unit unit)
    {
        return CandidateKind(unit) + " CANDIDATE  " + TargetName(unit);
    }
    private static string ShortName(Unit unit)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(unit.unitName)) return unit.unitName;
            return unit is Aircraft aircraft ? TypeOf(aircraft) : unit.GetType().Name.ToUpperInvariant();
        }
        catch { return "UNIT"; }
    }
    private static string TargetName(Unit unit)
    {
        string value = ShortName(unit); return value.Length <= 16 ? value : value.Substring(0, 15) + "…";
    }
    private static string TypeOf(Aircraft a) { try { return a.definition != null && !string.IsNullOrWhiteSpace(a.definition.code) ? a.definition.code.ToUpperInvariant() : a.definition == null ? "AIRCRAFT" : a.definition.name.ToUpperInvariant(); } catch { return "AIRCRAFT"; } }
    private static string Percent(float v) => Mathf.RoundToInt(Mathf.Clamp01(v) * 100).ToString("00") + "%";

    private void DestroyUi()
    {
        GameObject? staleCanvas = canvasObject;
        canvasObject = root = null; overlaysRoot = null; panel = feedbackBox = commandBox = directMissionDivider = missionRecoveryDivider = null; panelRect = null; homeRow = null;
        selectedOverlays.Clear();
        for (int i = 0; i < droneRows.Length; i++) droneRows[i] = null;
        flightLabel = flightCount = groupLabel = feedback = selectionSummary = targetSummary = seadTargetSummary = antiShipTargetSummary = candidateSummary = emptyStatus = emptyPrompt = null;
        groupPrevious = groupNext = takeControl = auto = follow = loiter = cruise = a2a = sead = asuw = cas = strike = cap = standDown = rtb = clearTarget = null;
        candidateOverlay = a2aTargetOverlay = seadTargetOverlay = antiShipTargetOverlay = null; visible = hasSession = false;
        observedSessionId = 0; observedSessionLocal = false;
        ResetNativeManagedObservation();
        ResetCasProjection(); ResetCapProjection(); ResetStrikeProjection(); ResetGroupState(true); DestroyDamageDisplay(); DestroyPointMarker();
        if (staleCanvas != null) UnityEngine.Object.Destroy(staleCanvas);
    }
    private static void SetRect(RectTransform r, float x, float y, float w, float h) { r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); }
    private static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    private static float Snap(float value, float scale) => Mathf.Round(value * scale) / scale;
    private static string ScenePath(Transform transform) { string value = transform.name; for (Transform? p = transform.parent; p != null; p = p.parent) value = p.name + "/" + value; return value; }
    private static Color C(int r, int g, int b, int a = 255) => new Color32((byte)r, (byte)g, (byte)b, (byte)a);

    private sealed class MapClickRelay : MonoBehaviour, IPointerClickHandler
    {
        internal MapCommandPanel? owner;
        public void OnPointerClick(PointerEventData eventData) => owner?.MapBackgroundClicked(eventData);
    }

    // uGUI is already loaded by the game but is not a project reference. Cache its exact public members once here.
    private static class Ui
    {
        private static readonly Type ImageType = Require("UnityEngine.UI.Image, UnityEngine.UI"),
                                     ButtonType = Require("UnityEngine.UI.Button, UnityEngine.UI"),
                                     CanvasType = Require("UnityEngine.Canvas, UnityEngine.UIModule");
        private static readonly Type InputType = Require("UnityEngine.Input, UnityEngine.InputLegacyModule");
        private static readonly Type RectTransformUtilityType = Require("UnityEngine.RectTransformUtility, UnityEngine.UIModule");
        private static readonly MethodInfo GetMouseButtonUpMethod = InputType.GetMethod(
            "GetMouseButtonUp", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null)!;
        private static readonly MethodInfo GetKeyMethod = InputType.GetMethod(
            "GetKey", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(KeyCode) }, null)!;
        private static readonly PropertyInfo MousePositionProperty = InputType.GetProperty(
            "mousePosition", BindingFlags.Static | BindingFlags.Public)!;
        private static readonly MethodInfo RectangleContainsScreenPointMethod = RectTransformUtilityType.GetMethod(
            "RectangleContainsScreenPoint", BindingFlags.Static | BindingFlags.Public, null,
            new[] { typeof(RectTransform), typeof(Vector2), typeof(Camera) }, null)!;
        private static readonly Dictionary<string, PropertyInfo> Properties = new Dictionary<string, PropertyInfo>();
        private static object? sharedTextStyle;
        private static Type? activeTextType;

        internal sealed class Element
        {
            internal readonly GameObject gameObject; internal readonly RectTransform rect; internal readonly Component graphic;
            internal Element(GameObject o, Component g) { gameObject = o; rect = o.GetComponent<RectTransform>(); graphic = g; }
        }
        internal sealed class TextElement
        {
            internal readonly GameObject gameObject; internal readonly RectTransform rect; internal readonly Component text;
            internal TextElement(GameObject o, Component t) { gameObject = o; rect = o.GetComponent<RectTransform>(); text = t; }
            internal string Value { get => (string?)Get(text, "text") ?? ""; set => Set(text, "text", value); }
            internal Color Color { set => Set(text, "color", value); }
            internal int FontSize { set => Set(text, "fontSize", value); }
        }
        internal sealed class ButtonElement
        {
            internal readonly Element background; internal readonly Component button; internal readonly TextElement label;
            internal ButtonElement(Element bg, Component b, TextElement l) { background = bg; button = b; label = l; }
            internal bool Interactable { set => Set(button, "interactable", value); }
        }
        internal sealed class EntrySlot : IDisposable
        {
            internal readonly RectTransform rect;
            internal readonly int slotIndex;
            private readonly GameObject gameObject;
            private readonly Component button, text, highlight, mfd;
            private readonly System.Collections.IList leftScreens;
            private readonly object? originalScreen;
            private readonly string originalLabel;
            private readonly bool originalEnabled, originalInteractable, originalActive;
            private Component? screen;
            private GameObject? displayPanel;
            private bool overridden, installed;
            internal EntrySlot(GameObject o, RectTransform rect, Component button, Component text, Component highlight,
                               Component mfd, System.Collections.IList leftScreens, int slotIndex)
            {
                gameObject = o;
                this.rect = rect;
                this.slotIndex = slotIndex;
                this.button = button;
                this.text = text;
                this.highlight = highlight;
                this.mfd = mfd;
                // LW temporarily owns the verified serialized left-screen slot and restores its original MFDScreen on release.
                this.leftScreens = leftScreens;
                originalScreen = leftScreens[slotIndex];
                originalLabel = (string?)Get(text, "text") ?? "";
                originalEnabled = (bool)(Get(button, "enabled") ?? false);
                originalInteractable = (bool)(Get(button, "interactable") ?? false);
                originalActive = o.activeSelf;
            }
            internal Component TextComponent => text;
            internal Component HighlightComponent => highlight;
            internal string SlotPath => ScenePath(gameObject.transform);
            internal string TextPath => ScenePath(text.transform);
            internal string HighlightPath => ScenePath(highlight.transform);
            internal bool ApplyTargetPanelStyle(Element target, Action<string> log)
            {
                var rightScreens = (System.Collections.IList)typeof(VirtualMFD).GetField("rightScreens", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mfd)!;
                if (rightScreens.Count <= 1 || rightScreens[1] == null) return false;
                object targetScreen = rightScreens[1]!;
                GameObject sourcePanel = (GameObject)Field(targetScreen.GetType(), "displayPanel").GetValue(targetScreen)!;
                Component? source = sourcePanel.GetComponent(ImageType); if (source == null) return false;
                string[] members = { "sprite", "type", "fillCenter", "material", "color", "pixelsPerUnitMultiplier", "preserveAspect", "useSpriteMesh", "raycastTarget", "maskable" };
                foreach (string member in members) Set(target.graphic, member, Get(source, member));
                log("state=map_panel_style source=VirtualMFD.rightScreens[1].displayPanel sprite=" +
                    ((UnityEngine.Object?)Get(source, "sprite"))?.name + " type=" + Get(source, "type") +
                    " fill=" + Get(source, "fillCenter") + " color=" + Rgba((Color)Get(source, "color")!) +
                    " ppu=" + Get(source, "pixelsPerUnitMultiplier"));
                return true;
            }
            internal bool PrepareScreen(GameObject screenObject, GameObject panel)
            {
                if (screen != null)
                    return true;
                if (originalScreen != null && (bool)(Field(originalScreen.GetType(), "isActive").GetValue(originalScreen) ?? false))
                    return false;
                screen = screenObject.AddComponent(typeof(MFDScreen));
                displayPanel = panel;
                SetField(screen, "virtualMFD", mfd);
                SetField(screen, "label", text);
                SetField(screen, "highlight", highlight);
                SetField(screen, "shortName", "LW");
                SetField(screen, "displayPanel", panel);
                SetField(screen, "aircraftOnly", false);
                SetField(screen, "isActive", false);
                Set(highlight, "enabled", false);
                panel.SetActive(false);
                return true;
            }
            internal void SetLoyalWingman(bool enabled, bool mapOpen)
            {
                if (enabled)
                {
                    if (screen == null || leftScreens.Count <= slotIndex || !Same(leftScreens[slotIndex], originalScreen) && !Same(leftScreens[slotIndex], screen))
                        return;
                    if (!Same(leftScreens[slotIndex], screen))
                        leftScreens[slotIndex] = screen;
                    screen.GetType().GetMethod("Setup")!.Invoke(screen, new object[] { mfd, "LW" });
                    installed = true;
                    if (mapOpen) gameObject.SetActive(true);
                    Set(button, "enabled", true);
                    Set(button, "interactable", true);
                    overridden = true;
                    FieldInfo active = typeof(VirtualMFD).GetField("activeLeft", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    bool selected = Same(active.GetValue(mfd), screen);
                    if (!mapOpen)
                    {
                        gameObject.SetActive(false);
                        if (displayPanel != null) displayPanel.SetActive(false);
                        SetField(screen, "isActive", selected); Set(highlight, "enabled", selected);
                    }
                    else if (selected && displayPanel != null && !displayPanel.activeSelf)
                        screen.GetType().GetMethod("ShowScreen")!.Invoke(screen, new object[] { Vector3.zero });
                }
                else if (overridden)
                    Restore();
            }
            internal bool IsScreenActive => installed && screen != null && leftScreens.Count > slotIndex && Same(leftScreens[slotIndex], screen) &&
                                          (bool)(Field(screen.GetType(), "isActive").GetValue(screen) ?? false) && displayPanel != null && displayPanel.activeSelf;
            private void Restore()
            {
                bool restoreScreen = installed;
                installed = overridden = false;
                if (restoreScreen)
                {
                    if (IsAlive(screen))
                        TryRestore(() => screen!.GetType().GetMethod("CloseScreen")!.Invoke(screen, new object[] { -Screen.width * Vector3.right }));
                    if (IsAlive(mfd))
                    {
                        TryRestore(() =>
                        {
                            FieldInfo active = typeof(VirtualMFD).GetField("activeLeft", BindingFlags.Instance | BindingFlags.NonPublic)!;
                            if (Same(active.GetValue(mfd), screen)) active.SetValue(mfd, null);
                        });
                    }
                    if (leftScreens.Count > slotIndex && Same(leftScreens[slotIndex], screen))
                        leftScreens[slotIndex] = IsAlive(originalScreen) ? originalScreen : null;
                }
                if (IsAlive(highlight)) TryRestore(() => Set(highlight, "enabled", false));
                if (IsAlive(text)) TryRestore(() => Set(text, "text", originalLabel));
                if (IsAlive(button))
                {
                    TryRestore(() => Set(button, "enabled", originalEnabled));
                    TryRestore(() => Set(button, "interactable", originalInteractable));
                }
                if (IsAlive(gameObject)) TryRestore(() => gameObject.SetActive(originalActive));
            }
            public void Dispose() => Restore();

            private static void TryRestore(Action restore)
            {
                try { restore(); }
                catch (MissingReferenceException) { }
                catch (TargetInvocationException e) when (e.InnerException is MissingReferenceException || e.InnerException is NullReferenceException) { }
            }
        }
        internal static EntrySlot? BindEntrySlot(GameplayUI gameplay, Action<string> log, out string reason)
        {
            const int intendedSlotIndex = 3;
            reason = "unknown";
            try
            {
                Component[] mfds = gameplay.GetComponentsInChildren(typeof(VirtualMFD), true);
                if (mfds.Length == 0) return Fail(out reason, "virtual_mfd_missing");
                if (mfds.Length != 1) return Fail(out reason, "virtual_mfd_ambiguous_" + mfds.Length);
                Component mfd = mfds[0];
                if (!mfd.transform.IsChildOf(gameplay.transform)) return Fail(out reason, "virtual_mfd_outside_gameplay_ui");

                FieldInfo? buttonsField = FindField(mfd.GetType(), "leftButtons");
                FieldInfo? screensField = FindField(mfd.GetType(), "leftScreens");
                if (buttonsField == null) return Fail(out reason, "left_buttons_field_missing");
                if (screensField == null) return Fail(out reason, "left_screens_field_missing");
                if (buttonsField.IsPublic || buttonsField.IsStatic) return Fail(out reason, "left_buttons_field_not_private_instance");
                if (screensField.IsPublic || screensField.IsStatic) return Fail(out reason, "left_screens_field_not_private_instance");
                if (!HasListElementType(buttonsField.FieldType, ButtonType)) return Fail(out reason, "left_buttons_element_type_mismatch");
                if (!HasListElementType(screensField.FieldType, typeof(MFDScreen))) return Fail(out reason, "left_screens_element_type_mismatch");
                if (!(buttonsField.GetValue(mfd) is System.Collections.IList buttons)) return Fail(out reason, "left_buttons_not_list");
                if (!(screensField.GetValue(mfd) is System.Collections.IList screens)) return Fail(out reason, "left_screens_not_list");
                if (buttons.Count <= intendedSlotIndex) return Fail(out reason, "left_button_count_" + buttons.Count);
                if (screens.Count <= intendedSlotIndex) return Fail(out reason, "left_screen_count_" + screens.Count);
                if (!(buttons[intendedSlotIndex] is Component button)) return Fail(out reason, "left_button_slot_3_not_component");
                if (!ButtonType.IsInstanceOfType(button)) return Fail(out reason, "left_button_slot_3_type_" + button.GetType().FullName);
                Component? attachedButton = button.gameObject.GetComponent(ButtonType);
                if (!Same(attachedButton, button)) return Fail(out reason, "left_button_slot_3_component_identity_mismatch");
                if (!button.transform.IsChildOf(mfd.transform)) return Fail(out reason, "left_button_slot_3_outside_virtual_mfd");
                int identityCount = 0;
                for (int i = 0; i < buttons.Count; i++) if (Same(buttons[i], button)) identityCount++;
                if (identityCount != 1) return Fail(out reason, "left_button_slot_3_identity_count_" + identityCount);
                RectTransform? rect = button.gameObject.GetComponent<RectTransform>();
                if (rect == null) return Fail(out reason, "left_button_slot_3_rect_missing");

                object? originalScreen = screens[intendedSlotIndex];
                if (originalScreen != null && !typeof(MFDScreen).IsInstanceOfType(originalScreen))
                    return Fail(out reason, "left_screen_slot_3_type_" + originalScreen.GetType().FullName);
                if (originalScreen is Component originalScreenComponent &&
                    !Same(originalScreenComponent.gameObject.GetComponent(typeof(MFDScreen)), originalScreenComponent))
                    return Fail(out reason, "left_screen_slot_3_component_identity_mismatch");
                FieldInfo? labelField = FindField(typeof(MFDScreen), "label");
                FieldInfo? highlightField = FindField(typeof(MFDScreen), "highlight");
                if (labelField == null) return Fail(out reason, "mfd_screen_label_field_missing");
                if (highlightField == null) return Fail(out reason, "mfd_screen_highlight_field_missing");
                Type textType = labelField.FieldType, highlightType = highlightField.FieldType;
                if (!typeof(Component).IsAssignableFrom(textType)) return Fail(out reason, "mfd_screen_label_type_not_component");
                if (!typeof(Component).IsAssignableFrom(highlightType) || !ImageType.IsAssignableFrom(highlightType))
                    return Fail(out reason, "mfd_screen_highlight_type_incompatible_" + highlightType.FullName);

                Component? text = null, highlight = null;
                int nativeHighlightPairCount = 0, nativeHighlightSignatureCount = 0;
                string highlightSource = "original_screen", nativeHighlightSignature = "original_screen";
                if (originalScreen != null)
                {
                    object? nativeLabel = labelField.GetValue(originalScreen), nativeHighlight = highlightField.GetValue(originalScreen);
                    if (nativeLabel != null)
                    {
                        if (!(nativeLabel is Component labelComponent) || !textType.IsInstanceOfType(labelComponent))
                            return Fail(out reason, "native_label_type_mismatch");
                        if (!labelComponent.transform.IsChildOf(button.transform)) return Fail(out reason, "native_label_outside_selected_button");
                        text = labelComponent;
                    }
                    if (nativeHighlight != null)
                    {
                        if (!(nativeHighlight is Component highlightComponent) || !highlightType.IsInstanceOfType(highlightComponent))
                            return Fail(out reason, "native_highlight_type_mismatch");
                        if (!highlightComponent.transform.IsChildOf(button.transform)) return Fail(out reason, "native_highlight_outside_selected_button");
                        highlight = highlightComponent;
                    }
                }
                if (text == null)
                {
                    Component[] candidates = BoundedComponents(button, textType, null);
                    if (candidates.Length == 0) return Fail(out reason, "selected_button_label_missing");
                    if (candidates.Length != 1) return Fail(out reason, "selected_button_label_ambiguous_" + candidates.Length);
                    text = candidates[0];
                }
                if (highlight == null)
                {
                    if (!TryResolveNativeHighlight(button, mfd, buttons, screens, intendedSlotIndex, highlightField, highlightType,
                            out highlight, out nativeHighlightPairCount, out nativeHighlightSignatureCount,
                            out nativeHighlightSignature, out string highlightReason))
                        return Fail(out reason, highlightReason);
                    highlightSource = "neighbor_signature";
                }
                if (!Same(text.gameObject.GetComponent(textType), text)) return Fail(out reason, "selected_button_label_component_identity_mismatch");
                if (highlight == null) return Fail(out reason, "selected_button_highlight_resolution_returned_null");
                if (!HasExactComponentIdentity(highlight.gameObject, highlightType, highlight)) return Fail(out reason, "selected_button_highlight_component_identity_mismatch");

                activeTextType = textType;
                EntrySlot entry = new EntrySlot(button.gameObject, rect, button, text, highlight, mfd, screens, intendedSlotIndex);
                log("state=map_panel_entry_bound index=" + intendedSlotIndex + " slot_path=" + entry.SlotPath +
                    " button_count=" + buttons.Count + " screen_count=" + screens.Count + " text_type=" + textType.FullName +
                    " original_screen=" + (originalScreen != null) + " highlight_source=" + highlightSource +
                    " native_pair_count=" + nativeHighlightPairCount + " native_signature_count=" + nativeHighlightSignatureCount +
                    " native_signature=" + nativeHighlightSignature + " selected_overlay_path=" + entry.HighlightPath);
                reason = "none";
                return entry;
            }
            catch (Exception e)
            {
                reason = "entry_bind_exception_" + e.GetType().Name + "_" + SafeReason(e.Message);
                return null;
            }
        }
        internal static bool CaptureTextStyle(EntrySlot entry, GameplayUI gameplay, Action<string> log)
        {
            Component text = entry.TextComponent; Transform source = text.transform;
            if (activeTextType == null || !activeTextType.IsInstanceOfType(text) || !(source is RectTransform sourceRect)) return false;
            if (!HasProperty(text.GetType(), "text") || !HasProperty(text.GetType(), "font") || !HasProperty(text.GetType(), "fontSize") ||
                !HasProperty(text.GetType(), "material") || !HasProperty(text.GetType(), "fontStyle") || !HasProperty(text.GetType(), "lineSpacing") ||
                !HasProperty(text.GetType(), "color") || !HasProperty(text.GetType(), "alignment") || !HasProperty(text.GetType(), "raycastTarget") ||
                !HasAnyProperty(text.GetType(), "supportRichText", "richText") ||
                !HasAnyProperty(text.GetType(), "resizeTextForBestFit", "enableAutoSizing") ||
                !HasAnyProperty(text.GetType(), "overflowMode", "horizontalOverflow")) return false;
            sharedTextStyle = text;
            int canvasDepth = 0; Transform? parent = source; while (parent != null) { if (parent.GetComponent(CanvasType) != null) canvasDepth++; parent = parent.parent; }
            Vector3[] corners = new Vector3[4]; sourceRect.GetWorldCorners(corners);
            object canvas = GameplayCanvas(gameplay); log("state=map_text_style source=" + entry.TextPath + " type=" + text.GetType().FullName + " font=" +
                ((UnityEngine.Object?)Get(text, "font"))?.name + " material=" + ((UnityEngine.Object?)Get(text, "material"))?.name +
                " style=" + Get(text, "fontStyle") + " size=" + Get(text, "fontSize") + " canvas_scale=" + Get(canvas, "scaleFactor") +
                " source_canvas_depth=" + canvasDepth + " source_screen_xy=" + Mathf.Round(corners[0].x) + "," + Mathf.Round(corners[0].y) +
                " source_lossy_scale=" + source.lossyScale.x + "," + source.lossyScale.y + " panel_canvas_depth=1 screen=" + Screen.width + "x" + Screen.height);
            return true;
        }
        internal static bool CapturePalette(EntrySlot entry, Action<string> log, out Color labelGreen, out Color highlightGreen)
        {
            labelGreen = highlightGreen = UnityEngine.Color.clear;
            Component label = entry.TextComponent, highlight = entry.HighlightComponent;
            if (!TryGet(label, "color", out object? labelColor) || !(labelColor is Color capturedLabel) ||
                !TryGet(highlight, "color", out object? highlightColor) || !(highlightColor is Color rawHighlight)) return false;
            labelGreen = capturedLabel;
            highlightGreen = rawHighlight.g >= rawHighlight.r && rawHighlight.g >= rawHighlight.b ? rawHighlight : labelGreen;
            log("state=map_palette source_label=" + entry.TextPath + " label_type=" + label.GetType().FullName + " label_rgba=" + Rgba(labelGreen) +
                " source_highlight=" + entry.HighlightPath + " highlight_type=" + highlight.GetType().FullName + " highlight_rgba=" + Rgba(rawHighlight));
            return true;
        }
        internal static bool MouseButtonUp(int button) => (bool)(GetMouseButtonUpMethod.Invoke(null, new object[] { button }) ?? false);
        internal static bool ControlHeld => (bool)(GetKeyMethod.Invoke(null, new object[] { KeyCode.LeftControl }) ?? false) ||
                                            (bool)(GetKeyMethod.Invoke(null, new object[] { KeyCode.RightControl }) ?? false);
        internal static Vector2 MousePosition { get { Vector3 value = (Vector3)(MousePositionProperty.GetValue(null, null) ?? Vector3.zero); return new Vector2(value.x, value.y); } }
        internal static bool Contains(RectTransform rect, Vector2 point) => (bool)(
            RectangleContainsScreenPointMethod.Invoke(null, new object?[] { rect, point, null }) ?? false);
        internal static Component GameplayCanvas(GameplayUI gameplay) => (Component)typeof(GameplayUI).GetField("gameplayCanvas", BindingFlags.Instance | BindingFlags.Public)!.GetValue(gameplay)!;
        internal static float CanvasScale(GameplayUI gameplay) => Mathf.Max(.01f, (float)(Get(GameplayCanvas(gameplay), "scaleFactor") ?? 1f));
        internal static Element Image(string name, Transform parent, Color color, bool raycast)
        {
            GameObject o = new GameObject(name, typeof(RectTransform)); o.transform.SetParent(parent, false);
            Component image = o.AddComponent(ImageType); Set(image, "color", color); Set(image, "raycastTarget", raycast);
            return new Element(o, image);
        }
        internal static TextElement Text(string value, Transform parent, int size, Color color, string alignment)
        {
            if (activeTextType == null || sharedTextStyle == null) throw new InvalidOperationException("native_text_style_not_bound");
            GameObject o = new GameObject("Text", typeof(RectTransform));
            o.transform.SetParent(parent, false);
            GameObject glyph = new GameObject("Glyph", typeof(RectTransform));
            glyph.transform.SetParent(o.transform, false);
            RectTransform glyphRect = glyph.GetComponent<RectTransform>();
            glyphRect.anchorMin = new Vector2(-.5f, -.5f);
            glyphRect.anchorMax = new Vector2(1.5f, 1.5f);
            glyphRect.offsetMin = glyphRect.offsetMax = Vector2.zero;
            glyphRect.pivot = new Vector2(.5f, .5f);
            glyphRect.localScale = new Vector3(.5f, .5f, 1f);
            Component text = glyph.AddComponent(activeTextType);
            Set(text, "text", value);
            CopyRequired(text, "font");
            CopyRequired(text, "material");
            CopyRequired(text, "fontStyle");
            CopyRequired(text, "lineSpacing");
            CopyAvailable(text, "supportRichText", "richText");
            CopyAvailable(text, "alignByGeometry");
            SetAvailable(text, false, "resizeTextForBestFit", "enableAutoSizing");
            // A 34-point glyph at half scale preserves the MFD's half-unit text grid at every canvas scale.
            Set(text, "fontSize", 34);
            Set(text, "color", color);
            SetTextAlignment(text, alignment);
            Set(text, "raycastTarget", false);
            if (HasProperty(text.GetType(), "overflowMode"))
                SetEnum(text, "overflowMode", "Overflow");
            else
            {
                SetEnum(text, "horizontalOverflow", "Overflow");
                SetEnum(text, "verticalOverflow", "Overflow");
            }
            return new TextElement(o, text);
        }
        internal static ButtonElement Button(string name, Transform parent, string label, int size, Color background, Color textColor, Action click)
        {
            Element image = Image(name, parent, background, true); Component button = image.gameObject.AddComponent(ButtonType); Set(button, "targetGraphic", image.graphic);
            object onClick = Get(button, "onClick")!; MethodInfo add = onClick.GetType().GetMethod("AddListener")!;
            Delegate callback = Delegate.CreateDelegate(add.GetParameters()[0].ParameterType, click.Target, click.Method); add.Invoke(onClick, new object[] { callback });
            TextElement text = Text(label, image.gameObject.transform, size, textColor, "MiddleCenter"); Stretch(text.rect);
            text.FontSize = size;
            return new ButtonElement(image, button, text);
        }
        internal static ButtonElement ButtonOn(Element image, Action click)
        {
            Component button = image.gameObject.AddComponent(ButtonType); Set(button, "targetGraphic", image.graphic);
            object onClick = Get(button, "onClick")!; MethodInfo add = onClick.GetType().GetMethod("AddListener")!;
            Delegate callback = Delegate.CreateDelegate(add.GetParameters()[0].ParameterType, click.Target, click.Method); add.Invoke(onClick, new object[] { callback });
            TextElement empty = Text("", image.gameObject.transform, 1, UnityEngine.Color.clear, "MiddleCenter"); return new ButtonElement(image, button, empty);
        }
        internal static void Color(Element element, Color color) => Set(element.graphic, "color", color);
        internal static object? GetValue(object target, string name) => Get(target, name);
        internal static void SetValue(object target, string name, object? value) => Set(target, name, value);
        internal static bool IsDestroyed(object? value) => value is UnityEngine.Object unityObject && unityObject == null;
        private static bool IsAlive(object? value) => value != null && !IsDestroyed(value);
        private static void CopyRequired(object target, string name)
        {
            if (sharedTextStyle == null) throw new InvalidOperationException("native_text_style_not_bound");
            Set(target, name, Get(sharedTextStyle, name));
        }
        private static void CopyAvailable(object target, params string[] names)
        {
            if (sharedTextStyle == null) throw new InvalidOperationException("native_text_style_not_bound");
            foreach (string name in names)
            {
                if (!HasProperty(target.GetType(), name) || !HasProperty(sharedTextStyle.GetType(), name)) continue;
                Set(target, name, Get(sharedTextStyle, name));
                return;
            }
        }
        private static void SetAvailable(object target, object? value, params string[] names)
        {
            foreach (string name in names)
            {
                if (!HasProperty(target.GetType(), name)) continue;
                Set(target, name, value);
                return;
            }
            throw new MissingMemberException(target.GetType().FullName, string.Join("/", names));
        }
        private static Type Require(string value) => Type.GetType(value, true)!;
        private static PropertyInfo Property(Type type, string name)
        {
            string key = type.FullName + "." + name; if (!Properties.TryGetValue(key, out PropertyInfo property))
            { property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!; Properties.Add(key, property); }
            return property;
        }
        private static bool HasProperty(Type type, string name) => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) != null;
        private static bool HasAnyProperty(Type type, params string[] names)
        {
            foreach (string name in names) if (HasProperty(type, name)) return true;
            return false;
        }
        private static object? Get(object target, string name) => Property(target.GetType(), name).GetValue(target, null);
        private static bool TryGet(object target, string name, out object? value)
        {
            PropertyInfo? property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null) { value = null; return false; }
            value = property.GetValue(target, null); return true;
        }
        private static void Set(object target, string name, object? value)
        {
            PropertyInfo property = Property(target.GetType(), name);
            if (value != null && !property.PropertyType.IsInstanceOfType(value) && IsNumeric(value.GetType()) && IsNumeric(property.PropertyType))
                value = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(target, value, null);
        }
        private static bool Same(object? a, object? b) => ReferenceEquals(a, b);
        private static string Rgba(Color c) => Mathf.RoundToInt(c.r * 255) + "," + Mathf.RoundToInt(c.g * 255) + "," + Mathf.RoundToInt(c.b * 255) + "," + Mathf.RoundToInt(c.a * 255);
        private static FieldInfo? FindField(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static FieldInfo Field(Type type, string name) => FindField(type, name) ?? throw new MissingFieldException(type.FullName, name);
        private static void SetField(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
        private static void SetEnum(object target, string name, string value) { PropertyInfo property = Property(target.GetType(), name); property.SetValue(target, Enum.Parse(property.PropertyType, value), null); }
        private static void SetTextAlignment(object target, string anchor)
        {
            PropertyInfo property = Property(target.GetType(), "alignment");
            string value = anchor;
            if (!Enum.IsDefined(property.PropertyType, value))
            {
                value = anchor switch
                {
                    "UpperLeft" => "TopLeft", "UpperCenter" => "Top", "UpperRight" => "TopRight",
                    "MiddleLeft" => "MidlineLeft", "MiddleCenter" => "Midline", "MiddleRight" => "MidlineRight",
                    "LowerLeft" => "BottomLeft", "LowerCenter" => "Bottom", "LowerRight" => "BottomRight",
                    _ => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, "Unsupported text anchor")
                };
            }
            property.SetValue(target, Enum.Parse(property.PropertyType, value), null);
        }
        private static bool IsNumeric(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }
        private static EntrySlot? Fail(out string reason, string value) { reason = value; return null; }
        private static bool HasListElementType(Type listType, Type expected)
        {
            if (!typeof(System.Collections.IList).IsAssignableFrom(listType) || !listType.IsGenericType) return false;
            Type[] arguments = listType.GetGenericArguments();
            return arguments.Length == 1 && arguments[0] == expected;
        }
        private static Component[] BoundedComponents(Component button, Type type, object? excluded)
        {
            Component[] all = button.GetComponentsInChildren(type, true);
            var result = new List<Component>();
            foreach (Component candidate in all)
                if (candidate != null && candidate.transform.IsChildOf(button.transform) && !Same(candidate, excluded)) result.Add(candidate);
            return result.ToArray();
        }
        private static bool TryResolveNativeHighlight(Component selectedButton, Component mfd,
            System.Collections.IList buttons, System.Collections.IList screens, int selectedIndex,
            FieldInfo highlightField, Type highlightType, out Component? selectedHighlight,
            out int pairCount, out int signatureCount, out string signature, out string reason)
        {
            selectedHighlight = null; pairCount = signatureCount = 0; signature = "none"; reason = "unknown";
            var signatures = new Dictionary<string, int>();
            for (int i = 0; i < screens.Count; i++)
            {
                if (i == selectedIndex || screens[i] == null) continue;
                pairCount++;
                if (i >= buttons.Count)
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_button_missing", pairCount, signatures.Count);
                if (!(buttons[i] is Component pairButton) || !ButtonType.IsInstanceOfType(pairButton))
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_button_type", pairCount, signatures.Count);
                if (!HasExactComponentIdentity(pairButton.gameObject, ButtonType, pairButton) || !pairButton.transform.IsChildOf(mfd.transform))
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_button_identity", pairCount, signatures.Count);
                if (!(screens[i] is Component pairScreen) || !typeof(MFDScreen).IsInstanceOfType(pairScreen) ||
                    !HasExactComponentIdentity(pairScreen.gameObject, typeof(MFDScreen), pairScreen))
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_screen_identity", pairCount, signatures.Count);
                object? value = highlightField.GetValue(pairScreen);
                if (!(value is Component pairHighlight) || pairHighlight.GetType() != highlightType)
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_field_type", pairCount, signatures.Count);
                if (!HasExactComponentIdentity(pairHighlight.gameObject, highlightType, pairHighlight))
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_component_identity", pairCount, signatures.Count);
                if (!TryBuildHighlightSignature(pairButton, pairHighlight, highlightType, out string pairSignature, out string pairReason))
                    return HighlightFail(out reason, "native_highlight_pair_" + i + "_" + pairReason, pairCount, signatures.Count);
                signatures[pairSignature] = signatures.TryGetValue(pairSignature, out int support) ? support + 1 : 1;
            }
            signatureCount = signatures.Count;
            if (pairCount == 0) return HighlightFail(out reason, "native_highlight_no_neighbor_pairs", pairCount, signatureCount);
            if (signatureCount != 1) return HighlightFail(out reason, "native_highlight_signature_ambiguous", pairCount, signatureCount);
            foreach (string key in signatures.Keys) signature = key;

            object? targetGraphic = TryGet(selectedButton, "targetGraphic", out object? graphic) ? graphic : null;
            bool rootTargetRejected = targetGraphic is Component target && Same(target.transform, selectedButton.transform);
            int matches = 0;
            foreach (Component candidate in selectedButton.GetComponentsInChildren(highlightType, true))
            {
                if (candidate == null || candidate.GetType() != highlightType) continue;
                if (!TryBuildHighlightSignature(selectedButton, candidate, highlightType, out string candidateSignature, out _)) continue;
                if (candidateSignature != signature) continue;
                selectedHighlight = candidate; matches++;
            }
            if (matches != 1)
            {
                selectedHighlight = null;
                return HighlightFail(out reason, "native_highlight_selected_match_" + matches + "_root_target_rejected_" + rootTargetRejected,
                    pairCount, signatureCount);
            }
            reason = "none";
            return true;
        }
        private static bool TryBuildHighlightSignature(Component button, Component highlight, Type highlightType,
            out string signature, out string reason)
        {
            signature = "none"; reason = "unknown";
            if (highlight.GetType() != highlightType) { reason = "endpoint_type"; return false; }
            if (Same(highlight.transform, button.transform)) { reason = "button_root_rejected"; return false; }
            if (!highlight.transform.IsChildOf(button.transform)) { reason = "outside_button"; return false; }
            object? targetGraphic = TryGet(button, "targetGraphic", out object? graphic) ? graphic : null;
            if (Same(highlight, targetGraphic)) { reason = "target_graphic_rejected"; return false; }

            var childPath = new List<int>();
            Transform current = highlight.transform;
            while (!Same(current, button.transform))
            {
                childPath.Add(current.GetSiblingIndex());
                if (current.parent == null) { reason = "path_unrooted"; return false; }
                current = current.parent;
            }
            childPath.Reverse();
            Component[] endpointComponents = highlight.gameObject.GetComponents(highlightType);
            int ordinal = -1;
            for (int i = 0; i < endpointComponents.Length; i++) if (Same(endpointComponents[i], highlight)) { ordinal = i; break; }
            if (ordinal < 0) { reason = "endpoint_identity"; return false; }
            signature = string.Join(".", childPath) + "|" + highlightType.FullName + "|" + ordinal;
            reason = "none";
            return true;
        }
        private static bool HasExactComponentIdentity(GameObject owner, Type type, Component expected)
        {
            int matches = 0;
            foreach (Component component in owner.GetComponents(type)) if (Same(component, expected)) matches++;
            return matches == 1;
        }
        private static bool HighlightFail(out string reason, string value, int pairs, int signatures)
        {
            reason = value + "_pairs_" + pairs + "_signatures_" + signatures;
            return false;
        }
        private static string SafeReason(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "no_detail";
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-') chars[i] = '_';
            return new string(chars);
        }
    }

    private sealed class LostEntry
    {
        internal int groupId, groupSlot;
        internal Aircraft displayReference = null!;
        internal float expiresAt;
    }

    private sealed class RowView
    {
        internal readonly Ui.Element background, selection, assignment; internal readonly Ui.ButtonElement button; internal readonly Ui.TextElement name, type, status, metrics, ammo, threat; internal Aircraft? aircraft;
        internal RowView(Ui.Element bg, Ui.ButtonElement b, Ui.TextElement n, Ui.TextElement t, Ui.TextElement s, Ui.TextElement m, Ui.TextElement a, Ui.TextElement th, Ui.Element sel, Ui.Element ass) { background = bg; button = b; name = n; type = t; status = s; metrics = m; ammo = a; threat = th; selection = sel; assignment = ass; }
    }
    private sealed class OverlayView { internal readonly RectTransform root; internal readonly Ui.Element[] lines; internal readonly Ui.TextElement label; internal OverlayView(RectTransform r, Ui.Element[] l, Ui.TextElement t) { root = r; lines = l; label = t; } }
}

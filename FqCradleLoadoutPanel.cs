using System;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LoyalWingman;

internal sealed class FqCradleLoadoutPanel : IDisposable
{
    private const int PhysicalRoundsPerCradle = FqCradleLoadoutLogic.PhysicalRoundsPerCradle;
    private const float LabelWidth = 64f;
    private const float DropdownHeight = 44f;
    private const float PopupItemHeight = 34f;
    private readonly Plugin plugin;
    private readonly ManualLogSource log;
    private readonly HardpointSet[] sets;
    private readonly List<string?>[] optionKeys;
    private readonly List<string>[] optionLabels;
    private readonly TMP_Dropdown[,] dropdowns;
    private readonly Button launcherButton;
    private readonly TMP_Text launcherLabel;
    private readonly Button resetButton;
    private readonly Button collapseButton;
    private readonly Button previousCradleButton;
    private readonly Button nextCradleButton;
    private readonly TMP_Text cradleLabel;
    private readonly GameObject panelObject;
    private CarrierFqLoadoutPlan plan;
    private int currentCradleIndex;
    private GameObject? root;

    internal bool Alive => root != null;
    internal bool Expanded => panelObject != null && panelObject.activeSelf;
    internal CarrierFqLoadoutPlan PlanCopy => plan.Copy();

    private FqCradleLoadoutPanel(Plugin plugin, CarrierFqLoadoutPlan plan, HardpointSet[] sets,
                                 TMP_Dropdown template, RectTransform canvasRect, RectTransform sourceRect,
                                 ManualLogSource log, bool expanded)
    {
        this.plugin = plugin;
        this.plan = plan.Copy();
        this.sets = sets;
        this.log = log;
        optionKeys = new List<string?>[sets.Length];
        optionLabels = new List<string>[sets.Length];
        dropdowns = new TMP_Dropdown[PhysicalRoundsPerCradle, sets.Length];

        root = new GameObject("LW_FQ_Loadout_Overlay", typeof(RectTransform), typeof(LayoutElement));
        root.transform.SetParent(canvasRect, false);
        root.transform.SetAsLastSibling();
        RectTransform overlayRect = (RectTransform)root.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        root.GetComponent<LayoutElement>().ignoreLayout = true;

        GetSourceBounds(overlayRect, sourceRect, out float sourceLeft, out float sourceTop);
        launcherButton = CreateButton("Launcher", overlayRect, template, "CRADLE LOADOUTS [+]", 224f, 38f);
        RectTransform launcherRect = (RectTransform)launcherButton.transform;
        launcherRect.anchorMin = Vector2.zero;
        launcherRect.anchorMax = Vector2.zero;
        launcherRect.pivot = new Vector2(0f, 0f);
        launcherRect.anchoredPosition = ClampLauncher(overlayRect, sourceLeft, sourceTop);
        launcherLabel = launcherButton.GetComponentInChildren<TMP_Text>(true);
        launcherButton.onClick.RemoveAllListeners();
        launcherButton.onClick.AddListener(ToggleExpanded);

        panelObject = new GameObject("Expanded Panel", typeof(RectTransform), typeof(VerticalLayoutGroup));
        panelObject.transform.SetParent(overlayRect, false);
        RectTransform panelRect = (RectTransform)panelObject.transform;
        panelRect.anchorMin = new Vector2(.5f, 0f);
        panelRect.anchorMax = new Vector2(.5f, 0f);
        panelRect.pivot = new Vector2(.5f, 0f);
        float width = Mathf.Clamp(104f + sets.Length * 202f, 680f, Mathf.Max(680f, canvasRect.rect.width * .7f));
        panelRect.sizeDelta = new Vector2(width, 360f);
        panelRect.anchoredPosition = new Vector2(-canvasRect.rect.width * .035f,
                                                  Mathf.Clamp(sourceTop + 54f, 160f,
                                                              Mathf.Max(160f, canvasRect.rect.height * .34f)));

        VerticalLayoutGroup panelLayout = panelObject.GetComponent<VerticalLayoutGroup>();
        panelLayout.padding = new RectOffset(14, 14, 10, 12);
        panelLayout.spacing = 5f;
        panelLayout.childControlWidth = true;
        panelLayout.childControlHeight = true;
        panelLayout.childForceExpandWidth = true;
        panelLayout.childForceExpandHeight = false;

        GameObject header = CreateRow("Header", panelObject.transform, 10f, 44f);
        CreateText("Title", header.transform, template, "CRADLE LOADOUTS", 18f, OriginalTextColor(template),
                   FontStyles.Bold, 0f, 1f).alignment = TextAlignmentOptions.MidlineLeft;
        resetButton = CreateButton("RESET", header.transform, template, "RESET", 80f, 34f);
        collapseButton = CreateButton("COLLAPSE", header.transform, template, "COLLAPSE", 106f, 34f);
        resetButton.onClick.RemoveAllListeners();
        resetButton.onClick.AddListener(Reset);
        collapseButton.onClick.RemoveAllListeners();
        collapseButton.onClick.AddListener(Collapse);

        GameObject cradlePager = CreateRow("Cradle Pager", panelObject.transform, 8f, 30f);
        previousCradleButton = CreateButton("Previous Cradle", cradlePager.transform, template, "<", 38f, 30f);
        cradleLabel = CreateText("Current Cradle", cradlePager.transform, template, "", 14f,
                                OriginalTextColor(template), FontStyles.Bold, 120f, 1f);
        cradleLabel.alignment = TextAlignmentOptions.Center;
        nextCradleButton = CreateButton("Next Cradle", cradlePager.transform, template, ">", 38f, 30f);
        previousCradleButton.onClick.RemoveAllListeners();
        previousCradleButton.onClick.AddListener(() => ChangeCradle(-1));
        nextCradleButton.onClick.RemoveAllListeners();
        nextCradleButton.onClick.AddListener(() => ChangeCradle(1));

        BuildOptions();
        GameObject headings = CreateRow("Hardpoint Headings", panelObject.transform, 8f, 30f);
        CreateText("FQ spacer", headings.transform, template, "", 10f, Color.white, FontStyles.Normal,
                   LabelWidth, 0f);
        for (int hp = 0; hp < sets.Length; hp++)
        {
            string label = string.IsNullOrWhiteSpace(sets[hp]?.name) ? "HP" + (hp + 1) : sets[hp].name;
            TMP_Text heading = CreateText("HP" + (hp + 1), headings.transform, template, label, 14f,
                                          OriginalTextColor(template), FontStyles.Bold, 120f, 1f);
            heading.alignment = TextAlignmentOptions.Center;
        }

        GameObject rows = new GameObject("FQ Rows", typeof(RectTransform), typeof(VerticalLayoutGroup),
                                         typeof(LayoutElement));
        rows.transform.SetParent(panelObject.transform, false);
        VerticalLayoutGroup rowsLayout = rows.GetComponent<VerticalLayoutGroup>();
        rowsLayout.spacing = 7f;
        rowsLayout.childControlWidth = true;
        rowsLayout.childControlHeight = true;
        rowsLayout.childForceExpandWidth = true;
        rowsLayout.childForceExpandHeight = false;
        rows.GetComponent<LayoutElement>().flexibleHeight = 1f;
        for (int row = 0; row < PhysicalRoundsPerCradle; row++)
            CreateLoadoutRow(row, template, rows.transform);

        RefreshSelections();
        SetExpanded(expanded);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
    }

    internal static FqCradleLoadoutPanel? TryCreate(Plugin plugin, CarrierFqLoadoutPlan plan,
                                                     List<WeaponSelector> weaponSelectors,
                                                     Transform backgroundTransform, ManualLogSource log,
                                                     bool expanded = false)
    {
        try
        {
            TMP_Dropdown? template = null;
            if (weaponSelectors != null)
                foreach (WeaponSelector selector in weaponSelectors)
                {
                    if (selector == null)
                        continue;
                    template = selector.GetComponentInChildren<TMP_Dropdown>(true);
                    if (template != null)
                        break;
                }
            Canvas? canvas = backgroundTransform != null ? backgroundTransform.GetComponentInParent<Canvas>() : null;
            RectTransform? canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            RectTransform? sourceRect = backgroundTransform as RectTransform;
            if (template == null || canvasRect == null || sourceRect == null ||
                !plugin.TryGetFqLoadoutDefinition(out AircraftDefinition definition))
            {
                log.LogError("[LoyalWingman] state=fq_loadout_panel_unavailable reason=source");
                return null;
            }
            HardpointSet[]? sets = definition.unitPrefab.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
            if (sets == null || sets.Length == 0 || sets.Length != plan.HardpointCount)
            {
                log.LogError("[LoyalWingman] state=fq_loadout_panel_unavailable reason=hardpoints");
                return null;
            }
            if (plan.CradleCount <= 0 || plan.PhysicalRoundsPerCradle != PhysicalRoundsPerCradle)
            {
                log.LogError("[LoyalWingman] state=fq_loadout_panel_unavailable reason=cradles");
                return null;
            }
            return new FqCradleLoadoutPanel(plugin, plan, sets, template, canvasRect, sourceRect, log, expanded);
        }
        catch (Exception e)
        {
            log.LogError("[LoyalWingman] state=fq_loadout_panel_unavailable reason=" + e.GetType().Name);
            return null;
        }
    }

    private void BuildOptions()
    {
        for (int hp = 0; hp < sets.Length; hp++)
        {
            var legal = new List<(string label, string? key)>();
            if (sets[hp]?.weaponOptions != null)
                foreach (WeaponMount mount in sets[hp].weaponOptions)
                    if (mount != null && !mount.NotAllowed(MissionManager.AllowEventContent))
                    {
                        string label = string.IsNullOrWhiteSpace(mount.mountName) ? mount.jsonKey : mount.mountName;
                        legal.Add((label ?? "", mount.jsonKey));
                    }
            legal.Sort((a, b) =>
            {
                int byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.label, b.label);
                return byLabel != 0 ? byLabel : StringComparer.Ordinal.Compare(a.key, b.key);
            });
            optionKeys[hp] = new List<string?> { null };
            optionLabels[hp] = new List<string> { "EMPTY" };
            foreach (var option in legal)
            {
                optionKeys[hp].Add(option.key);
                optionLabels[hp].Add(option.label);
            }
        }
    }

    private void CreateLoadoutRow(int row, TMP_Dropdown template, Transform parent)
    {
        GameObject rowObject = CreateRow("FQ" + (row + 1), parent, 8f, 46f);
        TMP_Text fqLabel = CreateText("FQ label", rowObject.transform, template, "ROUND " + (row + 1), 15f,
                                      OriginalTextColor(template), FontStyles.Bold, LabelWidth, 0f);
        fqLabel.alignment = TextAlignmentOptions.MidlineLeft;
        for (int hp = 0; hp < sets.Length; hp++)
        {
            TMP_Dropdown dropdown = CreateDropdown("FQ" + (row + 1) + "_HP" + (hp + 1),
                                                   rowObject.transform, template, optionLabels[hp]);

            int rowIndex = row;
            int hpIndex = hp;
            dropdown.onValueChanged.AddListener(value => Select(rowIndex, hpIndex, value));
            dropdowns[row, hp] = dropdown;
        }
    }

    private static TMP_Dropdown CreateDropdown(string name, Transform parent, TMP_Dropdown styleSource,
                                                List<string> labels)
    {
        GameObject rootObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        rootObject.SetActive(false);
        rootObject.transform.SetParent(parent, false);
        RectTransform rootRect = (RectTransform)rootObject.transform;
        rootRect.sizeDelta = new Vector2(194f, DropdownHeight);
        Image rootImage = rootObject.GetComponent<Image>();
        Image sourceImage = styleSource.targetGraphic as Image ?? styleSource.GetComponent<Image>();
        if (sourceImage != null)
        {
            rootImage.sprite = sourceImage.sprite;
            rootImage.type = sourceImage.type;
        }
        rootImage.color = new Color(.055f, .085f, .07f, .96f);
        rootImage.raycastTarget = true;

        TMP_Dropdown dropdown = rootObject.AddComponent<TMP_Dropdown>();
        dropdown.targetGraphic = rootImage;
        dropdown.transition = Selectable.Transition.ColorTint;
        dropdown.colors = new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(.78f, 1f, .8f, 1f),
            pressedColor = new Color(.56f, .82f, .6f, 1f),
            selectedColor = new Color(.78f, 1f, .8f, 1f),
            disabledColor = new Color(.45f, .48f, .45f, .55f),
            colorMultiplier = 1f,
            fadeDuration = .08f
        };
        dropdown.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        dropdown.alphaFadeSpeed = .08f;

        GameObject captionObject = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
        captionObject.transform.SetParent(rootRect, false);
        RectTransform captionRect = (RectTransform)captionObject.transform;
        captionRect.anchorMin = Vector2.zero;
        captionRect.anchorMax = Vector2.one;
        captionRect.offsetMin = new Vector2(10f, 2f);
        captionRect.offsetMax = new Vector2(-30f, -2f);
        TextMeshProUGUI caption = captionObject.GetComponent<TextMeshProUGUI>();
        ConfigureDropdownText(caption, styleSource.captionText, 15f, TextAlignmentOptions.MidlineLeft);
        caption.overflowMode = TextOverflowModes.Ellipsis;

        GameObject arrowObject = new GameObject("Arrow", typeof(RectTransform), typeof(TextMeshProUGUI));
        arrowObject.transform.SetParent(rootRect, false);
        RectTransform arrowRect = (RectTransform)arrowObject.transform;
        arrowRect.anchorMin = new Vector2(1f, 0f);
        arrowRect.anchorMax = Vector2.one;
        arrowRect.pivot = new Vector2(1f, .5f);
        arrowRect.sizeDelta = new Vector2(26f, 0f);
        arrowRect.anchoredPosition = new Vector2(-3f, 0f);
        TextMeshProUGUI arrow = arrowObject.GetComponent<TextMeshProUGUI>();
        ConfigureDropdownText(arrow, styleSource.captionText, 14f, TextAlignmentOptions.Center);
        arrow.text = "v";

        GameObject templateObject = new GameObject("Template", typeof(RectTransform), typeof(Image),
                                                   typeof(ScrollRect));
        templateObject.transform.SetParent(rootRect, false);
        RectTransform templateRect = (RectTransform)templateObject.transform;
        templateRect.anchorMin = Vector2.zero;
        templateRect.anchorMax = new Vector2(1f, 0f);
        templateRect.pivot = new Vector2(.5f, 1f);
        templateRect.anchoredPosition = new Vector2(0f, -2f);
        templateRect.sizeDelta = new Vector2(0f, Mathf.Clamp(PopupItemHeight * Mathf.Min(labels.Count, 7) + 8f,
                                                             110f, 232f));
        Image templateImage = templateObject.GetComponent<Image>();
        templateImage.color = new Color(.018f, .03f, .024f, .97f);
        templateImage.raycastTarget = true;

        GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image),
                                                   typeof(RectMask2D));
        viewportObject.transform.SetParent(templateRect, false);
        RectTransform viewportRect = (RectTransform)viewportObject.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(2f, 2f);
        viewportRect.offsetMax = new Vector2(-18f, -2f);
        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, .01f);
        viewportImage.raycastTarget = true;

        GameObject contentObject = new GameObject("Content", typeof(RectTransform));
        contentObject.transform.SetParent(viewportRect, false);
        RectTransform contentRect = (RectTransform)contentObject.transform;
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = Vector2.one;
        contentRect.pivot = new Vector2(.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, PopupItemHeight);

        GameObject itemObject = new GameObject("Item", typeof(RectTransform), typeof(Image), typeof(Toggle));
        itemObject.transform.SetParent(contentRect, false);
        RectTransform itemRect = (RectTransform)itemObject.transform;
        itemRect.anchorMin = new Vector2(0f, .5f);
        itemRect.anchorMax = new Vector2(1f, .5f);
        itemRect.pivot = new Vector2(.5f, .5f);
        itemRect.anchoredPosition = Vector2.zero;
        itemRect.sizeDelta = new Vector2(0f, PopupItemHeight);
        Image itemImage = itemObject.GetComponent<Image>();
        itemImage.color = Color.white;
        itemImage.raycastTarget = true;
        Toggle itemToggle = itemObject.GetComponent<Toggle>();
        itemToggle.targetGraphic = itemImage;
        itemToggle.graphic = null;
        itemToggle.transition = Selectable.Transition.ColorTint;
        itemToggle.colors = new ColorBlock
        {
            normalColor = new Color(.058f, .125f, .072f, .96f),
            highlightedColor = new Color(.38f, .41f, .38f, 1f),
            pressedColor = new Color(.48f, .51f, .48f, 1f),
            selectedColor = new Color(.2f, .3f, .21f, 1f),
            disabledColor = new Color(.055f, .07f, .058f, .45f),
            colorMultiplier = 1f,
            fadeDuration = .06f
        };
        itemToggle.navigation = new Navigation { mode = Navigation.Mode.Automatic };

        GameObject itemLabelObject = new GameObject("Item Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        itemLabelObject.transform.SetParent(itemRect, false);
        RectTransform itemLabelRect = (RectTransform)itemLabelObject.transform;
        itemLabelRect.anchorMin = Vector2.zero;
        itemLabelRect.anchorMax = Vector2.one;
        itemLabelRect.offsetMin = new Vector2(9f, 1f);
        itemLabelRect.offsetMax = new Vector2(-6f, -1f);
        TextMeshProUGUI itemLabel = itemLabelObject.GetComponent<TextMeshProUGUI>();
        ConfigureDropdownText(itemLabel, styleSource.captionText, 14.5f, TextAlignmentOptions.MidlineLeft);
        itemLabel.overflowMode = TextOverflowModes.Ellipsis;

        GameObject scrollbarObject = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image),
                                                    typeof(Scrollbar));
        scrollbarObject.transform.SetParent(templateRect, false);
        RectTransform scrollbarRect = (RectTransform)scrollbarObject.transform;
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = Vector2.one;
        scrollbarRect.pivot = new Vector2(1f, .5f);
        scrollbarRect.sizeDelta = new Vector2(16f, 0f);
        scrollbarRect.anchoredPosition = Vector2.zero;
        Image scrollbarImage = scrollbarObject.GetComponent<Image>();
        scrollbarImage.color = new Color(.025f, .055f, .035f, .95f);
        scrollbarImage.raycastTarget = true;

        GameObject slidingObject = new GameObject("Sliding Area", typeof(RectTransform));
        slidingObject.transform.SetParent(scrollbarRect, false);
        RectTransform slidingRect = (RectTransform)slidingObject.transform;
        slidingRect.anchorMin = Vector2.zero;
        slidingRect.anchorMax = Vector2.one;
        slidingRect.offsetMin = new Vector2(2f, 2f);
        slidingRect.offsetMax = new Vector2(-2f, -2f);

        GameObject handleObject = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleObject.transform.SetParent(slidingRect, false);
        RectTransform handleRect = (RectTransform)handleObject.transform;
        handleRect.anchorMin = Vector2.zero;
        handleRect.anchorMax = Vector2.one;
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;
        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(.48f, .78f, .5f, .95f);
        handleImage.raycastTarget = true;

        Scrollbar scrollbar = scrollbarObject.GetComponent<Scrollbar>();
        scrollbar.handleRect = handleRect;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.transition = Selectable.Transition.ColorTint;
        scrollbar.colors = dropdown.colors;
        scrollbar.navigation = new Navigation { mode = Navigation.Mode.Automatic };

        ScrollRect scroll = templateObject.GetComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.viewport = viewportRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.scrollSensitivity = 22f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scroll.verticalScrollbarSpacing = 2f;

        templateObject.SetActive(false);
        dropdown.template = templateRect;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.onValueChanged.RemoveAllListeners();
        dropdown.ClearOptions();
        foreach (string label in labels)
            dropdown.options.Add(new TMP_Dropdown.OptionData(label));
        dropdown.SetValueWithoutNotify(0);
        dropdown.RefreshShownValue();

        LayoutElement element = rootObject.GetComponent<LayoutElement>();
        element.ignoreLayout = false;
        element.minWidth = 120f;
        element.preferredWidth = 194f;
        element.flexibleWidth = 1f;
        element.minHeight = DropdownHeight;
        element.preferredHeight = DropdownHeight;
        rootObject.SetActive(true);
        return dropdown;
    }

    private static void ConfigureDropdownText(TextMeshProUGUI text, TMP_Text source, float size,
                                              TextAlignmentOptions alignment)
    {
        text.font = source != null && source.font != null ? source.font : TMP_Settings.defaultFontAsset;
        if (source != null && source.font != null && source.fontSharedMaterial != null)
            text.fontSharedMaterial = source.fontSharedMaterial;
        text.fontSize = size;
        text.color = source != null ? source.color : new Color(.72f, .92f, .72f, 1f);
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.maxVisibleLines = 1;
        text.raycastTarget = false;
    }

    private void HideDropdowns()
    {
        foreach (TMP_Dropdown dropdown in dropdowns)
            if (dropdown != null)
                dropdown.Hide();
    }

    private void Select(int row, int hp, int value)
    {
        if (value < 0 || value >= optionKeys[hp].Count ||
            !plan.TrySet(currentCradleIndex, row, hp, optionKeys[hp][value]))
            return;
        plugin.SetActiveFqLoadoutPlan(plan);
    }

    private void Reset()
    {
        if (!plugin.TryCreateDefaultFqLoadoutPlan(plan.CradleCount, out CarrierFqLoadoutPlan? reset,
                                                  out string reason) || reset == null)
        {
            log.LogError("[LoyalWingman] state=fq_loadout_reset_failed reason=" + reason);
            return;
        }
        plan = reset;
        plugin.SetActiveFqLoadoutPlan(plan);
        RefreshSelections();
    }

    private void RefreshSelections()
    {
        cradleLabel.text = "CRADLE " + (currentCradleIndex + 1);
        previousCradleButton.interactable = currentCradleIndex > 0;
        nextCradleButton.interactable = currentCradleIndex < plan.CradleCount - 1;
        for (int row = 0; row < PhysicalRoundsPerCradle; row++)
        {
            if (!plan.TryGetRow(currentCradleIndex, row, out string?[] selected))
                continue;
            for (int hp = 0; hp < sets.Length; hp++)
            {
                int index = selected[hp] == null ? 0 : optionKeys[hp].FindIndex(k => k == selected[hp]);
                if (index < 0)
                    index = 0;
                dropdowns[row, hp].SetValueWithoutNotify(index);
                dropdowns[row, hp].RefreshShownValue();
            }
        }
    }

    private void ChangeCradle(int direction)
    {
        int next = Mathf.Clamp(currentCradleIndex + direction, 0, plan.CradleCount - 1);
        if (next == currentCradleIndex)
            return;
        HideDropdowns();
        currentCradleIndex = next;
        RefreshSelections();
    }

    private void ToggleExpanded() => SetExpanded(!Expanded);

    private void Collapse() => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        if (panelObject == null)
            return;
        if (!expanded)
            HideDropdowns();
        panelObject.SetActive(expanded);
        launcherLabel.text = expanded ? "CRADLE LOADOUTS [-]" : "CRADLE LOADOUTS [+]";
    }

    private static GameObject CreateRow(string name, Transform parent, float spacing, float height)
    {
        GameObject row = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup),
                                        typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        LayoutElement element = row.GetComponent<LayoutElement>();
        element.minHeight = height;
        element.preferredHeight = height;
        return row;
    }

    private static TMP_Text CreateText(string name, Transform parent, TMP_Dropdown template, string value,
                                       float size, Color color, FontStyles style, float minWidth, float flexibleWidth)
    {
        GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI),
                                               typeof(LayoutElement));
        gameObject.transform.SetParent(parent, false);
        TextMeshProUGUI text = gameObject.GetComponent<TextMeshProUGUI>();
        TMP_Text source = template.captionText;
        if (source != null)
        {
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
        }
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.fontStyle = style;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.maxVisibleLines = 1;
        text.raycastTarget = false;
        LayoutElement element = gameObject.GetComponent<LayoutElement>();
        element.minWidth = minWidth;
        element.flexibleWidth = flexibleWidth;
        return text;
    }

    private static Button CreateButton(string name, Transform parent, TMP_Dropdown template, string value,
                                       float width, float height)
    {
        GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button),
                                               typeof(LayoutElement));
        gameObject.transform.SetParent(parent, false);
        Image sourceImage = template.targetGraphic as Image ?? template.GetComponent<Image>();
        Image image = gameObject.GetComponent<Image>();
        if (sourceImage != null)
        {
            image.sprite = sourceImage.sprite;
            image.type = sourceImage.type;
            image.color = sourceImage.color;
        }
        Button button = gameObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.colors = template.colors;
        RectTransform rect = (RectTransform)gameObject.transform;
        rect.sizeDelta = new Vector2(width, height);
        LayoutElement element = gameObject.GetComponent<LayoutElement>();
        element.minWidth = width;
        element.preferredWidth = width;
        element.minHeight = height;
        element.preferredHeight = height;
        TMP_Text label = CreateText("Label", gameObject.transform, template, value, 14f,
                                    OriginalTextColor(template), FontStyles.Bold, 0f, 1f);
        RectTransform labelRect = (RectTransform)label.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(9f, 0f);
        labelRect.offsetMax = new Vector2(-9f, 0f);
        label.alignment = TextAlignmentOptions.Center;
        return button;
    }

    private static Color OriginalTextColor(TMP_Dropdown template) =>
        template.captionText != null ? template.captionText.color : new Color(.75f, .9f, .72f, 1f);

    private static void GetSourceBounds(RectTransform overlay, RectTransform source, out float left, out float top)
    {
        Vector3[] corners = new Vector3[4];
        source.GetWorldCorners(corners);
        Vector3 topLeft = overlay.InverseTransformPoint(corners[1]);
        left = topLeft.x + overlay.rect.width * overlay.pivot.x;
        top = topLeft.y + overlay.rect.height * overlay.pivot.y;
    }

    private static Vector2 ClampLauncher(RectTransform overlay, float sourceLeft, float sourceTop)
    {
        float x = Mathf.Clamp(sourceLeft + 8f, 18f, overlay.rect.width - 242f);
        float y = Mathf.Clamp(sourceTop + 10f, 18f, overlay.rect.height - 54f);
        return new Vector2(x, y);
    }

    public void Dispose()
    {
        if (EventSystem.current != null && root != null)
        {
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(root.transform))
                EventSystem.current.SetSelectedGameObject(null);
        }
        if (launcherButton != null)
            launcherButton.onClick.RemoveAllListeners();
        if (resetButton != null)
            resetButton.onClick.RemoveAllListeners();
        if (collapseButton != null)
            collapseButton.onClick.RemoveAllListeners();
        if (previousCradleButton != null)
            previousCradleButton.onClick.RemoveAllListeners();
        if (nextCradleButton != null)
            nextCradleButton.onClick.RemoveAllListeners();
        HideDropdowns();
        foreach (TMP_Dropdown dropdown in dropdowns)
            if (dropdown != null)
            {
                dropdown.onValueChanged.RemoveAllListeners();
            }
        if (root != null)
            UnityEngine.Object.Destroy(root);
        root = null;
    }
}

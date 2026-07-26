using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoyalWingman;

internal sealed class RecoveryGuidanceVisual
{
    private enum GuidanceState { Approach, Extending, Waiting, Ready }

    private const int CaptureSegments = 72;
    private const int PreparationSegments = 96;
    private static readonly Color approachColor = new Color(0.25f, 0.88f, 1f, 0.9f);
    private static readonly Color extendingColor = new Color(1f, 0.72f, 0.2f, 0.95f);
    private static readonly Color readyColor = new Color(0.35f, 1f, 0.58f, 1f);
    private static readonly Color shadowColor = new Color(0.01f, 0.035f, 0.05f, 0.78f);
    private static readonly Color preparationColor = new Color(0.3f, 0.78f, 0.9f, 0.24f);

    private GameObject? root;
    private Transform? rootTransform, labelTransform;
    private Material? lineMaterial, labelMaterial;
    private LineRenderer? captureXy, captureXz, captureYz, preparation, centerDiamond, centerHorizontal,
                          centerVertical;
    private LineRenderer? captureXyShadow, captureXzShadow, captureYzShadow, preparationShadow,
                          centerDiamondShadow, centerHorizontalShadow, centerVerticalShadow;
    private TextMeshPro? label;
    private Camera? camera;
    private GuidanceState displayedState;
    private int displayedMeters = -1;
    private bool hasDisplayedState;

    internal void Tick(Aircraft drone, DroneCarrierPortWeapon port)
    {
        Transform? pose = port.AnimatedRecoveryPose;
        if (pose == null) { Hide(); return; }
        EnsureCreated();
        if (root == null || rootTransform == null) return;
        if (!root.activeSelf) root.SetActive(true);

        Vector3 hook = pose.position;
        Vector3 droneRoot = drone.transform.position;
        rootTransform.SetPositionAndRotation(hook, pose.rotation);

        float distance = Vector3.Distance(droneRoot, hook);
        GuidanceState state = ResolveState(distance, port.AnimatedMotion);
        int meters = Mathf.Max(0, Mathf.RoundToInt(distance));
        if (!hasDisplayedState || state != displayedState)
        {
            ApplyState(state);
            displayedState = state;
            hasDisplayedState = true;
        }
        TextMeshPro statusLabel = label!;
        if (meters != displayedMeters || statusLabel.text.Length == 0)
        {
            statusLabel.text = "RECOVERY " + meters + " m\n" + StateText(state);
            displayedMeters = meters;
        }

        if (camera == null || !camera.isActiveAndEnabled)
        {
            CameraStateManager cameraState = SceneSingleton<CameraStateManager>.i;
            camera = cameraState == null ? null : cameraState.mainCamera;
        }
        if (camera != null && labelTransform != null)
        {
            Transform cameraTransform = camera.transform;
            labelTransform.position = hook + cameraTransform.up * 1.25f;
            labelTransform.rotation = cameraTransform.rotation;
        }
    }

    internal void Hide()
    {
        if (root != null && root.activeSelf) root.SetActive(false);
        displayedMeters = -1;
        hasDisplayedState = false;
    }

    internal void Dispose()
    {
        if (root != null) Object.Destroy(root);
        if (lineMaterial != null) Object.Destroy(lineMaterial);
        if (labelMaterial != null) Object.Destroy(labelMaterial);
        root = null; rootTransform = labelTransform = null;
        lineMaterial = labelMaterial = null; label = null; camera = null;
        displayedMeters = -1; hasDisplayedState = false;
    }

    private void EnsureCreated()
    {
        if (root != null) return;
        Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default") ??
                        Shader.Find("UI/Default");
        if (shader == null) return;
        lineMaterial = new Material(shader) { name = "LW Recovery Guidance", renderQueue = 4000 };
        if (lineMaterial.HasProperty("_Color")) lineMaterial.SetColor("_Color", Color.white);
        if (lineMaterial.HasProperty("_SrcBlend")) lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        if (lineMaterial.HasProperty("_DstBlend")) lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        if (lineMaterial.HasProperty("_Cull")) lineMaterial.SetInt("_Cull", (int)CullMode.Off);
        if (lineMaterial.HasProperty("_ZTest")) lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
        if (lineMaterial.HasProperty("_ZWrite")) lineMaterial.SetInt("_ZWrite", 0);

        root = new GameObject("LW Recovery Guidance");
        rootTransform = root.transform;
        captureXyShadow = CreateCircle("Capture XY Back", RecoveryGuidanceVisual.CaptureSegments,
            DroneCarrierPortLogic.AnimatedCaptureRadius, 0.16f, shadowColor, CirclePlane.XY);
        captureXy = CreateCircle("Capture XY", CaptureSegments, DroneCarrierPortLogic.AnimatedCaptureRadius,
            0.075f, approachColor, CirclePlane.XY);
        captureXzShadow = CreateCircle("Capture XZ Back", CaptureSegments,
            DroneCarrierPortLogic.AnimatedCaptureRadius, 0.16f, shadowColor, CirclePlane.XZ);
        captureXz = CreateCircle("Capture XZ", CaptureSegments, DroneCarrierPortLogic.AnimatedCaptureRadius,
            0.075f, approachColor, CirclePlane.XZ);
        captureYzShadow = CreateCircle("Capture YZ Back", CaptureSegments,
            DroneCarrierPortLogic.AnimatedCaptureRadius, 0.16f, shadowColor, CirclePlane.YZ);
        captureYz = CreateCircle("Capture YZ", CaptureSegments, DroneCarrierPortLogic.AnimatedCaptureRadius,
            0.075f, approachColor, CirclePlane.YZ);
        preparationShadow = CreateCircle("Preparation Back", PreparationSegments,
            DroneCarrierPortLogic.AnimatedCapturePreparationRadius, 0.105f,
            new Color(shadowColor.r, shadowColor.g, shadowColor.b, 0.3f), CirclePlane.XY);
        preparation = CreateCircle("Preparation", PreparationSegments,
            DroneCarrierPortLogic.AnimatedCapturePreparationRadius, 0.045f, preparationColor, CirclePlane.XY);

        centerDiamondShadow = CreatePolyline("Center Diamond Back", 0.18f, shadowColor, true,
            new Vector3(0f, 0.72f, 0f), new Vector3(0.72f, 0f, 0f), new Vector3(0f, -0.72f, 0f),
            new Vector3(-0.72f, 0f, 0f));
        centerDiamond = CreatePolyline("Center Diamond", 0.095f, Color.white, true,
            new Vector3(0f, 0.72f, 0f), new Vector3(0.72f, 0f, 0f), new Vector3(0f, -0.72f, 0f),
            new Vector3(-0.72f, 0f, 0f));
        centerHorizontalShadow = CreatePolyline("Center Horizontal Back", 0.2f, shadowColor, false,
            new Vector3(-0.42f, 0f, 0f), new Vector3(0.42f, 0f, 0f));
        centerHorizontal = CreatePolyline("Center Horizontal", 0.11f, Color.white, false,
            new Vector3(-0.42f, 0f, 0f), new Vector3(0.42f, 0f, 0f));
        centerVerticalShadow = CreatePolyline("Center Vertical Back", 0.2f, shadowColor, false,
            new Vector3(0f, -0.42f, 0f), new Vector3(0f, 0.42f, 0f));
        centerVertical = CreatePolyline("Center Vertical", 0.11f, Color.white, false,
            new Vector3(0f, -0.42f, 0f), new Vector3(0f, 0.42f, 0f));
        CreateLabel();
    }

    private enum CirclePlane { XY, XZ, YZ }

    private LineRenderer CreateCircle(string name, int segments, float radius, float width, Color color,
                                      CirclePlane plane)
    {
        LineRenderer line = CreateLine(name, width, color, false);
        line.loop = true;
        line.positionCount = segments;
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            float a = Mathf.Cos(angle) * radius, b = Mathf.Sin(angle) * radius;
            line.SetPosition(i, plane == CirclePlane.XY ? new Vector3(a, b, 0f) :
                                plane == CirclePlane.XZ ? new Vector3(a, 0f, b) : new Vector3(0f, a, b));
        }
        return line;
    }

    private LineRenderer CreatePolyline(string name, float width, Color color, bool loop, params Vector3[] points)
    {
        LineRenderer line = CreateLine(name, width, color, false);
        line.loop = loop;
        line.positionCount = points.Length;
        line.SetPositions(points);
        return line;
    }

    private LineRenderer CreateLine(string name, float width, Color color, bool worldSpace)
    {
        GameObject gameObject = new GameObject(name);
        gameObject.transform.SetParent(rootTransform, false);
        LineRenderer line = gameObject.AddComponent<LineRenderer>();
        line.sharedMaterial = lineMaterial;
        line.useWorldSpace = worldSpace;
        line.alignment = LineAlignment.View;
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = color;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sortingOrder = 32000;
        return line;
    }

    private void CreateLabel()
    {
        GameObject labelObject = new GameObject("Recovery Status", typeof(RectTransform), typeof(TextMeshPro));
        labelObject.transform.SetParent(rootTransform, false);
        labelTransform = labelObject.transform;
        labelTransform.localScale = Vector3.one * 0.25f;
        RectTransform rect = (RectTransform)labelTransform;
        rect.sizeDelta = new Vector2(9f, 2.4f);
        label = labelObject.GetComponent<TextMeshPro>();
        if (TMP_Settings.defaultFontAsset != null) label.font = TMP_Settings.defaultFontAsset;
        label.fontSize = 2f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.color = approachColor;
        label.sortingOrder = 32000;
        labelMaterial = label.fontMaterial;
        if (labelMaterial.HasProperty("_ZTest")) labelMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
        label.outlineColor = new Color32(2, 8, 12, 230);
        label.outlineWidth = 0.18f;
    }

    private void ApplyState(GuidanceState state)
    {
        Color color = state == GuidanceState.Ready ? readyColor :
                      state == GuidanceState.Extending || state == GuidanceState.Waiting ? extendingColor : approachColor;
        SetColor(captureXy!, color); SetColor(captureXz!, color); SetColor(captureYz!, color);
        label!.color = color;
        displayedMeters = -1;
    }

    private static GuidanceState ResolveState(float distance, AnimatedPortMotion motion)
    {
        if (distance > DroneCarrierPortLogic.AnimatedCapturePreparationRadius) return GuidanceState.Approach;
        if (motion == AnimatedPortMotion.ExtendingCapture) return GuidanceState.Extending;
        if (motion == AnimatedPortMotion.WaitingCapture)
            return distance <= DroneCarrierPortLogic.AnimatedCaptureRadius ? GuidanceState.Ready : GuidanceState.Waiting;
        if (motion == AnimatedPortMotion.LockingCapture) return GuidanceState.Ready;
        return GuidanceState.Waiting;
    }

    private static string StateText(GuidanceState state) => state == GuidanceState.Approach ? "APPROACH" :
        state == GuidanceState.Extending ? "EXTENDING" : state == GuidanceState.Ready ? "CAPTURE READY" : "WAITING";

    private static void SetColor(LineRenderer line, Color color) => line.startColor = line.endColor = color;
}

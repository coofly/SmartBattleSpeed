// 用途：创建智能战斗倍速设置面板，提供主角/NPC 倍速选择和保存入口。

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SmartBattleSpeedMod;

// 智能战斗倍速设置面板，使用独立 Overlay Canvas 并跟随设置按钮定位。
internal sealed class SmartBattleSpeedSettingsUi
{
    private const float PanelWidth = 260f;
    private const float PanelHeight = 232f;
    private const float AnchorGap = 10f;
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;
    private const float ReferenceHalfHeight = ReferenceHeight * 0.5f;
    private const float MatchWidthOrHeight = 0.5f;
    private static readonly int[] SpeedOptions = { 1, 2, 3, 5, 10 };

    private readonly SmartBattleSpeedSettings _settings;
    private readonly Action _saveClicked;
    private readonly GameObject _root;
    private readonly RectTransform _panel;
    private readonly Font _font;
    private readonly Action<string>? _log;
    private readonly List<Button> _mainSpeedButtons = new();
    private readonly List<Button> _npcSpeedButtons = new();
    private readonly List<Button> _npcAttackMainCharacterSpeedButtons = new();
    private RectTransform? _anchorButton;
    private bool _visible;
    private int _anchorDiagnosticLogs;

    private readonly Color _panelColor = new(0.10f, 0.08f, 0.06f, 0.96f);
    private readonly Color _headerColor = new(0.42f, 0.22f, 0.10f, 0.98f);
    private readonly Color _buttonColor = new(0.36f, 0.22f, 0.12f, 1f);
    private readonly Color _selectedColor = new(0.72f, 0.58f, 0.22f, 1f);

    // 创建设置面板。
    public SmartBattleSpeedSettingsUi(RectTransform anchorButton, Font font, SmartBattleSpeedSettings settings, Action saveClicked, Action<string>? log = null)
    {
        _settings = settings;
        _saveClicked = saveClicked;
        _font = font;
        _log = log;
        _anchorButton = anchorButton;

        EnsureEventSystem();

        _root = new GameObject("SmartBattleSpeedSettingsOverlay");
        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;

        CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _root.AddComponent<GraphicRaycaster>();
        _log?.Invoke("设置界面 Overlay 已创建：ScreenSpaceOverlay，sortingOrder=32000，referenceResolution=1920x1080。");

        _panel = CreateRect("SmartBattleSpeedSettingsPanel", _root.transform);
        _panel.anchorMin = new Vector2(0f, 1f);
        _panel.anchorMax = new Vector2(0f, 1f);
        _panel.pivot = new Vector2(0f, 1f);
        _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);

        Image image = _panel.gameObject.AddComponent<Image>();
        image.color = _panelColor;
        image.raycastTarget = true;

        CreateHeader();
        CreateSpeedRow("主角倍速：", 48f, SpeedTarget.MainCharacter);
        CreateSpeedRow("NPC倍速：", 90f, SpeedTarget.Npc);
        CreateSpeedRow("攻击主角：", 132f, SpeedTarget.NpcAttackMainCharacter);
        CreateSaveButton();

        UpdateAnchor(anchorButton);
        SetVisible(false);
    }

    // 返回设置面板当前是否显示。
    public bool IsVisible => _visible;

    // 切换面板显示。
    public bool ToggleVisible()
    {
        SetVisible(!_visible);
        return _visible;
    }

    // 设置面板显示状态。
    public void SetVisible(bool visible)
    {
        _visible = visible;
        _root.SetActive(visible);
        if (visible && _anchorButton != null)
        {
            UpdateAnchor(_anchorButton);
            RefreshSelectionVisuals();
            BringToFront();
        }
    }

    // 按设置按钮位置刷新面板位置。
    public void UpdateAnchor(RectTransform anchorButton)
    {
        _anchorButton = anchorButton;
        BringToFront();
        Vector3 bottomLeftWorld = ResolveAnchorBottomLeftWorld(anchorButton);
        Camera? anchorCamera = ResolveEventCamera(anchorButton);
        Vector2 screenPoint = ResolveAnchorScreenPoint(anchorCamera, bottomLeftWorld, out string coordinateSource);
        float scale = ResolveOverlayScale();
        float topOffset = Screen.height - screenPoint.y + AnchorGap;
        _panel.anchoredPosition = new Vector2(screenPoint.x / scale, -topOffset / scale);

        LogAnchorDiagnostics(anchorButton, bottomLeftWorld, anchorCamera, coordinateSource, screenPoint, scale);
    }

    // 获取锚点按钮左下角世界坐标；避免 IL2CPP 下 GetWorldCorners(Vector3[]) 数组回写失效。
    private static Vector3 ResolveAnchorBottomLeftWorld(RectTransform anchorButton)
    {
        Rect rect = anchorButton.rect;
        return anchorButton.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f));
    }

    // 销毁设置面板。
    public void Destroy()
    {
        UnityEngine.Object.Destroy(_root);
    }

    // 把设置面板提到同父级最上层。
    private void BringToFront()
    {
        _root.transform.SetAsLastSibling();
    }

    // 将设置按钮左下角转换为真实屏幕像素；本目标战斗 UI 的世界角点常是 0..1 归一化坐标。
    private static Vector2 ResolveAnchorScreenPoint(Camera? anchorCamera, Vector3 worldPoint, out string coordinateSource)
    {
        if (IsGameUiCameraPoint(anchorCamera, worldPoint))
        {
            coordinateSource = "UICamera中心坐标";
            float scale = ResolveOverlayScale();
            float referenceX = ReferenceWidth * 0.5f + worldPoint.x * ReferenceHalfHeight;
            float referenceY = ReferenceHeight * 0.5f + worldPoint.y * ReferenceHalfHeight;
            return new Vector2(referenceX * scale, referenceY * scale);
        }

        if (anchorCamera == null && IsNormalizedScreenPoint(worldPoint))
        {
            coordinateSource = "归一化屏幕坐标";
            return new Vector2(worldPoint.x * Screen.width, worldPoint.y * Screen.height);
        }

        coordinateSource = anchorCamera == null ? "Overlay 世界坐标" : "相机世界坐标";
        return RectTransformUtility.WorldToScreenPoint(anchorCamera, worldPoint);
    }

    // 判断世界角点是否实际表示 0..1 的屏幕归一化坐标。
    private static bool IsNormalizedScreenPoint(Vector3 worldPoint)
    {
        return worldPoint.x >= 0f
            && worldPoint.x <= 1f
            && worldPoint.y >= 0f
            && worldPoint.y <= 1f
            && Math.Abs(worldPoint.z) < 0.001f;
    }

    // 判断坐标是否来自本目标战斗 UICamera：中心为原点，半高约等于 1 个世界单位。
    private static bool IsGameUiCameraPoint(Camera? anchorCamera, Vector3 worldPoint)
    {
        return anchorCamera != null
            && Math.Abs(worldPoint.x) <= 2.5f
            && Math.Abs(worldPoint.y) <= 1.5f
            && Math.Abs(worldPoint.z) < 0.001f;
    }

    // 复刻 CanvasScaler 的 ScaleWithScreenSize 计算，直接把屏幕像素换成参考分辨率坐标。
    private static float ResolveOverlayScale()
    {
        float widthScale = Mathf.Max(0.0001f, Screen.width / ReferenceWidth);
        float heightScale = Mathf.Max(0.0001f, Screen.height / ReferenceHeight);
        float logWidth = Mathf.Log(widthScale, 2f);
        float logHeight = Mathf.Log(heightScale, 2f);
        return Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, MatchWidthOrHeight));
    }

    // 解析控件所属 Canvas 使用的事件相机，Overlay Canvas 返回 null。
    private static Camera? ResolveEventCamera(RectTransform rect)
    {
        Canvas? canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            return null;
        }

        return canvas.worldCamera;
    }

    // 只输出少量锚点诊断，帮助现场确认坐标链，不刷屏。
    private void LogAnchorDiagnostics(
        RectTransform anchorButton,
        Vector3 bottomLeftWorld,
        Camera? anchorCamera,
        string coordinateSource,
        Vector2 screenPoint,
        float scale)
    {
        if (_anchorDiagnosticLogs >= 3)
        {
            return;
        }

        _anchorDiagnosticLogs++;
        Vector3 localPosition = anchorButton.localPosition;
        Vector2 anchoredPosition = _panel.anchoredPosition;
        _log?.Invoke(
            "设置界面定位诊断："
            + $"屏幕={Screen.width}x{Screen.height}，"
            + $"按钮local=({localPosition.x:F1},{localPosition.y:F1},{localPosition.z:F1})，"
            + $"左下world=({bottomLeftWorld.x:F4},{bottomLeftWorld.y:F4},{bottomLeftWorld.z:F4})，"
            + $"相机={(anchorCamera == null ? "无" : anchorCamera.name)}，"
            + $"来源={coordinateSource}，"
            + $"屏幕点=({screenPoint.x:F1},{screenPoint.y:F1})，"
            + $"scale={scale:F4}，"
            + $"面板anchored=({anchoredPosition.x:F1},{anchoredPosition.y:F1})。");
    }

    // 创建标题栏。
    private void CreateHeader()
    {
        RectTransform header = CreateRect("标题栏", _panel);
        SetTopLeft(header, Vector2.zero, new Vector2(PanelWidth, 30f));
        Image image = header.gameObject.AddComponent<Image>();
        image.color = _headerColor;
        image.raycastTarget = true;

        Text title = CreateText("标题", header, "智能战斗倍速 - Sc千寻", 16, TextAnchor.MiddleCenter, Color.white);
        title.fontStyle = FontStyle.Bold;
        Stretch(title.rectTransform);
    }

    // 创建一行倍速选择按钮。
    private void CreateSpeedRow(string label, float y, SpeedTarget speedTarget)
    {
        Text rowLabel = CreateText(label.TrimEnd('：'), _panel, label, 14, TextAnchor.MiddleLeft, Color.white);
        SetTopLeft(rowLabel.rectTransform, new Vector2(14f, -y), new Vector2(78f, 28f));

        List<Button> target = ResolveButtonGroup(speedTarget);
        for (int i = 0; i < SpeedOptions.Length; i++)
        {
            int speed = SpeedOptions[i];
            Button button = CreateButton($"{speed}x", () => SelectSpeed(speedTarget, speed));
            RectTransform rect = button.GetComponent<RectTransform>();
            SetTopLeft(rect, new Vector2(90f + i * 32f, -y), new Vector2(28f, 28f));
            target.Add(button);
        }
    }

    // 创建保存按钮。
    private void CreateSaveButton()
    {
        Button save = CreateButton("保存", _saveClicked);
        SetTopLeft(save.GetComponent<RectTransform>(), new Vector2(PanelWidth - 82f, -184f), new Vector2(68f, 30f));
    }

    // 选择某一类智能倍速。
    private void SelectSpeed(SpeedTarget speedTarget, int speed)
    {
        switch (speedTarget)
        {
            case SpeedTarget.MainCharacter:
                _settings.MainCharacterSpeed = speed;
                break;
            case SpeedTarget.Npc:
                _settings.NpcSpeed = speed;
                break;
            case SpeedTarget.NpcAttackMainCharacter:
                _settings.NpcAttackMainCharacterSpeed = speed;
                break;
        }

        RefreshSelectionVisuals();
    }

    // 刷新倍速选项按钮的颜色。
    private void RefreshSelectionVisuals()
    {
        RefreshButtonGroup(_mainSpeedButtons, _settings.MainCharacterSpeed);
        RefreshButtonGroup(_npcSpeedButtons, _settings.NpcSpeed);
        RefreshButtonGroup(_npcAttackMainCharacterSpeedButtons, _settings.NpcAttackMainCharacterSpeed);
    }

    // 返回对应设置项的按钮组。
    private List<Button> ResolveButtonGroup(SpeedTarget speedTarget)
    {
        return speedTarget switch
        {
            SpeedTarget.MainCharacter => _mainSpeedButtons,
            SpeedTarget.Npc => _npcSpeedButtons,
            SpeedTarget.NpcAttackMainCharacter => _npcAttackMainCharacterSpeedButtons,
            _ => _npcSpeedButtons
        };
    }

    // 刷新单组倍速按钮的颜色。
    private void RefreshButtonGroup(IReadOnlyList<Button> buttons, int selectedSpeed)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            Button button = buttons[i];
            Color color = SpeedOptions[i] == selectedSpeed ? _selectedColor : _buttonColor;
            Image? image = button.targetGraphic?.GetComponent<Image>();
            if (image != null)
            {
                image.color = color;
            }

            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.selectedColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.18f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            button.colors = colors;
        }
    }

    // 创建普通按钮。
    private Button CreateButton(string label, Action clicked)
    {
        RectTransform root = CreateRect(label + "按钮", _panel);
        Image image = root.gameObject.AddComponent<Image>();
        image.color = _buttonColor;
        image.raycastTarget = true;

        Button button = root.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(clicked));

        Text text = CreateText("文字", root, label, 13, TextAnchor.MiddleCenter, Color.white);
        text.fontStyle = FontStyle.Bold;
        Stretch(text.rectTransform);
        return button;
    }

    // 创建文本对象。
    private Text CreateText(string name, Transform parent, string text, int size, TextAnchor alignment, Color color)
    {
        RectTransform rect = CreateRect(name, parent);
        Text label = rect.gameObject.AddComponent<Text>();
        label.font = _font;
        label.text = text;
        label.fontSize = size;
        label.alignment = alignment;
        label.color = color;
        label.raycastTarget = false;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        return label;
    }

    // 创建 RectTransform 对象。
    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject obj = new(name);
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    // 设置左上角布局。
    private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    // 填满父节点。
    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // 确保存在 UGUI 事件系统。
    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null)
        {
            return;
        }

        GameObject eventSystem = new("SmartBattleSpeedMod_EventSystem");
        UnityEngine.Object.DontDestroyOnLoad(eventSystem);
        eventSystem.AddComponent<EventSystem>();
        eventSystem.AddComponent<StandaloneInputModule>();
    }

    // 设置面板中的倍速类别。
    private enum SpeedTarget
    {
        MainCharacter,
        Npc,
        NpcAttackMainCharacter
    }
}

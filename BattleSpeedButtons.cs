// 用途：创建和维护战斗倍速栏中的“智能”“设置”扩展按钮。

using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SmartBattleSpeedMod;

// 智能倍速按钮，完整保留原生按钮组件和事件，由 BattleTimeScaleButtonClicked 前置补丁接管业务逻辑。
internal sealed class SmartSpeedButtonUi
{
    private const string ButtonName = "SmartBattleSpeedButton";
    private const string ButtonText = "智能";

    private GameObject? _button;
    private Toggle? _toggleComponent;
    private bool _lastSelected;

    public bool IsAlive => _button != null && !_button.WasCollected;
    public Font? Font => _button?.GetComponentInChildren<Text>(true)?.font;

    // 创建或刷新智能按钮。
    public void CreateOrUpdate(Transform tab, Transform template, Vector3 localPosition)
    {
        _button = BattleSpeedButtonFactory.EnsureClone(tab, template, ButtonName, ButtonText, localPosition);
        _toggleComponent = _button.GetComponent<Toggle>() ?? _button.GetComponentInChildren<Toggle>(true);
        SetSelected(_lastSelected);
    }

    // 判断对象是否属于智能按钮或其子节点。
    public bool Contains(GameObject obj)
    {
        return BattleSpeedButtonFactory.Contains(_button, obj);
    }

    // 设置智能按钮选中态。
    public void SetSelected(bool selected)
    {
        _lastSelected = selected;
        if (_toggleComponent == null)
        {
            return;
        }

        _toggleComponent.SetIsOnWithoutNotify(selected);
        if (selected)
        {
            _toggleComponent.Select();
        }
    }

    // 销毁智能按钮。
    public void Destroy()
    {
        if (_button != null && !_button.WasCollected)
        {
            UnityEngine.Object.Destroy(_button);
        }

        _button = null;
        _toggleComponent = null;
        _lastSelected = false;
    }
}

// 设置入口按钮，只复用倍速按钮外观，不参与 Unity UI 选中态。
internal sealed class SettingsButtonUi
{
    private const string ButtonName = "SmartBattleSpeedSettingsButton";
    private const string ButtonText = "设置";

    private GameObject? _button;
    private ButtonClick? _clickHandler;

    public bool IsAlive => _button != null && !_button.WasCollected;
    public RectTransform? RectTransform => _button?.GetComponent<RectTransform>();
    public Font? Font => _button?.GetComponentInChildren<Text>(true)?.font;

    // 创建或刷新设置按钮，移除 Selectable 语义，并使用 ButtonClick 处理点击。
    public void CreateOrUpdate(Transform tab, Transform template, Vector3 localPosition, Action clicked)
    {
        _button = BattleSpeedButtonFactory.EnsureCleanButton(tab, template, ButtonName, ButtonText, localPosition);
        BattleSpeedButtonFactory.ClearAllClickComponents(_button);
        _clickHandler = _button.AddComponent<ButtonClick>();
        _clickHandler.leftClick ??= new UnityEvent();
        _clickHandler.middleClick ??= new UnityEvent();
        _clickHandler.rightClick ??= new UnityEvent();
        _clickHandler.leftClick.RemoveAllListeners();
        _clickHandler.middleClick.RemoveAllListeners();
        _clickHandler.rightClick.RemoveAllListeners();
        _clickHandler.leftClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(clicked));
    }

    // 判断对象是否属于设置按钮或其子节点。
    public bool Contains(GameObject obj)
    {
        return BattleSpeedButtonFactory.Contains(_button, obj);
    }

    // 销毁设置按钮。
    public void Destroy()
    {
        if (_button != null && !_button.WasCollected)
        {
            UnityEngine.Object.Destroy(_button);
        }

        _button = null;
        _clickHandler = null;
    }
}

// 扩展按钮克隆和组件清理工具，集中处理外观复用细节。
internal static class BattleSpeedButtonFactory
{
    private const int LabelFontSize = 18;

    // 确保指定克隆按钮存在，并同步标题、位置和模板缩放。
    public static GameObject EnsureClone(Transform tab, Transform template, string name, string text, Vector3 localPosition)
    {
        Transform? existing = tab.Find(name);
        GameObject button = existing != null
            ? existing.gameObject
            : UnityEngine.Object.Instantiate(template.gameObject, tab, false);

        SyncButtonTransformAndText(button, template, name, text, localPosition);
        return button;
    }

    // 确保指定干净按钮存在，只复用原生按钮素材和布局。
    public static GameObject EnsureCleanButton(Transform tab, Transform template, string name, string text, Vector3 localPosition)
    {
        Transform? existing = tab.Find(name);
        GameObject button = existing != null ? existing.gameObject : CreateCleanButtonFromTemplate(tab, template, name);

        SyncButtonTransformAndText(button, template, name, text, localPosition);
        return button;
    }

    // 同步按钮根节点的位置、缩放和显示文本。
    private static void SyncButtonTransformAndText(GameObject button, Transform template, string name, string text, Vector3 localPosition)
    {
        button.name = name;
        button.transform.SetAsLastSibling();
        button.transform.localPosition = localPosition;
        button.transform.localScale = template.localScale;
        SetButtonLabel(button, text);
    }

    // 用原生按钮素材从零创建一个干净按钮对象，避免继承原生持久化事件。
    private static GameObject CreateCleanButtonFromTemplate(Transform tab, Transform template, string name)
    {
        GameObject button = new(name);
        RectTransform rect = button.AddComponent<RectTransform>();
        RectTransform templateRect = template.GetComponent<RectTransform>();
        rect.SetParent(tab, false);
        if (templateRect != null)
        {
            rect.anchorMin = templateRect.anchorMin;
            rect.anchorMax = templateRect.anchorMax;
            rect.pivot = templateRect.pivot;
            rect.sizeDelta = templateRect.sizeDelta;
        }

        CopyBackground(button.transform, template);
        CopyLabel(button.transform, template);
        CopyCanvasGroup(button, template.gameObject);
        return button;
    }

    // 复制原生按钮背景 Image 素材。
    private static void CopyBackground(Transform parent, Transform template)
    {
        Transform? templateBackground = template.Find("Background");
        Image? templateImage = templateBackground?.GetComponent<Image>() ?? template.GetComponentInChildren<Image>(true);
        if (templateImage == null)
        {
            return;
        }

        GameObject background = new("Background");
        RectTransform rect = background.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        CopyRectTransform(rect, templateImage.rectTransform);

        Image image = background.AddComponent<Image>();
        image.sprite = templateImage.sprite;
        image.overrideSprite = templateImage.overrideSprite;
        image.type = templateImage.type;
        image.preserveAspect = templateImage.preserveAspect;
        image.fillCenter = templateImage.fillCenter;
        image.fillMethod = templateImage.fillMethod;
        image.fillAmount = templateImage.fillAmount;
        image.fillClockwise = templateImage.fillClockwise;
        image.fillOrigin = templateImage.fillOrigin;
        image.color = templateImage.color;
        image.material = templateImage.material;
        image.raycastTarget = templateImage.raycastTarget;
    }

    // 复制原生按钮文本字体和布局。
    private static void CopyLabel(Transform parent, Transform template)
    {
        Transform? templateLabelTransform = template.Find("Label");
        Text? templateText = templateLabelTransform?.GetComponent<Text>() ?? template.GetComponentInChildren<Text>(true);
        if (templateText == null)
        {
            return;
        }

        GameObject labelObject = new("Label");
        RectTransform rect = labelObject.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        CopyRectTransform(rect, templateText.rectTransform);

        Text label = labelObject.AddComponent<Text>();
        label.font = templateText.font;
        label.fontStyle = templateText.fontStyle;
        label.fontSize = Math.Min(templateText.fontSize, LabelFontSize);
        label.lineSpacing = templateText.lineSpacing;
        label.supportRichText = templateText.supportRichText;
        label.alignment = templateText.alignment;
        label.alignByGeometry = templateText.alignByGeometry;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.resizeTextForBestFit = false;
        label.color = templateText.color;
        label.material = templateText.material;
        label.raycastTarget = false;
    }

    // 复制 CanvasGroup 这类纯显示状态。
    private static void CopyCanvasGroup(GameObject button, GameObject template)
    {
        CanvasGroup? templateGroup = template.GetComponent<CanvasGroup>();
        if (templateGroup == null)
        {
            return;
        }

        CanvasGroup group = button.AddComponent<CanvasGroup>();
        group.alpha = templateGroup.alpha;
        group.interactable = templateGroup.interactable;
        group.blocksRaycasts = templateGroup.blocksRaycasts;
        group.ignoreParentGroups = templateGroup.ignoreParentGroups;
    }

    // 复制 RectTransform 布局字段。
    private static void CopyRectTransform(RectTransform target, RectTransform source)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
    }

    // 判断对象是否属于指定按钮根节点或其子节点。
    public static bool Contains(GameObject? root, GameObject obj)
    {
        if (root == null)
        {
            return false;
        }

        Transform? current = obj.transform;
        while (current != null)
        {
            if (current.gameObject == root)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    // 清理按钮上的所有点击入口，设置按钮使用。
    public static void ClearAllClickComponents(GameObject button)
    {
        foreach (Button oldButton in button.GetComponentsInChildren<Button>(true))
        {
            oldButton.onClick.RemoveAllListeners();
            UnityEngine.Object.DestroyImmediate(oldButton);
        }

        ClearNonButtonClickComponents(button);
    }

    // 清理克隆体继承的非 Button 点击入口，智能按钮保留原 Button 选中视觉。
    public static void ClearNonButtonClickComponents(GameObject button)
    {
        foreach (ButtonClick buttonClick in button.GetComponentsInChildren<ButtonClick>(true))
        {
            buttonClick.leftClick?.RemoveAllListeners();
            buttonClick.middleClick?.RemoveAllListeners();
            buttonClick.rightClick?.RemoveAllListeners();
            UnityEngine.Object.DestroyImmediate(buttonClick);
        }

        foreach (UIEventTrigger trigger in button.GetComponentsInChildren<UIEventTrigger>(true))
        {
            trigger.onClick?.Clear();
            UnityEngine.Object.DestroyImmediate(trigger);
        }

        foreach (UIEventListener listener in button.GetComponentsInChildren<UIEventListener>())
        {
            listener.Clear();
            UnityEngine.Object.DestroyImmediate(listener);
        }
    }

    // 设置按钮标题。
    private static void SetButtonLabel(GameObject button, string text)
    {
        Text? label = button.GetComponentInChildren<Text>(true);
        if (label == null)
        {
            return;
        }

        label.text = text;
        label.fontSize = Math.Min(label.fontSize, LabelFontSize);
        label.resizeTextForBestFit = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
    }
}

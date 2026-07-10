// 用途：在战斗倍速栏添加“智能”和“设置”按钮，并维护它们与原生倍速按钮的选中关系。

using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(SmartBattleSpeedMod.SmartBattleSpeedPlugin), "智能战斗倍速", "1.0.0", "Sc千寻")]
[assembly: MelonGame(null, "LongYinLiZhiZhuan")]

namespace SmartBattleSpeedMod;

// 智能战斗倍速 Mod 入口，负责补丁安装和战斗 UI 生命周期。
public sealed partial class SmartBattleSpeedPlugin : MelonMod
{
    private const int NativeButtonCount = 5;
    private const int NewBattleForceApplyFrames = 8;
    private const float SkipButtonVisualSpacingFactor = 2f / 3f;
    private const float SkipButtonExtraRightOffset = 4f;

    private static SmartBattleSpeedPlugin? ActivePlugin { get; set; }

    private readonly SmartSpeedButtonUi _smartButton = new();
    private readonly SettingsButtonUi _settingsButton = new();
    private SmartBattleSpeedSettings _settings = SmartBattleSpeedSettings.Default;
    private SmartBattleSpeedSettingsUi? _settingsUi;
    private Transform? _knownTimeScaleTab;
    private bool _smartSelected;
    private float _lastAppliedSmartSpeed = -1f;
    private bool _battleReadyForSmartSpeed;
    private int _pendingForceApplyFrames;
    private bool _settingsPauseApplied;
    private bool _battlePausedBeforeSettings;
    private int _lastSmartTogglePointerClickFrame = -1000;
    private bool _npcAttackingMainCharacter;
    private int _skipButtonLayoutLogs;

    // 初始化 Mod，并安装原生倍速按钮点击后置补丁。
    public override void OnInitializeMelon()
    {
        ActivePlugin = this;
        _settings = SmartBattleSpeedSettings.Load(LoggerInstance);
        _smartSelected = _settings.SmartModeEnabled;
        LoggerInstance.Msg($"智能战斗倍速已加载：主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x，智能模式={FormatSmartMode(_smartSelected)}，调试日志={FormatSmartMode(_settings.DebugLoggingEnabled)}。");

        try
        {
            HarmonyInstance.Patch(
                AccessTools.Method(typeof(BattleController), nameof(BattleController.BattleTimeScaleButtonClicked)),
                prefix: new HarmonyMethod(typeof(SmartBattleSpeedPlugin), nameof(OnNativeBattleSpeedClickedPrefix)),
                postfix: new HarmonyMethod(typeof(SmartBattleSpeedPlugin), nameof(OnNativeBattleSpeedClickedPostfix)));
            HarmonyInstance.Patch(
                AccessTools.Method(typeof(Toggle), nameof(Toggle.OnPointerClick), new[] { typeof(PointerEventData) }),
                prefix: new HarmonyMethod(typeof(SmartBattleSpeedPlugin), nameof(OnUiTogglePointerClickPrefix)));
            HarmonyInstance.Patch(
                AccessTools.Method(typeof(BattleController), nameof(BattleController.BattleUnitAttackStart)),
                prefix: new HarmonyMethod(typeof(SmartBattleSpeedPlugin), nameof(OnBattleUnitAttackStartPrefix)));
            HarmonyInstance.Patch(
                AccessTools.Method(typeof(BattleController), nameof(BattleController.BattleUnitAttackEnd), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(SmartBattleSpeedPlugin), nameof(OnBattleUnitAttackEndPrefix)));

            LogDebug("智能战斗倍速 UI 已加载：战斗倍速栏会追加“智能”和“设置”按钮。");
            LogDebug("智能战斗倍速原生按钮点击监听已安装：UnityEngine.UI.Toggle.OnPointerClick。");
            LogDebug("NPC攻击主角倍速监听已安装：BattleUnitAttackStart / BattleUnitAttackEnd。");
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning("安装倍速按钮补丁失败，智能按钮仍会尝试创建：" + ex.Message);
        }
    }

    // 每帧轻量确认战斗倍速栏是否存在，存在时补齐扩展按钮。
    public override void OnUpdate()
    {
        BattleController? battle = BattleController.Instance;
        SyncBattleSpeedButtons(battle);
        TrackBattleLifecycle(battle);
        _smartButton.SetSelected(_smartSelected);
        if (_smartSelected && IsBattleReadyForSmartSpeed(battle))
        {
            bool forceWrite = _pendingForceApplyFrames > 0;
            ApplySmartBattleSpeed(battle, forceWrite);
            if (_pendingForceApplyFrames > 0)
            {
                _pendingForceApplyFrames--;
            }
        }
    }

    // 卸载 Mod 时销毁运行时创建的按钮。
    public override void OnDeinitializeMelon()
    {
        RestoreBattlePauseForSettings();
        _smartButton.Destroy();
        _settingsButton.Destroy();
        _settingsUi?.Destroy();
        _settingsUi = null;
        _knownTimeScaleTab = null;
        _battleReadyForSmartSpeed = false;
        _pendingForceApplyFrames = 0;
        _npcAttackingMainCharacter = false;
        if (ReferenceEquals(ActivePlugin, this))
        {
            ActivePlugin = null;
        }
    }

    // 原生倍速入口前置保护，只拦截智能按钮继承的原生持久化事件。
    private static bool OnNativeBattleSpeedClickedPrefix(GameObject buttonClicked)
    {
        SmartBattleSpeedPlugin? plugin = ActivePlugin;
        if (plugin == null)
        {
            return true;
        }

        if (plugin._smartButton.Contains(buttonClicked))
        {
            plugin.LogDebug("点击来源识别：智能按钮，" + plugin.DescribeButtonClick(buttonClicked));
            if (!plugin.ConsumeSmartTogglePointerClick())
            {
                plugin.LogDebug("智能按钮事件来源不是玩家点击智能按钮，已忽略。");
                return false;
            }

            plugin.SelectSmartMode();
            return false;
        }

        if (plugin._settingsButton.Contains(buttonClicked))
        {
            plugin.LogDebug("点击来源识别：设置按钮，" + plugin.DescribeButtonClick(buttonClicked));
            plugin.OpenSettings();
            return false;
        }

        return true;
    }

    // 记录原生倍速入口调用来源；关闭智能模式只由原生按钮 UI 点击监听负责。
    private static void OnNativeBattleSpeedClickedPostfix(GameObject buttonClicked)
    {
        SmartBattleSpeedPlugin? plugin = ActivePlugin;
        if (plugin == null)
        {
            return;
        }

        if (plugin._smartButton.Contains(buttonClicked) || plugin._settingsButton.Contains(buttonClicked))
        {
            return;
        }

        bool nativeButton = plugin.IsNativeBattleSpeedButton(buttonClicked);
        plugin.LogDebug(
            "原生倍速方法调用："
            + plugin.DescribeButtonClick(buttonClicked)
            + $"，isNative={nativeButton}，点击前智能模式={FormatSmartMode(plugin._smartSelected)}。");
    }

    // 监听原生倍速 Toggle 真实鼠标点击，用于识别玩家从“智能”切到原生倍速按钮。
    private static void OnUiTogglePointerClickPrefix(Toggle __instance, PointerEventData eventData)
    {
        ActivePlugin?.HandleUiTogglePointerClick(__instance, eventData);
    }

    // 攻击起手时判断当前 NPC 是否正在准备攻击主角本人。
    private static void OnBattleUnitAttackStartPrefix(BattleController __instance)
    {
        ActivePlugin?.HandleBattleUnitAttackStart(__instance);
    }

    // 攻击结束流程启动时清理“NPC攻击主角”临时状态。
    private static void OnBattleUnitAttackEndPrefix(BattleController __instance, float delayTime)
    {
        ActivePlugin?.HandleBattleUnitAttackEnd(__instance, delayTime);
    }

    // 点击“智能”按钮时启用智能倍速接管，并立即应用当前设置。
    private void SelectSmartMode()
    {
        SetSmartMode(true, true, null);
        ApplySmartBattleSpeed(BattleController.Instance, true);
        LogPlayerSetting($"玩家设置智能战斗倍速：智能模式=开启，主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x。");
    }

    // 点击“设置”按钮时只打开设置入口，不改变任何倍速按钮选中状态。
    private void OpenSettings()
    {
        EnsureSettingsUi();
        if (_settingsUi == null)
        {
            return;
        }

        bool visible = _settingsUi.ToggleVisible();
        UpdateBattlePauseForSettings(visible);
        LogDebug(visible ? "智能战斗倍速设置界面已打开。" : "智能战斗倍速设置界面已关闭。");
    }

    // 同步战斗倍速栏扩展按钮布局。
    private void SyncBattleSpeedButtons(BattleController? battle)
    {
        GameObject? timeScaleTab = battle?.timeScaleTab;
        if (timeScaleTab == null || !timeScaleTab.activeInHierarchy)
        {
            SetSettingsUiVisible(false);
            return;
        }

        Transform tab = timeScaleTab.transform;
        if (_knownTimeScaleTab != null && !_knownTimeScaleTab.Equals(tab))
        {
            ResetBattleSpeedUi();
        }

        if (_smartButton.IsAlive && _settingsButton.IsAlive && _knownTimeScaleTab != null && _knownTimeScaleTab.Equals(tab))
        {
            RelayoutBattleSkipButton(battle, tab);
            if (_settingsUi?.IsVisible == true && _settingsButton.RectTransform != null)
            {
                _settingsUi.UpdateAnchor(_settingsButton.RectTransform);
            }

            return;
        }

        if (tab.childCount < NativeButtonCount)
        {
            return;
        }

        Transform template = tab.GetChild(NativeButtonCount - 1);
        Transform previous = tab.GetChild(NativeButtonCount - 2);
        Vector3 buttonStep = template.localPosition - previous.localPosition;

        _smartButton.CreateOrUpdate(tab, template, template.localPosition + buttonStep);
        _settingsButton.CreateOrUpdate(tab, template, template.localPosition + buttonStep * 2f, OpenSettings);
        RelayoutBattleSkipButton(battle, tab);
        if (_settingsUi != null && _settingsButton.RectTransform != null)
        {
            _settingsUi.UpdateAnchor(_settingsButton.RectTransform);
        }

        _knownTimeScaleTab = tab;
    }

    // 有跳过按钮时把它排到设置按钮后面，避免遮挡智能和设置按钮。
    private void RelayoutBattleSkipButton(BattleController? battle, Transform tab)
    {
        GameObject? skipButton = battle?.battleSkipButton;
        if (skipButton == null || !skipButton.activeInHierarchy || tab.childCount < NativeButtonCount)
        {
            return;
        }

        Transform template = tab.GetChild(NativeButtonCount - 1);
        Transform previous = tab.GetChild(NativeButtonCount - 2);
        Vector3 buttonStep = template.localPosition - previous.localPosition;
        Transform skipTransform = skipButton.transform;
        Transform? skipParent = skipTransform.parent;
        if (skipParent == null)
        {
            return;
        }

        Vector3 spacingOffset = ResolveWideButtonSpacingOffset(template, skipTransform, buttonStep, out float templateWidth, out float skipWidth);
        Vector3 desiredTabLocalPosition = template.localPosition + buttonStep * 3f + spacingOffset;
        Vector3 desiredWorldPosition = tab.TransformPoint(desiredTabLocalPosition);
        Vector3 desiredLocalPosition = skipParent.InverseTransformPoint(desiredWorldPosition);
        if ((skipTransform.localPosition - desiredLocalPosition).sqrMagnitude < 0.01f)
        {
            return;
        }

        Vector3 oldPosition = skipTransform.localPosition;
        skipTransform.localPosition = desiredLocalPosition;
        if (_skipButtonLayoutLogs < 5)
        {
            _skipButtonLayoutLogs++;
            LogDebug(
                "跳过按钮布局调整："
                + $"old=({oldPosition.x:F1},{oldPosition.y:F1},{oldPosition.z:F1})，"
                + $"new=({desiredLocalPosition.x:F1},{desiredLocalPosition.y:F1},{desiredLocalPosition.z:F1})，"
                + $"templateWidth={templateWidth:F1}，skipWidth={skipWidth:F1}，offset=({spacingOffset.x:F1},{spacingOffset.y:F1},{spacingOffset.z:F1})，"
                + $"parent={GetTransformPath(skipParent)}。");
        }
    }

    // 跳过按钮比普通倍速按钮更宽；按实机视觉调校后的半宽差补偿中心点，保持边缘间距自然。
    private static Vector3 ResolveWideButtonSpacingOffset(Transform template, Transform skipTransform, Vector3 buttonStep, out float templateWidth, out float skipWidth)
    {
        RectTransform? templateRect = template.GetComponent<RectTransform>();
        RectTransform? skipRect = skipTransform.GetComponent<RectTransform>();
        templateWidth = templateRect?.rect.width ?? 0f;
        skipWidth = skipRect?.rect.width ?? 0f;
        if (templateRect == null || skipRect == null || buttonStep.sqrMagnitude < 0.001f)
        {
            return Vector3.zero;
        }

        float extraDistance = Math.Max(0f, (skipWidth - templateWidth) * 0.5f * SkipButtonVisualSpacingFactor) + SkipButtonExtraRightOffset;
        return buttonStep.normalized * extraDistance;
    }

    // 战斗 UI 重建时清理旧对象，避免设置界面挂在旧层级下。
    private void ResetBattleSpeedUi()
    {
        _smartButton.Destroy();
        _settingsButton.Destroy();
        SetSettingsUiVisible(false);
        _settingsUi?.Destroy();
        _settingsUi = null;
        _knownTimeScaleTab = null;
        _skipButtonLayoutLogs = 0;
    }

    // 确保设置界面已创建。
    private void EnsureSettingsUi()
    {
        if (_settingsUi != null)
        {
            return;
        }

        RectTransform? anchor = _settingsButton.RectTransform;
        Font? font = ResolveGameFont() ?? _settingsButton.Font ?? _smartButton.Font;
        if (anchor == null || font == null)
        {
            LoggerInstance.Warning("设置界面创建失败：设置按钮或字体尚未准备好。");
            return;
        }

        _settingsUi = new SmartBattleSpeedSettingsUi(anchor, font, _settings, SaveSettings, LogDebug);
    }

    // 使用与“更好的传送”一致的字体解析方式，优先取游戏已加载的 UGUI 字体。
    private static Font? ResolveGameFont()
    {
        Text[] texts = Resources.FindObjectsOfTypeAll<Text>();
        foreach (Text text in texts)
        {
            if (text != null && text.font != null)
            {
                return text.font;
            }
        }

        Font[] fonts = Resources.FindObjectsOfTypeAll<Font>();
        foreach (Font font in fonts)
        {
            if (font != null)
            {
                return font;
            }
        }

        return null;
    }

    // 保存设置界面当前选项。
    private void SaveSettings()
    {
        LogDebug(
            $"保存智能战斗倍速设置：主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x，"
            + $"当前运行模式={FormatSmartMode(_smartSelected)}，SmartModeEnabled保持为{FormatSmartMode(_settings.SmartModeEnabled)}。");
        _settings.Save(LoggerInstance);
        LogPlayerSetting($"玩家设置智能战斗倍速：主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x，智能模式={FormatSmartMode(_smartSelected)}。");
        SetSettingsUiVisible(false);
        if (_smartSelected)
        {
            _lastAppliedSmartSpeed = -1f;
            ApplySmartBattleSpeed(BattleController.Instance, true);
            _smartButton.SetSelected(true);
            LogDebug("智能战斗倍速设置已应用到当前战斗。");
        }
    }

    // 统一设置界面显隐入口，确保窗口状态和战斗暂停状态同步。
    private void SetSettingsUiVisible(bool visible)
    {
        _settingsUi?.SetVisible(visible);
        UpdateBattlePauseForSettings(visible);
    }

    // 设置界面打开时暂停战斗，关闭时只恢复本 Mod 造成的暂停。
    private void UpdateBattlePauseForSettings(bool settingsVisible)
    {
        if (settingsVisible)
        {
            PauseBattleForSettings();
            return;
        }

        RestoreBattlePauseForSettings();
    }

    // 记录原暂停状态，并在需要时暂停战斗。
    private void PauseBattleForSettings()
    {
        BattleController? battle = BattleController.Instance;
        if (battle == null || _settingsPauseApplied)
        {
            return;
        }

        _battlePausedBeforeSettings = battle.battlePaused;
        if (!_battlePausedBeforeSettings)
        {
            battle.battlePaused = true;
            _settingsPauseApplied = true;
        }
    }

    // 关闭设置界面时恢复打开前的暂停状态。
    private void RestoreBattlePauseForSettings()
    {
        if (!_settingsPauseApplied)
        {
            return;
        }

        BattleController? battle = BattleController.Instance;
        if (battle != null)
        {
            battle.battlePaused = _battlePausedBeforeSettings;
        }

        _settingsPauseApplied = false;
        _battlePausedBeforeSettings = false;
    }

    // 按战斗状态机识别新战斗，避免跨战斗复用 UI 选中态但漏掉实际倍速接管。
    private void TrackBattleLifecycle(BattleController? battle)
    {
        bool ready = IsBattleReadyForSmartSpeed(battle);
        if (!ready)
        {
            if (_battleReadyForSmartSpeed)
            {
                _lastAppliedSmartSpeed = -1f;
            }

            _battleReadyForSmartSpeed = false;
            _pendingForceApplyFrames = 0;
            if (_npcAttackingMainCharacter)
            {
                _npcAttackingMainCharacter = false;
                _lastAppliedSmartSpeed = -1f;
                LogDebug("战斗退出可接管状态，已清理NPC攻击主角倍速标记。");
            }

            return;
        }

        if (_battleReadyForSmartSpeed)
        {
            return;
        }

        _battleReadyForSmartSpeed = true;
        InitializeBattleSmartState();
        if (_smartSelected)
        {
            LogDebug("检测到新战斗进入可接管状态，智能战斗倍速将重新应用。");
        }
    }

    // 只有战斗状态机真正进入 Fighting 且当前行动单位存在时，才让智能倍速接管。
    private static bool IsBattleReadyForSmartSpeed(BattleController? battle)
    {
        return battle != null
            && battle.battleState == BattleState.Fighting
            && battle.nowActiveUnit != null
            && battle.timeScaleTab != null
            && battle.timeScaleTab.activeInHierarchy;
    }

    // 保存智能模式开关状态，确保跨游戏启动周期恢复用户选择。
    private void PersistSmartMode(bool enabled)
    {
        if (_settings.SmartModeEnabled == enabled)
        {
            return;
        }

        _settings.SmartModeEnabled = enabled;
        _settings.Save(LoggerInstance);
    }

    // 切换智能模式内部状态、按钮视觉和持久化状态。
    private void SetSmartMode(bool enabled, bool persist, string? logMessage)
    {
        bool oldSelected = _smartSelected;
        bool oldPersisted = _settings.SmartModeEnabled;
        _smartSelected = enabled;
        _lastAppliedSmartSpeed = -1f;
        _smartButton.SetSelected(enabled);
        if (persist)
        {
            PersistSmartMode(enabled);
        }

        LogDebug(
            $"智能模式状态更新：运行时 {FormatSmartMode(oldSelected)} -> {FormatSmartMode(_smartSelected)}，"
            + $"配置 {FormatSmartMode(oldPersisted)} -> {FormatSmartMode(_settings.SmartModeEnabled)}，"
            + $"persist={persist}。");

        if (!string.IsNullOrEmpty(logMessage))
        {
            LogDebug(logMessage);
        }
    }

    // 新战斗进入 ready 时按配置初始化本场智能模式状态和强制写入窗口。
    private void InitializeBattleSmartState()
    {
        bool oldSelected = _smartSelected;
        _smartSelected = _settings.SmartModeEnabled;
        _lastAppliedSmartSpeed = -1f;
        _pendingForceApplyFrames = _smartSelected ? NewBattleForceApplyFrames : 0;
        _smartButton.SetSelected(_smartSelected);
        LogDebug(
            "战斗开始初始化智能倍速："
            + $"SmartModeEnabled={FormatSmartMode(_settings.SmartModeEnabled)}，"
            + $"运行时 {FormatSmartMode(oldSelected)} -> {FormatSmartMode(_smartSelected)}，"
            + $"主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x，"
            + $"forceFrames={_pendingForceApplyFrames}。");
    }

    // 如果智能模式下玩家点击原生倍速按钮，则关闭智能模式并交回原生倍速逻辑。
    private void HandleUiTogglePointerClick(Toggle toggle, PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        if (toggle == null)
        {
            return;
        }

        GameObject? clicked = toggle.gameObject;
        if (clicked == null || _settingsButton.Contains(clicked))
        {
            return;
        }

        if (_smartButton.Contains(clicked))
        {
            MarkSmartTogglePointerClick(clicked);
            return;
        }

        if (!_smartSelected || _knownTimeScaleTab == null)
        {
            return;
        }

        if (!IsNativeBattleSpeedButton(clicked, _knownTimeScaleTab))
        {
            return;
        }

        LogDebug(
            "检测到玩家点击原生倍速按钮："
            + DescribeButtonClick(clicked)
            + $"，pointerButton={eventData.button}，"
            + $"toggleIsOnBefore={toggle.isOn}，"
            + $"智能模式={FormatSmartMode(_smartSelected)}。");
        SetSmartMode(false, true, "玩家点击原生倍速按钮，智能战斗倍速已关闭。");
    }

    // 攻击起手时根据实际伤害范围判断是否启用 NPC 攻击主角倍速。
    private void HandleBattleUnitAttackStart(BattleController? battle)
    {
        if (battle == null)
        {
            return;
        }

        bool oldState = _npcAttackingMainCharacter;
        bool newState = NpcAttackMainCharacterDetector.IsTargetingMainCharacter(battle, out string diagnostic);
        _npcAttackingMainCharacter = newState;
        LogDebug(
            "攻击起手检测："
            + diagnostic
            + $"，NPC攻击主角={FormatSmartMode(newState)}，智能模式={FormatSmartMode(_smartSelected)}。");

        if (oldState == newState)
        {
            return;
        }

        _lastAppliedSmartSpeed = -1f;
        if (_smartSelected)
        {
            ApplySmartBattleSpeed(battle, true);
        }
    }

    // 攻击结束流程启动时清理 NPC 攻击主角倍速标记。
    private void HandleBattleUnitAttackEnd(BattleController? battle, float delayTime)
    {
        if (!_npcAttackingMainCharacter)
        {
            return;
        }

        _npcAttackingMainCharacter = false;
        _lastAppliedSmartSpeed = -1f;
        LogDebug($"攻击结束检测：BattleUnitAttackEnd(delay={delayTime:F2})，已清理NPC攻击主角倍速标记，智能模式={FormatSmartMode(_smartSelected)}。");
        if (_smartSelected)
        {
            ApplySmartBattleSpeed(battle, true);
        }
    }

    // 记录玩家真实点击智能 Toggle 的帧，用于区分 ToggleGroup 取消选中触发的继承事件。
    private void MarkSmartTogglePointerClick(GameObject clicked)
    {
        _lastSmartTogglePointerClickFrame = Time.frameCount;
        LogDebug("检测到玩家点击智能按钮 Toggle：" + DescribeButtonClick(clicked) + $"，frame={_lastSmartTogglePointerClickFrame}。");
    }

    // 只允许同一帧真实智能按钮点击触发智能模式开启。
    private bool ConsumeSmartTogglePointerClick()
    {
        if (_lastSmartTogglePointerClickFrame != Time.frameCount)
        {
            return false;
        }

        _lastSmartTogglePointerClickFrame = -1000;
        return true;
    }

    // 只把原生倍速栏前 5 个按钮视为关闭智能的来源，避免设置按钮或其他转发误关智能。
    private bool IsNativeBattleSpeedButton(GameObject buttonClicked)
    {
        BattleController? battle = BattleController.Instance;
        Transform? tab = battle?.timeScaleTab?.transform;
        if (tab == null)
        {
            return false;
        }

        return IsNativeBattleSpeedButton(buttonClicked, tab);
    }

    // 判断对象是否属于指定倍速栏前 5 个原生倍速按钮。
    private static bool IsNativeBattleSpeedButton(GameObject buttonClicked, Transform tab)
    {
        Transform? current = buttonClicked.transform;
        while (current != null && current.parent != tab)
        {
            current = current.parent;
        }

        if (current == null || current.parent != tab)
        {
            return false;
        }

        for (int i = 0; i < Math.Min(NativeButtonCount, tab.childCount); i++)
        {
            if (tab.GetChild(i) == current)
            {
                return true;
            }
        }

        return false;
    }

    // 输出点击对象和其在倍速栏中的归属，方便确认是否把扩展按钮误判为原生按钮。
    private string DescribeButtonClick(GameObject buttonClicked)
    {
        Transform? directChild = ResolveDirectTimeScaleChild(buttonClicked.transform);
        int directChildIndex = ResolveChildIndex(directChild);
        string directChildName = directChild == null ? "无" : directChild.name;
        return $"button={GetTransformPath(buttonClicked.transform)}，directChild={directChildName}，directChildIndex={directChildIndex}";
    }

    // 找到点击对象在 timeScaleTab 下的直接子节点。
    private static Transform? ResolveDirectTimeScaleChild(Transform? transform)
    {
        Transform? tab = BattleController.Instance?.timeScaleTab?.transform;
        Transform? current = transform;
        while (current != null && current.parent != tab)
        {
            current = current.parent;
        }

        return current != null && current.parent == tab ? current : null;
    }

    // 返回节点在父节点中的索引。
    private static int ResolveChildIndex(Transform? transform)
    {
        if (transform?.parent == null)
        {
            return -1;
        }

        Transform parent = transform.parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            if (parent.GetChild(i) == transform)
            {
                return i;
            }
        }

        return -1;
    }

    // 返回 Transform 完整路径，便于从日志确认点击来源。
    private static string GetTransformPath(Transform? transform)
    {
        if (transform == null)
        {
            return "无";
        }

        string path = transform.name;
        Transform? current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    // 智能模式开启时按当前行动单位写入目标战斗倍速。
    private void ApplySmartBattleSpeed(BattleController? battle, bool forceWrite)
    {
        try
        {
            GameDataController? gameData = GameDataController.Instance;
            WorldData? worldData = gameData?.gameSaveData?.WorldData;
            if (worldData == null)
            {
                return;
            }

            if (!IsBattleReadyForSmartSpeed(battle))
            {
                return;
            }

            float targetSpeed = ResolveCurrentSmartSpeed();
            if (!forceWrite
                && Math.Abs(_lastAppliedSmartSpeed - targetSpeed) < 0.001f
                && Math.Abs(worldData.battleTimeScale - targetSpeed) < 0.001f)
            {
                return;
            }

            float oldSpeed = worldData.battleTimeScale;
            worldData.battleTimeScale = targetSpeed;
            _lastAppliedSmartSpeed = targetSpeed;
            LogDebug(
                "智能倍速写入："
                + $"heroID={ResolveCurrentHeroId()}，"
                + $"old={oldSpeed}x，target={targetSpeed}x，forceWrite={forceWrite}，"
                + $"NPC攻击主角={FormatSmartMode(_npcAttackingMainCharacter)}，"
                + $"主角 {_settings.MainCharacterSpeed}x，NPC {_settings.NpcSpeed}x，NPC攻击主角 {_settings.NpcAttackMainCharacterSpeed}x。");
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning("应用智能战斗倍速失败：" + ex.Message);
        }
    }

    // 根据当前行动单位判断应使用主角倍速还是 NPC 倍速。
    private float ResolveCurrentSmartSpeed()
    {
        BattleUnit? nowActiveUnit = BattleController.Instance?.nowActiveUnit;
        HeroData? heroData = nowActiveUnit?.heroData;
        if (heroData != null && heroData.heroID == 0)
        {
            return _settings.MainCharacterSpeed;
        }

        return _npcAttackingMainCharacter ? _settings.NpcAttackMainCharacterSpeed : _settings.NpcSpeed;
    }

    // 返回当前行动单位 heroID，日志中用于确认主角/NPC 分流。
    private static int ResolveCurrentHeroId()
    {
        return BattleController.Instance?.nowActiveUnit?.heroData?.heroID ?? -1;
    }

}

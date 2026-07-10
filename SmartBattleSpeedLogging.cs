// 用途：集中处理智能战斗倍速 Mod 的普通日志和调试日志开关。

namespace SmartBattleSpeedMod;

// 智能战斗倍速 Mod 日志辅助逻辑。
public sealed partial class SmartBattleSpeedPlugin
{
    // 调试日志开关关闭时不输出运行时诊断，避免污染玩家日志。
    private void LogDebug(string message)
    {
        if (_settings.DebugLoggingEnabled)
        {
            LoggerInstance.Msg(message);
        }
    }

    // 玩家主动操作产生的关键日志，不受调试日志开关影响。
    private void LogPlayerSetting(string message)
    {
        LoggerInstance.Msg(message);
    }

    // 格式化智能模式状态。
    private static string FormatSmartMode(bool enabled)
    {
        return enabled ? "开启" : "关闭";
    }
}

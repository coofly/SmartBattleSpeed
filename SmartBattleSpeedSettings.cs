// 用途：保存智能战斗倍速设置，并负责从 UserData 读写配置。

using System;
using System.IO;
using System.Text.Json;
using MelonLoader;
using UnityEngine;

namespace SmartBattleSpeedMod;

// 智能战斗倍速配置，核心倍速逻辑直接读取这里的目标倍速。
internal sealed class SmartBattleSpeedSettings
{
    public int MainCharacterSpeed { get; set; } = 1;
    public int NpcSpeed { get; set; } = 5;
    public int NpcAttackMainCharacterSpeed { get; set; } = 5;
    public bool SmartModeEnabled { get; set; }
    public bool DebugLoggingEnabled { get; set; }

    public static SmartBattleSpeedSettings Default => new();

    // 从 UserData 读取设置，失败时使用默认值。
    public static SmartBattleSpeedSettings Load(MelonLogger.Instance logger)
    {
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                return Default;
            }

            string json = File.ReadAllText(path);
            SmartBattleSpeedSettings? settings = JsonSerializer.Deserialize<SmartBattleSpeedSettings>(json);
            if (settings == null)
            {
                return Default;
            }

            settings.MainCharacterSpeed = NormalizeSpeed(settings.MainCharacterSpeed, 1);
            settings.NpcSpeed = NormalizeSpeed(settings.NpcSpeed, 5);
            settings.NpcAttackMainCharacterSpeed = ResolveNpcAttackMainCharacterSpeed(json, settings);
            return settings;
        }
        catch (Exception ex)
        {
            logger.Warning("读取智能战斗倍速设置失败，使用默认设置：" + ex.Message);
            return Default;
        }
    }

    // 保存设置到 UserData。
    public void Save(MelonLogger.Instance logger)
    {
        try
        {
            MainCharacterSpeed = NormalizeSpeed(MainCharacterSpeed, 1);
            NpcSpeed = NormalizeSpeed(NpcSpeed, 5);
            NpcAttackMainCharacterSpeed = NormalizeSpeed(NpcAttackMainCharacterSpeed, NpcSpeed);

            string path = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            if (DebugLoggingEnabled)
            {
                logger.Msg($"智能战斗倍速设置已保存：主角 {MainCharacterSpeed}x，NPC {NpcSpeed}x，NPC攻击主角 {NpcAttackMainCharacterSpeed}x，智能模式={(SmartModeEnabled ? "开启" : "关闭")}，调试日志=开启。");
            }
        }
        catch (Exception ex)
        {
            logger.Warning("保存智能战斗倍速设置失败：" + ex.Message);
        }
    }

    // 返回配置文件路径。
    private static string GetConfigPath()
    {
        string userDataPath = Path.Combine(Application.dataPath, "..", "UserData");
        return Path.GetFullPath(Path.Combine(userDataPath, "SmartBattleSpeedMod_settings.json"));
    }

    // 限定到支持的倍速档位。
    private static int NormalizeSpeed(int speed, int fallback)
    {
        return speed is 1 or 2 or 3 or 5 or 10 ? speed : fallback;
    }

    // 兼容 v0.1.11 短暂使用过的旧字段名，并让更早配置默认跟随 NPC 倍速。
    private static int ResolveNpcAttackMainCharacterSpeed(string json, SmartBattleSpeedSettings settings)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty(nameof(NpcAttackMainCharacterSpeed), out JsonElement current)
            && current.TryGetInt32(out int currentSpeed))
        {
            return NormalizeSpeed(currentSpeed, settings.NpcSpeed);
        }

        if (root.TryGetProperty("NpcAttackPlayerSpeed", out JsonElement legacy)
            && legacy.TryGetInt32(out int legacySpeed))
        {
            return NormalizeSpeed(legacySpeed, settings.NpcSpeed);
        }

        return settings.NpcSpeed;
    }
}

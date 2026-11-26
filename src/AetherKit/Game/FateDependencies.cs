using System.Collections.Generic;
using System.Linq;

namespace AetherKit.Game;

/// <summary>自動Fate 用到的其他插件，給模組頁顯示有沒有開。</summary>
public static class FateDependencies
{
    public enum Level
    {
        /// <summary>沒開就不能用。</summary>
        Required,

        /// <summary>沒開也能跑，但會比較不穩。</summary>
        Recommended,

        /// <summary>開著會互相干擾。</summary>
        Conflict
    }

    public readonly record struct Entry(string Name, string Purpose, Level Level, bool Loaded);

    public static IEnumerable<Entry> Check()
    {
        yield return Make("vnavmesh", "尋路移動", Level.Required, "vnavmesh");
        // 輸出插件看設定選哪個，只有選中的那個是必要的
        if (Plugin.Instance.Config.AutoFate.Rotation == RotationPlugin.WrathCombo)
            yield return Make("Wrath Combo", "戰鬥輸出", Level.Required, "WrathCombo");
        else
            yield return Make("Rotation Solver Reborn", "戰鬥輸出", Level.Required, "RotationSolver");
        var bossMod = Make("BossMod Reborn", "躲 AOE", Level.Recommended, "BossModReborn", "BossMod");
        yield return bossMod;

        // BossMod 開著時再看它的 AI 有沒有開（AI 沒開就不會自己躲）；問不到就不顯示這行
        if (bossMod.Loaded && BossModAiActive() is { } active)
            yield return new Entry("BossMod AI", "開著才會自己跑出 AOE（在 BossMod 的 AI 設定裡開）", Level.Recommended, active);
    }

    private static bool? aiActive;
    private static System.DateTime aiCheckedAt;

    /// <summary>
    /// BossMod 的 AI 有沒有開：IPC「BossMod.AI.IsActive」（只讀）。設定頁每幀都會問，快取 1 秒；
    /// 問不到（版本不同、沒註冊）回 null。
    /// </summary>
    private static bool? BossModAiActive()
    {
        var now = System.DateTime.Now;
        if (now - aiCheckedAt < System.TimeSpan.FromSeconds(1)) return aiActive;

        aiCheckedAt = now;
        try
        {
            aiActive = Services.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsActive").InvokeFunc();
        }
        catch
        {
            aiActive = null;
        }

        return aiActive;
    }

    /// <summary>必要的插件都開著。</summary>
    public static bool RequiredReady => Check().All(e => e.Level != Level.Required || e.Loaded);

    private static Entry Make(string name, string purpose, Level level, params string[] internalNames) =>
        new(name, purpose, level, Loaded(internalNames));

    private static bool Loaded(string[] internalNames) =>
        Services.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && internalNames.Contains(p.InternalName));
}

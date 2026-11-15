using System.Collections.Generic;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 持有狀況模組：滑鼠指到物品時，在資訊框裡列出這個物品放在背包、雇員、鞍囊等哪裡、各有幾個。
/// </summary>
public sealed class OwnershipModule : IModule
{
    private readonly Ownership ownership;
    private readonly OwnershipTooltip tooltip;
    private readonly OwnershipSettings settings;
    private bool enabled;

    public OwnershipModule(Plugin plugin)
    {
        ownership = new Ownership(plugin);
        tooltip = new OwnershipTooltip(plugin, ownership);
        settings = new OwnershipSettings(plugin);
    }

    public string Id => "item-ownership";
    public string Name => "持有狀況";
    public string Description => "在物品資訊框裡列出這個物品放在哪裡、有幾個，包括雇員、鞍囊和投影台。";

    public void Enable(WindowSystem windows)
    {
        tooltip.Start();
        enabled = true;
    }

    public void Disable(WindowSystem windows)
    {
        tooltip.Stop();
        enabled = false;
    }

    // 資訊框沒開時也要記：打開雇員、鞍囊的時候不一定會去看物品
    public void BeforeWindows()
    {
        if (enabled)
            ownership.Track();
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", settings.Draw); }
    }

    public bool HasSettings(Configuration config) => true;

    // 只帶偏好，不帶更新時間和雇員名字 — 那是這台電腦、這些角色的紀錄
    public void CopySettings(Configuration from, Configuration to)
    {
        var lastSeen = to.ItemOwnership.LastSeen;
        var names = to.ItemOwnership.RetainerNames;

        to.ItemOwnership = new ItemOwnershipConfig
        {
            StaleDays = from.ItemOwnership.StaleDays,
            ShowAge = from.ItemOwnership.ShowAge,
            ShowTotal = from.ItemOwnership.ShowTotal,
            ShowWhenNone = from.ItemOwnership.ShowWhenNone,
            LastSeen = lastSeen,
            RetainerNames = names,
        };
    }

    public void Dispose() => tooltip.Dispose();
}

using System;
using AetherKit.Game;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AetherKit.UI;

/// <summary>持有狀況的設定：顯示方式，以及雇員、鞍囊、投影台的資料是多久前看到的。</summary>
public unsafe class OwnershipSettings
{
    private readonly Plugin plugin;

    public OwnershipSettings(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private ItemOwnershipConfig Config => plugin.Config.ItemOwnership;

    public void Draw()
    {
        Paint.SettingsHeading("顯示");

        var showAge = Config.ShowAge;
        if (ImGui.Checkbox("標示資料是多久前看到的", ref showAge))
        {
            Config.ShowAge = showAge;
            plugin.Save();
        }

        var showTotal = Config.ShowTotal;
        if (ImGui.Checkbox("右上角的持有數量補上全部的數量", ref showTotal))
        {
            Config.ShowTotal = showTotal;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("遊戲的持有數量只算身上的。雇員、鞍囊等地方也有時，改成「持有數量 12・全部 48」。");

        var showNone = Config.ShowWhenNone;
        if (ImGui.Checkbox("沒有持有時也顯示", ref showNone))
        {
            Config.ShowWhenNone = showNone;
            plugin.Save();
        }

        var days = Config.StaleDays;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("超過幾天沒更新就變灰色", ref days, 1, 60))
        {
            Config.StaleDays = days;
            plugin.Save();
        }

        DrawSeen();

        ImGui.Spacing();
        ImGui.TextDisabled("・資料跟遊戲右鍵「查看持有狀況」是同一份，背包、兵裝庫、裝備中是即時的。");
        ImGui.TextDisabled("・雇員、鞍囊、投影台要打開過才會更新；雇員在市場賣掉的東西要叫出雇員才會反映。");
        ImGui.TextDisabled("・收藏櫃要在這次登入打開過才讀得到。");
    }

    private void DrawSeen()
    {
        Paint.SettingsHeading("資料更新時間");

        var character = Services.PlayerState.ContentId;
        if (character == 0)
        {
            ImGui.TextDisabled("登入角色後才會顯示。");
            return;
        }

        var now = DateTime.UtcNow;

        Row("鞍囊", Ownership.SeenKey("saddlebag", character), now);
        Row("投影台", Ownership.SeenKey("dresser", character), now);

        // 列這個角色有快取的雇員；名字用記下來的，雇員資料要去過傳喚鈴才有
        var finder = ItemFinderModule.Instance();
        if (finder == null) return;

        foreach (var entry in finder->RetainerInventories)
        {
            var name = Config.RetainerNames.TryGetValue(entry.Item1, out var known) ? known : "雇員（還不知道名字）";
            Row(name, Ownership.SeenKey("retainer", entry.Item1), now);
        }
    }

    private void Row(string label, string key, DateTime now)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(160);

        if (Config.LastSeen.TryGetValue(key, out var seen))
            ImGui.TextUnformatted($"{OwnershipTooltip.Age(now - seen)}（{seen.ToLocalTime():yyyy/MM/dd HH:mm}）");
        else
            ImGui.TextDisabled("裝好插件後還沒打開過");
    }
}

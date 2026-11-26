using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AetherKit.Tweaks;

/// <summary>
/// 移除重複幻化：投影台裡同一件道具放了好幾份的，留第一份，其他取出到背包。
/// 預設染色不同的不算重複（可以在選項關掉）。
/// </summary>
public sealed unsafe class RemoveDuplicateGlamours : DresserTakeOutTweak
{
    public override string Id => "remove-duplicate-glamours";
    public override string Name => "移除重複幻化";
    public override string Description => "投影台加「取出重複」按鈕，同一件道具放了好幾份的只留一份，其他取出到背包。";

    protected override string StartLabel => "取出重複";

    protected override List<int> Targets(MirageManager* mirage)
    {
        var result = new List<int>();
        var seen = new HashSet<(uint Id, byte Stain0, byte Stain1)>();
        var keepDyed = Config.DresserDuplicateKeepDyed;

        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0) continue;

            var key = keepDyed
                ? (GlamourCommon.BaseId(ids[i]), mirage->PrismBoxStain0Ids[i], mirage->PrismBoxStain1Ids[i])
                : (GlamourCommon.BaseId(ids[i]), (byte)0, (byte)0);

            if (!seen.Add(key)) result.Add(i);
        }

        return result;
    }

    protected override Action QuickOptions => DrawSettings;

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var keep = Config.DresserDuplicateKeepDyed;
        if (ImGui.Checkbox("染色不同的不算重複", ref keep))
        {
            Config.DresserDuplicateKeepDyed = keep;
            Save();
        }
    }
}

/// <summary>
/// 移出可放收藏櫃的：投影台裡收藏櫃也收得下的道具取出到背包，之後可以改放收藏櫃，省投影台的格子。
/// </summary>
public sealed unsafe class RemoveArmoireItemsFromDresser : DresserTakeOutTweak
{
    public override string Id => "dresser-armoire-items";
    public override string Name => "移出可放收藏櫃的";
    public override string Description => "投影台加「取出可放收藏櫃的」按鈕，收藏櫃也收得下的道具取出到背包，之後可以改放收藏櫃。";

    protected override string StartLabel => "取出可放收藏櫃的";

    protected override List<int> Targets(MirageManager* mirage)
    {
        var result = new List<int>();
        var rows = GlamourCommon.CabinetRows;

        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] != 0 && rows.ContainsKey(GlamourCommon.BaseId(ids[i])))
                result.Add(i);
        }

        return result;
    }
}

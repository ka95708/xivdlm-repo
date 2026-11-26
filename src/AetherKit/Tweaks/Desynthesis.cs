using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 一鍵分解：分解清單上方加「全部分解」按鈕，照清單順序一件一件分解，可以略過 HQ。
///
/// 每一件：送「選這一項」給分解的 Agent → 分解確認視窗自動按確定 → 等分解結束（角色不在事件中）→ 下一件。
/// </summary>
public sealed unsafe class Desynthesis : LoopTweak
{
    private const string DialogAddon = "SalvageDialog";

    // 分解的 Agent，與清單視窗的資料位置：第 9 個值是件數，第 14 + 8i 個是第 i 件的名稱
    private const AgentId SalvageAgent = (AgentId)129;
    private const int CountIndex = 9;
    private const int FirstNameIndex = 14;
    private const int NameStride = 8;

    // HQ 道具名稱後面的圖示字元
    private const char HqGlyph = '';

    public override string Id => "desynthesis";
    public override string Name => "一鍵分解";
    public override string Description => "分解清單加「全部分解」按鈕，照順序一件一件分解，可略過 HQ。";
    public override TweakGroup Group => TweakGroup.Items;
    public override string Where => "分解清單上方";

    protected override string Addon => "SalvageItemSelector";
    protected override string StartLabel => "全部分解";

    // 選了一件之後，確認框最多等這麼久；連續幾次都沒跳出來就算卡住（背包滿了之類）
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private const int MaxMisses = 3;

    // 按了確認之後，分解動作開始前會有一小段空檔：最多等這麼久看到動作開始，才選下一件
    private static readonly TimeSpan EventStartTimeout = TimeSpan.FromSeconds(1.5);

    private DateTime? sentAt;
    private bool confirmed;
    private DateTime confirmedAt;
    private bool sawEvent;
    private int misses;

    protected override string? CanStart(AtkUnitBase* list)
    {
        sentAt = null;
        confirmed = false;
        sawEvent = false;
        misses = 0;
        return null;
    }

    /// <summary>
    /// 不用固定等待（跟 DR 一樣）：確認框一出現就按、分解動作一結束就選下一件。
    /// 同一疊分解好幾次時清單件數不會變，所以用「按了幾次確認」算完成數，不看清單件數。
    /// </summary>
    protected override string? Step(AtkUnitBase* list, DateTime now)
    {
        var dialog = Addons.Get(DialogAddon);
        if (dialog != null)
        {
            Addons.Fire(dialog, 0, 0);
            if (!confirmed)
            {
                CountDone();
                confirmedAt = now;
            }

            confirmed = true;
            misses = 0;
            Wait(Poll);
            return null;
        }

        // 分解動作中
        if (EventBusy)
        {
            sawEvent = true;
            Wait(Poll);
            return null;
        }

        if (sentAt is { } sent)
        {
            // 選了還沒看到確認框：再等一下
            if (!confirmed && now - sent < DialogTimeout)
            {
                Wait(Poll);
                return null;
            }

            // 按了確認、分解動作還沒開始：再等一下，免得太早選下一件
            if (confirmed && !sawEvent && now - confirmedAt < EventStartTimeout)
            {
                Wait(Poll);
                return null;
            }

            if (!confirmed && ++misses >= MaxMisses)
                return "分解沒有開始，背包可能滿了，停止";

            sentAt = null;
        }

        var count = Addons.ValueInt(list, CountIndex);
        var index = NextItem(list, count);
        if (index < 0) return $"分解完成，共 {Done} 件";

        Log.Write($"分解 第 {index} 項 {Addons.ValueText(list, FirstNameIndex + index * NameStride)}（清單 {count} 件）");
        Addons.SendAgentEvent(SalvageAgent, 0, 12, index);
        sentAt = now;
        confirmed = false;
        sawEvent = false;
        Wait(Poll);
        return null;
    }

    /// <summary>清單裡下一件要分解的（依設定略過 HQ）；沒有就是 -1。</summary>
    private int NextItem(AtkUnitBase* list, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var name = Addons.ValueText(list, FirstNameIndex + i * NameStride);
            if (Config.DesynthSkipHq && name.Contains(HqGlyph)) continue;

            return i;
        }

        return -1;
    }

    protected override Action QuickOptions => DrawSettings;

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var skip = Config.DesynthSkipHq;
        if (ImGui.Checkbox("略過 HQ 道具", ref skip))
        {
            Config.DesynthSkipHq = skip;
            Save();
        }
    }
}

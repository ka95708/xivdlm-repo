using System.Linq;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 天書奇談點格子：在天書奇談上點還沒完成的格子，直接打開任務搜尋器、選好那個副本（或隨機任務）。
/// 做法是在 16 個格子的按鈕上掛點擊事件；天書奇談關掉或重開時重新掛。
/// 格子對應哪個副本是依遊戲資料表推出來的，少數特殊格子（例如「任一深層迷宮」）對不到就不動作。
/// </summary>
public sealed unsafe class WeeklyBingoOpen : Tweak
{
    private const string Addon = "WeeklyBingo";
    private const int Slots = 16;

    // 格子按鈕的節點編號從 12 開始
    private const uint FirstSlotNode = 12;

    private readonly IAddonEventHandle?[] handles = new IAddonEventHandle?[Slots];
    private nint hookedAddon;

    public override string Id => "weekly-bingo-open";
    public override string Name => "天書奇談點格子";
    public override string Description => "點天書奇談上還沒完成的格子，直接打開任務搜尋器並選好那個副本。";
    public override TweakGroup Group => TweakGroup.GoldSaucer;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "天書奇談視窗的格子";

    protected override void OnDisable() => Unhook();

    public override void Update(System.DateTime now)
    {
        var addon = (AddonWeeklyBingo*)Addons.Get(Addon);
        if ((nint)addon == hookedAddon) return;

        Unhook();
        if (addon == null) return;

        for (var i = 0; i < Slots; i++)
        {
            var button = addon->DutySlotList[i].DutyButton;
            if (button == null || button->OwnerNode == null) continue;

            handles[i] = Services.AddonEvents.AddEvent((nint)addon, (nint)button->OwnerNode, AddonEventType.MouseClick, OnClick);
        }

        hookedAddon = (nint)addon;
    }

    private void Unhook()
    {
        for (var i = 0; i < Slots; i++)
        {
            if (handles[i] is { } handle) Services.AddonEvents.RemoveEvent(handle);
            handles[i] = null;
        }

        hookedAddon = 0;
    }

    private void OnClick(AddonEventType type, AddonEventData data)
    {
        var node = (AtkResNode*)data.NodeTargetPointer;
        if (node == null || Services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) return;

        var index = (int)(node->NodeId - FirstSlotNode);
        if (index is < 0 or >= Slots) return;

        var state = PlayerState.Instance();
        if (state->GetWeeklyBingoTaskStatus(index) != PlayerState.WeeklyBingoTaskStatus.Open) return;

        var finder = AgentContentsFinder.Instance();
        if (finder == null) return;

        var order = state->WeeklyBingoOrderData[index];

        if (BingoDuties.Roulette(order) is { } roulette)
        {
            finder->OpenRouletteDuty(roulette);
            return;
        }

        if (BingoDuties.Regular(order) is { } duty)
        {
            finder->OpenRegularDuty(duty);
            return;
        }

        Notify.Info("天書奇談", "這一格沒有對應到特定副本，請自己在任務搜尋器裡選。");
    }
}

/// <summary>天書奇談的格子（WeeklyBingoOrderData）對應到任務搜尋器的哪個副本或隨機任務。</summary>
internal static class BingoDuties
{
    /// <summary>對應到隨機任務的格子：隨機任務編號。</summary>
    public static byte? Roulette(uint order) => order switch
    {
        54 => 7,
        52 => 40,
        _ => null
    };

    /// <summary>對應到一般副本的格子：ContentFinderCondition 的列編號。</summary>
    public static uint? Regular(uint order)
    {
        var sheet = Services.Data.GetExcelSheet<WeeklyBingoOrderData>();
        if (sheet.GetRowOrDefault(order) is not { } row) return null;

        var data = row.Data.RowId;
        var result = row.Type switch
        {
            // 指定副本
            0 => First(c => c.Content.RowId == data && UIState.IsInstanceContentUnlocked(data)),
            // 指定等級的迷宮
            1 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired == data),
            // 等級區間的迷宮
            2 => data == 49 ? 56u : First(c => c.ContentType.RowId == 2
                                               && c.ClassJobLevelRequired >= data - (data > 50 ? 9 : 49)
                                               && c.ClassJobLevelRequired <= data - 1),
            3 => order switch
            {
                53 => First(c => c.ContentType.RowId == 21),
                67 => 599u,
                _ => 0u
            },
            4 => Raid(data),
            5 => data switch
            {
                49 => 56u,
                79 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired is >= 51 and <= 79 && c.ClassJobLevelRequired % 10 != 0),
                99 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired is >= 81 and <= 99 && c.ClassJobLevelRequired % 10 != 0),
                _ => 0u
            },
            6 => data switch
            {
                60 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired is 50 or 60),
                80 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired is 70 or 80),
                90 => First(c => c.ContentType.RowId == 2 && c.ClassJobLevelRequired == 90),
                _ => 0u
            },
            // 討伐戰
            7 => data switch
            {
                60 => First(c => c.ContentType.RowId == 4 && c.ClassJobLevelRequired is >= 50 and <= 60),
                100 => First(c => c.ContentType.RowId == 4 && c.ClassJobLevelRequired is >= 70 and <= 100),
                _ => 0u
            },
            // 團隊任務
            8 => data switch
            {
                60 => First(c => c.ContentType.RowId == 5 && c.AllianceRoulette && c.ClassJobLevelRequired is >= 50 and <= 60),
                90 => First(c => c.ContentType.RowId == 5 && c.AllianceRoulette && c.ClassJobLevelRequired is >= 70 and <= 90),
                _ => 0u
            },
            // 大型任務
            9 => data switch
            {
                60 => First(c => c.ContentType.RowId == 5 && c.NormalRaidRoulette && c.ClassJobLevelRequired is >= 50 and <= 60),
                100 => First(c => c.ContentType.RowId == 5 && c.NormalRaidRoulette && c.ClassJobLevelRequired is >= 70 and <= 100),
                _ => 0u
            },
            _ => 0u
        };

        return result == 0 ? null : result;
    }

    private static uint First(System.Func<ContentFinderCondition, bool> match) =>
        Services.Data.GetExcelSheet<ContentFinderCondition>()
            .Where(match)
            .OrderBy(c => c.SortKey)
            .Select(c => c.RowId)
            .FirstOrDefault();

    /// <summary>大型任務系列格子（第幾系列）→ 系列第一個副本。</summary>
    private static uint Raid(uint data) => data switch
    {
        2 => 93, 3 => 98, 4 => 107, 5 => 112, 6 => 136, 7 => 186, 8 => 252, 9 => 286, 10 => 587,
        11 => 653, 12 => 682, 13 => 715, 14 => 726, 15 => 749, 16 => 751, 17 => 808, 18 => 807,
        19 => 872, 20 => 876, 21 => 936, 22 => 941, 23 => 653, 24 => 715, 25 => 749, 26 => 92,
        27 => 120, 28 => 281, 29 => 700, 30 => 866, 31 => 808, 32 => 872, 33 => 936, 34 => 985,
        35 => 989, 36 => 985, 37 => 1019, 38 => 1023,
        _ => 0
    };
}

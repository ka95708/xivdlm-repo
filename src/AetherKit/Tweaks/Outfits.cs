using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 套裝組合：「組合套裝」視窗（MiragePrismPrismSetConvert）加「放入並組合」按鈕，
/// 把每個空著的部件格一格一格放入（跟點格子、在跳出的道具選單選第一個一樣），全部放好後按「組合」、確認。
/// 部件要在背包或兵裝庫裡；還在投影台裡的，先用「取出套裝部件」取出來。
/// </summary>
public sealed unsafe class OutfitCombine : LoopTweak
{
    private const string ConfirmAddon = "MiragePrismPrismSetConvertC";
    private const string IconMenuAddon = "ContextIconMenu";

    // 組合視窗的資料：第 1 個值是持有的幻化稜晶，第 15 個是部件數（也是要用的稜晶數），
    // 部件從第 16 個開始、每件 7 個值，第 4 個是放入的來源欄位（9999 = 還沒放）
    private const int PrismsHeldIndex = 1;
    private const int CountIndex = 15;
    private const int FirstItemIndex = 16;
    private const int ItemStride = 7;
    private const int SourceOffset = 4;
    private const int Unfilled = 9999;

    // 點部件格的 callback；道具選單選第一個的 callback 參數
    private const int FillSlotCallback = 13;
    private const uint IconMenuAction = 1021003;

    // 「組合」按鈕、確認視窗「是」按鈕的節點編號
    private const uint StoreButtonNode = 27;
    private const uint ConfirmYesNode = 6;

    // 按了「組合」後最多等幾次確認視窗
    private const int MaxSubmitWaits = 5;

    private bool submitted;
    private int submitWaits;

    public override string Id => "outfit-combine";
    public override string Name => "套裝組合";
    public override string Description => "組合套裝視窗加「放入並組合」按鈕，自動放入所有部件後按組合並確認。";
    public override TweakGroup Group => TweakGroup.Glamour;
    public override string Where => "投影台的組合套裝視窗上方";

    protected override string Addon => "MiragePrismPrismSetConvert";
    protected override string StartLabel => "放入並組合";

    protected override string? CanStart(AtkUnitBase* addon)
    {
        submitted = false;
        submitWaits = 0;
        return Addons.ValueInt(addon, CountIndex) <= 0 ? "視窗裡沒有部件" : null;
    }

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var confirm = Addons.Get(ConfirmAddon);
        if (confirm != null)
        {
            if (!Addons.ClickButton(confirm, ConfirmYesNode)) Addons.Fire(confirm, 0);
            return $"已送出組合（這次放入 {Done} 件）";
        }

        var menu = Addons.Get(IconMenuAddon);
        if (menu != null)
        {
            Addons.Fire(menu, 0, 0, IconMenuAction, 0u, 0);
            Wait(TimeSpan.FromMilliseconds(300));
            return null;
        }

        if (EventBusy)
        {
            Wait(TimeSpan.FromMilliseconds(200));
            return null;
        }

        var total = Addons.ValueInt(addon, CountIndex);
        var unfilled = UnfilledSlots(addon, total);

        if (unfilled.Count > 0)
        {
            if (!Progress(unfilled.Count))
                return "有部件放不進去，停止。背包和兵裝庫裡要有這件；還在投影台裡的先用「取出套裝部件」取出";

            Log.Write($"放入第 {unfilled[0]} 格（還有 {unfilled.Count} 格空著）");
            Addons.Fire(addon, FillSlotCallback, unfilled[0]);
            Wait(TimeSpan.FromMilliseconds(400));
            return null;
        }

        // 全部放好了：最後一件也算進去
        Progress(0);

        if (submitted)
        {
            if (++submitWaits > MaxSubmitWaits) return "按了組合但沒有跳出確認視窗，停止";
            Wait(TimeSpan.FromMilliseconds(500));
            return null;
        }

        var prisms = Addons.ValueInt(addon, PrismsHeldIndex);
        if (prisms < total) return $"幻化稜晶不夠（需要 {total} 個，身上 {prisms} 個），停止";

        Log.Write($"按組合（{total} 件）");
        if (!Addons.ClickButton(addon, StoreButtonNode)) return "「組合」按鈕不能按，停止";

        submitted = true;
        Wait(TimeSpan.FromMilliseconds(800));
        return null;
    }

    private static List<int> UnfilledSlots(AtkUnitBase* addon, int total)
    {
        var result = new List<int>();
        for (var i = 0; i < total; i++)
        {
            if (Addons.ValueInt(addon, FirstItemIndex + i * ItemStride + SourceOffset) == Unfilled)
                result.Add(i);
        }

        return result;
    }
}

/// <summary>
/// 取出套裝部件：投影台裡湊得成套裝（MirageStoreSetItem）的部件取出到背包，好拿去「套裝組合」。
/// 按下去時先排好要取哪些，再一件一件取（取出一件後那套就不完整了，不能邊取邊重算）。
/// 投影台裡已經有那套套裝的不動；預設只取湊齊一整套的、有染色的不取。
/// </summary>
public sealed unsafe class OutfitTakeOut : DresserTakeOutTweak
{
    private sealed record OutfitSet(uint SetId, uint[] Pieces);

    private static List<OutfitSet>? sets;

    private List<(uint Id, byte Stain0, byte Stain1)> plan = new();

    public override string Id => "outfit-take-out";
    public override string Name => "取出套裝部件";
    public override string Description => "投影台加「取出套裝部件」按鈕，湊得成套裝的部件取出到背包，好拿去組合套裝。";

    protected override string StartLabel => "取出套裝部件";

    protected override string? CanStart(AtkUnitBase* addon)
    {
        var mirage = GlamourCommon.PrismBox();
        if (mirage == null) return "投影台資料還沒讀到，等視窗載入完再按";

        plan = BuildPlan(mirage);
        return plan.Count == 0 ? "沒有湊得成套裝的部件" : base.CanStart(addon);
    }

    protected override List<int> Targets(MirageManager* mirage)
    {
        var result = new List<int>();
        var used = new HashSet<int>();
        var ids = mirage->PrismBoxItemIds;

        foreach (var (id, stain0, stain1) in plan)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] != id || mirage->PrismBoxStain0Ids[i] != stain0 || mirage->PrismBoxStain1Ids[i] != stain1) continue;
                if (!used.Add(i)) continue;

                result.Add(i);
                break;
            }
        }

        return result;
    }

    /// <summary>照設定挑出要取的部件（道具編號含 HQ、兩個染色）。</summary>
    private List<(uint Id, byte Stain0, byte Stain1)> BuildPlan(MirageManager* mirage)
    {
        var result = new List<(uint, byte, byte)>();
        var used = new HashSet<int>();
        var ids = mirage->PrismBoxItemIds;
        var completeOnly = Config.OutfitCompleteOnly;
        var skipDyed = Config.OutfitSkipDyed;

        var stored = new HashSet<uint>();
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] != 0) stored.Add(GlamourCommon.BaseId(ids[i]));
        }

        foreach (var set in Sets())
        {
            // 投影台裡已經有這套套裝了
            if (stored.Contains(set.SetId)) continue;

            var found = new List<int>();
            foreach (var piece in set.Pieces)
            {
                var index = FindPiece(mirage, piece, skipDyed, used);
                if (index >= 0) found.Add(index);
            }

            if (found.Count == 0 || (completeOnly && found.Count < set.Pieces.Length)) continue;

            foreach (var index in found)
            {
                used.Add(index);
                result.Add((ids[index], mirage->PrismBoxStain0Ids[index], mirage->PrismBoxStain1Ids[index]));
            }
        }

        return result;
    }

    private static int FindPiece(MirageManager* mirage, uint piece, bool skipDyed, HashSet<int> used)
    {
        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0 || GlamourCommon.BaseId(ids[i]) != piece || used.Contains(i)) continue;
            if (skipDyed && (mirage->PrismBoxStain0Ids[i] != 0 || mirage->PrismBoxStain1Ids[i] != 0)) continue;

            return i;
        }

        return -1;
    }

    /// <summary>遊戲裡所有套裝和它的部件（MirageStoreSetItem 表）。</summary>
    private static List<OutfitSet> Sets()
    {
        if (sets != null) return sets;

        sets = new List<OutfitSet>();
        foreach (var row in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.MirageStoreSetItem>())
        {
            var pieces = new List<uint>();
            foreach (var id in new[]
                     {
                         row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId, row.Hands.RowId,
                         row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId, row.Necklace.RowId, row.Bracelets.RowId,
                         row.Ring.RowId
                     })
            {
                if (id > 1) pieces.Add(id);
            }

            if (pieces.Count > 0) sets.Add(new OutfitSet(row.RowId, pieces.ToArray()));
        }

        return sets;
    }

    protected override Action QuickOptions => DrawSettings;

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var complete = Config.OutfitCompleteOnly;
        if (ImGui.Checkbox("只取湊齊一整套的", ref complete))
        {
            Config.OutfitCompleteOnly = complete;
            Save();
        }

        var skipDyed = Config.OutfitSkipDyed;
        if (ImGui.Checkbox("有染色的不取", ref skipDyed))
        {
            Config.OutfitSkipDyed = skipDyed;
            Save();
        }
    }
}

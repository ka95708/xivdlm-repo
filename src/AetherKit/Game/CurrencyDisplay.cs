using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetherKit.Core;

namespace AetherKit.Game;

/// <summary>
/// 更多貨幣顯示（Simple Tweaks 的 Expanded Currency Display）：在遊戲右下角的金幣視窗（_Money）裡，
/// 照設定的順序再加幾列「圖示＋數字」，用遊戲自己的圖示節點和數字節點，樣式跟金幣那一列一模一樣。
///
/// 做法照 Simple Tweaks 建節點，但不攔截遊戲函式、不放大視窗、不做滑鼠提示：
/// - 每 0.25 秒（遊戲執行緒）確保節點都在、位置對、數量是最新的；設定改了就整批拆掉重建。
/// - 開著 HUD 版面配置時先拆掉（版面配置會搬動金幣視窗），關掉再建回來。
/// - 金幣視窗被遊戲重建（換位址）時，舊節點跟著舊視窗走，只是忘掉不再碰它。
/// 這整套都是改記憶體（建節點、接進視窗的節點樹），列在改版檢查清單。
/// </summary>
public sealed unsafe class CurrencyDisplay : IDisposable
{
    private const string Addon = "_Money";

    // 金幣視窗裡原本的節點：數字（2）、圖示（3）
    private const uint MoneyCounterNode = 2;
    private const uint MoneyIconNode = 3;

    // 一列的大小（跟金幣那一列一樣）
    private static readonly Vector2 RowSize = new(156, 36);

    // 自己的節點編號（避開遊戲原本的節點）
    private const uint IconNodeBase = 31000;
    private const uint CounterNodeBase = 31100;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Plugin plugin;
    private readonly FeatureLog log = FeatureLog.For("currency");

    /// <summary>自己建的節點（拆的時候要用）：圖示節點、數字節點。</summary>
    private readonly List<(nint Icon, nint Counter)> nodes = new();

    private nint builtFor;
    private string builtKey = string.Empty;
    private DateTime next;

    public CurrencyDisplay(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private CurrencyDisplayConfig Config => plugin.Config.CurrencyDisplay;

    /// <summary>模組啟用中。</summary>
    public bool Active { get; private set; }

    public void Start()
    {
        if (Active) return;
        Active = true;
        Services.Framework.Update += OnUpdate;
    }

    public void Stop()
    {
        if (!Active) return;
        Active = false;
        Services.Framework.Update -= OnUpdate;
        Services.Framework.RunOnFrameworkThread(Remove);
    }

    /// <summary>設定改了：下一次更新整批重建。</summary>
    public void Refresh() => builtKey = string.Empty;

    public void Dispose() => Stop();

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.Now;
        if (now < next) return;
        next = now + Interval;

        try
        {
            Tick();
        }
        catch (Exception e)
        {
            log.Write($"錯誤：{e}");
            Services.Log.Error(e, "更多貨幣顯示發生錯誤");
        }
    }

    private void Tick()
    {
        var money = Addons.Get(Addon);

        // 金幣視窗不在、或正在調整 HUD 版面：先拆掉
        if (money == null || Addons.IsOpen("HudLayout") || !Services.ClientState.IsLoggedIn)
        {
            if (money != null && (nint)money == builtFor) Remove();
            else Forget();
            return;
        }

        // 金幣視窗被重建了：舊節點跟著舊視窗，忘掉就好
        if ((nint)money != builtFor) Forget();

        var key = Key();
        if (key != builtKey)
        {
            Remove();
            Build(money);
            builtKey = key;
        }

        UpdateCounts();
    }

    /// <summary>設定的指紋：順序、開關、上方或下方任何一個變了都要重建。</summary>
    private string Key()
    {
        var parts = new List<string> { Config.Area.ToString() };
        foreach (var c in Config.Currencies)
        {
            if (c.Enabled) parts.Add(c.ItemId.ToString());
        }

        return string.Join("|", parts);
    }

    // ── 建立、更新、拆除 ──

    // [改記憶體] 在金幣視窗裡建圖示、數字節點並接進節點樹（同 Simple Tweaks，見 改版檢查清單.md）
    private void Build(AtkUnitBase* money)
    {
        var moneyIcon = money->GetNodeById(MoneyIconNode);
        var moneyCounter = (AtkCounterNode*)money->GetNodeById(MoneyCounterNode);
        if (moneyIcon == null || moneyCounter == null || money->RootNode == null || money->RootNode->ChildNode == null) return;

        // 上一次載入插件時建的節點如果沒拆乾淨（例如卸載時來不及在遊戲執行緒拆），會一直留在金幣視窗裡，
        // 重新載入一次就多疊一層。先照節點編號把它們找出來拆掉
        RemoveLeftovers(money);

        var iconPos = new Vector2(moneyIcon->X, moneyIcon->Y);
        var counterPos = new Vector2(moneyCounter->AtkResNode.X, moneyCounter->AtkResNode.Y);
        // 清單一律照畫面由上往下：放上方時最後一個貼著金幣，放下方時第一個貼著金幣
        var shown = Config.Currencies.Count(c => c.Enabled);
        var index = 0u;
        foreach (var currency in Config.Currencies)
        {
            if (!currency.Enabled) continue;

            index++;
            var row = Config.Area == CurrencyArea.Above ? -(shown - (int)index + 1) : (int)index;
            var offset = new Vector2(0, RowSize.Y * row);
            var icon = Services.Data.GetExcelSheet<Item>().GetRowOrDefault(currency.ItemId)?.Icon ?? 0;

            var counter = MakeCounter(CounterNodeBase + index, counterPos + offset, moneyCounter->PartsList);
            var image = MakeIcon(IconNodeBase + index, iconPos + offset, icon);
            if (counter != null) LinkAtEnd(money, (AtkResNode*)counter);
            if (image != null) LinkAtEnd(money, (AtkResNode*)image);

            nodes.Add(((nint)image, (nint)counter));
        }

        money->UldManager.UpdateDrawNodeList();
        builtFor = (nint)money;
        log.Write($"建立 {nodes.Count} 列貨幣（金幣{(Config.Area == CurrencyArea.Above ? "上方" : "下方")}）");
    }

    private void UpdateCounts()
    {
        var manager = InventoryManager.Instance();
        var i = 0;
        foreach (var currency in Config.Currencies)
        {
            if (!currency.Enabled) continue;
            if (i >= nodes.Count) break;

            var counter = (AtkCounterNode*)nodes[i++].Counter;
            if (counter == null) continue;

            var count = manager->GetInventoryItemCount(currency.ItemId, false, true, true, 0);
            counter->SetText(count.ToString("n0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>拆掉自己建的節點並釋放記憶體（遊戲執行緒）。</summary>
    private void Remove()
    {
        var money = Addons.Get(Addon);
        if (money == null || (nint)money != builtFor)
        {
            Forget();
            return;
        }

        foreach (var (icon, counter) in nodes)
        {
            if (icon != 0) FreeIcon(money, (AtkImageNode*)icon);
            if (counter != 0) FreeCounter(money, (AtkCounterNode*)counter);
        }

        money->UldManager.UpdateDrawNodeList();
        if (nodes.Count > 0) log.Write($"拆掉 {nodes.Count} 列貨幣");
        Forget();
    }

    /// <summary>金幣視窗裡所有用我們編號（不在這次的清單裡）的節點：之前載入時留下來的，拆掉並釋放。</summary>
    private void RemoveLeftovers(AtkUnitBase* money)
    {
        var mine = new HashSet<nint>();
        foreach (var (icon, counter) in nodes)
        {
            mine.Add(icon);
            mine.Add(counter);
        }

        var icons = new List<nint>();
        var counters = new List<nint>();

        for (var node = money->RootNode->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (mine.Contains((nint)node)) continue;

            if (node->NodeId is > IconNodeBase and < CounterNodeBase && node->Type == NodeType.Image)
                icons.Add((nint)node);
            else if (node->NodeId is > CounterNodeBase and < CounterNodeBase + IconNodeRange && node->Type == NodeType.Counter)
                counters.Add((nint)node);
        }

        foreach (var icon in icons) FreeIcon(money, (AtkImageNode*)icon);
        foreach (var counter in counters) FreeCounter(money, (AtkCounterNode*)counter);

        if (icons.Count + counters.Count > 0)
            log.Write($"拆掉之前留下的 {icons.Count} 個圖示、{counters.Count} 個數字節點");
    }

    private const uint IconNodeRange = 100;

    private void Forget()
    {
        nodes.Clear();
        builtFor = 0;
        builtKey = string.Empty;
    }

    private static AtkCounterNode* MakeCounter(uint id, Vector2 pos, AtkUldPartsList* parts)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkCounterNode>();
        if (node == null) return null;

        node->AtkResNode.Type = NodeType.Counter;
        node->AtkResNode.NodeId = id;
        node->AtkResNode.NodeFlags = (NodeFlags)8243;
        node->AtkResNode.DrawFlags = 0;
        node->NumberWidth = 10;
        node->CommaWidth = 8;
        node->SpaceWidth = 6;
        node->TextAlign = 5;
        node->PartsList = parts; // 用金幣那一列的數字圖片（不是自己的，拆的時候不釋放）
        node->AtkResNode.SetWidth(128);
        node->AtkResNode.SetHeight(22);
        node->AtkResNode.SetPositionFloat(pos.X, pos.Y);
        node->AtkResNode.ToggleVisibility(true);
        return node;
    }

    private static AtkImageNode* MakeIcon(uint id, Vector2 pos, uint iconId)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
        if (node == null) return null;

        var parts = (AtkUldPartsList*)IMemorySpace.GetUISpace()->Malloc((ulong)Unsafe.SizeOf<AtkUldPartsList>(), 8);
        var part = (AtkUldPart*)IMemorySpace.GetUISpace()->Malloc((ulong)Unsafe.SizeOf<AtkUldPart>(), 8);
        var asset = (AtkUldAsset*)IMemorySpace.GetUISpace()->Malloc((ulong)Unsafe.SizeOf<AtkUldAsset>(), 8);
        if (parts == null || part == null || asset == null)
        {
            if (asset != null) IMemorySpace.Free(asset, (ulong)Unsafe.SizeOf<AtkUldAsset>());
            if (part != null) IMemorySpace.Free(part, (ulong)Unsafe.SizeOf<AtkUldPart>());
            if (parts != null) IMemorySpace.Free(parts, (ulong)Unsafe.SizeOf<AtkUldPartsList>());
            node->AtkResNode.Destroy(false);
            IMemorySpace.Free(node, (ulong)Unsafe.SizeOf<AtkImageNode>());
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();
        part->UldAsset = asset;
        part->U = 0;
        part->V = 0;
        part->Width = 36;
        part->Height = 36;
        parts->Id = 0;
        parts->PartCount = 1;
        parts->Parts = part;

        node->AtkResNode.Type = NodeType.Image;
        node->AtkResNode.NodeId = id;
        node->AtkResNode.NodeFlags = (NodeFlags)8243;
        node->AtkResNode.DrawFlags = 0;
        node->WrapMode = 1;
        node->Flags = 128;
        node->PartsList = parts;
        node->LoadIconTexture(iconId, 0);
        node->AtkResNode.SetWidth(36);
        node->AtkResNode.SetHeight(36);
        node->AtkResNode.SetPositionFloat(pos.X, pos.Y);
        node->AtkResNode.ToggleVisibility(true);
        return node;
    }

    /// <summary>接在視窗根節點底下第一層的最後面（跟 Simple Tweaks 一樣）。</summary>
    private static void LinkAtEnd(AtkUnitBase* addon, AtkResNode* node)
    {
        var last = addon->RootNode->ChildNode;
        while (last->PrevSiblingNode != null) last = last->PrevSiblingNode;

        last->PrevSiblingNode = node;
        node->NextSiblingNode = last;
        node->PrevSiblingNode = null;
        node->ParentNode = last->ParentNode;
    }

    private static void Unlink(AtkResNode* node)
    {
        if (node->PrevSiblingNode != null) node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
        if (node->NextSiblingNode != null) node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
        if (node->ParentNode != null && node->ParentNode->ChildNode == node) node->ParentNode->ChildNode = node->NextSiblingNode;
        node->PrevSiblingNode = node->NextSiblingNode = null;
    }

    private static void FreeIcon(AtkUnitBase* addon, AtkImageNode* node)
    {
        Unlink(&node->AtkResNode);
        addon->UldManager.UpdateDrawNodeList();

        node->UnloadTexture();
        var parts = node->PartsList;
        node->PartsList = null;
        node->AtkResNode.Destroy(false);
        IMemorySpace.Free(node, (ulong)Unsafe.SizeOf<AtkImageNode>());

        if (parts == null) return;
        if (parts->Parts != null)
        {
            if (parts->Parts->UldAsset != null) IMemorySpace.Free(parts->Parts->UldAsset, (ulong)Unsafe.SizeOf<AtkUldAsset>());
            IMemorySpace.Free(parts->Parts, (ulong)Unsafe.SizeOf<AtkUldPart>());
        }

        IMemorySpace.Free(parts, (ulong)Unsafe.SizeOf<AtkUldPartsList>());
    }

    private static void FreeCounter(AtkUnitBase* addon, AtkCounterNode* node)
    {
        Unlink(&node->AtkResNode);
        addon->UldManager.UpdateDrawNodeList();

        node->PartsList = null; // 金幣那一列的，不是自己的
        node->AtkResNode.Destroy(false);
        IMemorySpace.Free(node, (ulong)Unsafe.SizeOf<AtkCounterNode>());
    }
}

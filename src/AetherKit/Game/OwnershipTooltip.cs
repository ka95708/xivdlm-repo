using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace AetherKit.Game;

/// <summary>
/// 在物品資訊框（ItemDetail）最下面加一段持有狀況；身上以外的地方也有時，
/// 右上角的「持有數量」再補上全部的數量。
///
/// 持有狀況用自己的文字節點：遊戲原本最下面那幾行是單行的，接在後面不會換行。
/// 做法跟 PriceInsight 一樣 — 節點放在資訊框底部，把資訊框拉高。PriceInsight 的市場價格
/// 也放在底部，這裡固定排在它上面：它先加就把它往下推，它後加自然會排到下面。
/// 不用找函式位置，台服跟國際服的版本不同，找位置的做法容易對不上。
/// </summary>
public sealed unsafe class OwnershipTooltip : IDisposable
{
    private const string Addon = "ItemDetail";

    /// <summary>ItemDetail 的字串陣列在 AtkArrayDataHolder 裡的位置（StringArrayType.ItemDetail）。</summary>
    private const int StringArrayIndex = 27;

    /// <summary>右上角「12/999 (持有數量 12)」或「(持有數量：0)」在字串陣列裡的位置。</summary>
    private const int QuantityIndex = 14;

    /// <summary>自己的文字節點；PriceInsight 用 32612，避開它。</summary>
    private const uint NodeId = 32613;
    private const uint PriceInsightNodeId = 32612;

    /// <summary>資訊框裡要排在持有狀況下面的節點（底下的操作說明）。</summary>
    private const uint InsertNodeId = 2;

    /// <summary>拿來抄字的顏色、外框的一般文字節點。</summary>
    private const uint StyleNodeId = 44;

    /// <summary>跟上面內容之間多空的距離，以及節點下方的留白。</summary>
    private const int Gap = 6;
    private const int Padding = 4;

    /// <summary>一行大約放幾個全形字，超過就換行（半形算半個）。</summary>
    private const float LineWidth = 26;

    /// <summary>一般的物品 ID 都比這小；HQ 加 1000000、收藏品加 500000，再大是任務道具。</summary>
    private const ulong CollectibleOffset = 500_000;
    private const ulong EventItemStart = 2_000_000;

    private readonly Plugin plugin;
    private readonly Ownership ownership;
    private readonly Field quantity = new(QuantityIndex);
    private bool listening;

    /// <summary>上次把資訊框拉高了多少；下次更新前要先收回來，不然會越拉越高。</summary>
    private int added;

    /// <summary>
    /// 啟用後第一次碰到資訊框時，先清掉上次載入留下的節點（例如舊版沒清乾淨的），
    /// 之後才只管自己這一個。
    /// </summary>
    private bool cleaned;

    // 拉高之後資訊框的高度、底下節點的位置；收回時確認還是這兩個值才動
    private ushort grownHeight;
    private float movedInsertY;

    public OwnershipTooltip(Plugin plugin, Ownership ownership)
    {
        this.plugin = plugin;
        this.ownership = ownership;
    }

    private ItemOwnershipConfig Config => plugin.Config.ItemOwnership;

    public void Start()
    {
        if (listening) return;
        cleaned = false;
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, Addon, OnPreUpdate);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, Addon, OnPostUpdate);
        listening = true;
    }

    public void Stop()
    {
        if (!listening) return;
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, Addon, OnPreUpdate);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, Addon, OnPostUpdate);
        listening = false;
        quantity.Forget();
        // 卸載插件時不一定在遊戲的主執行緒，動節點要回到主執行緒做
        Services.Framework.RunOnFrameworkThread(RemoveNode);
    }

    private void OnPreUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon != null)
                Restore(addon);

            if (args is AddonRequestedUpdateArgs update)
                UpdateQuantity(update);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "持有狀況：更新物品資訊框前處理失敗");
        }
    }

    private void OnPostUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon != null)
                ShowOwnership(addon);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "持有狀況：在物品資訊框加上持有狀況失敗");
        }
    }

    private static uint HoveredItem()
    {
        var hovered = Services.GameGui.HoveredItem;
        if (hovered == 0 || hovered >= EventItemStart) return 0;

        return (uint)(hovered % CollectibleOffset);
    }

    // ── 右上角的持有數量 ──

    private void UpdateQuantity(AddonRequestedUpdateArgs update)
    {
        var arrays = (StringArrayData**)update.StringArrayData;
        if (arrays == null) return;

        var strings = arrays[StringArrayIndex];
        if (strings == null || strings->Size <= QuantityIndex) return;

        var itemId = HoveredItem();
        var entries = itemId == 0 ? new List<OwnershipEntry>() : ownership.Find(itemId);

        // 遊戲的持有數量只算身上的；別的地方也有才補全部，只有身上有就維持原樣
        var showTotal = Config.ShowTotal && entries.Any(e => !e.OnHand);
        var total = entries.Sum(e => e.Total);
        quantity.Apply(strings, showTotal ? original => WithTotal(original, total) : null);
    }

    /// <summary>「(持有數量 12)」→「(持有數量 12・全部 48)」；找不到右括號就接在最後面。</summary>
    private static byte[] WithTotal(byte[] original, int total)
    {
        var close = Array.LastIndexOf(original, (byte)')');

        if (close < 0)
            return original.Concat(Encoding.UTF8.GetBytes(" (全部 " + total + ")")).ToArray();

        var insert = Encoding.UTF8.GetBytes("・全部 " + total);
        return original[..close].Concat(insert).Concat(original[close..]).ToArray();
    }

    // ── 最下面的持有狀況 ──

    private void ShowOwnership(AtkUnitBase* addon)
    {
        // 同一次更新可能收到不只一次，先把上次加的收回來
        Restore(addon);

        var itemId = HoveredItem();
        if (itemId == 0) return;

        var text = Describe(ownership.Find(itemId));
        if (text == null) return;

        var window = addon->WindowNode;
        var insert = addon->GetNodeById(InsertNodeId);
        if (window == null || insert == null) return;

        var node = FindNode(addon);
        if (node == null)
            node = CreateNode(addon, insert);
        if (node == null) return;

        var bytes = text.Value.Data.ToArray();
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);

        node->SetText(terminated);
        node->AtkResNode.ToggleVisibility(true);
        node->ResizeNodeForCurrentText();

        var height = node->AtkResNode.Height;
        added = height + Gap + Padding;

        // PriceInsight 已經放好的話，佔它的位置、把它往下推；不然就放在最底下
        var price = FindListedNode(addon, PriceInsightNodeId);
        float y;

        if (price != null && price->IsVisible())
        {
            y = price->Y;
            price->SetYFloat(price->Y + added);
        }
        else
        {
            y = window->AtkResNode.Height - 8;
        }

        node->AtkResNode.SetYFloat(y + Gap);

        var before = window->AtkResNode.Height;
        grownHeight = (ushort)(before + added);
        movedInsertY = insert->Y + added;

        SetWindowHeight(addon, grownHeight);
        insert->SetYFloat(movedInsertY);

    }

    /// <summary>
    /// 把上次拉高的收回來，節點先藏起來。遊戲重新排版前、以及每次要加之前都做，免得一直往下疊。
    /// 只收回還維持我們設的值的部分：遊戲已經自己重新排過版的話，那部分就不動，免得縮過頭。
    /// </summary>
    private void Restore(AtkUnitBase* addon)
    {
        if (!cleaned)
        {
            RemoveAllNodes(addon);
            cleaned = true;
            added = 0;
            return;
        }

        var node = FindNode(addon);
        if (node != null)
            node->AtkResNode.ToggleVisibility(false);

        if (added == 0) return;

        var window = addon->WindowNode;
        if (window != null && window->AtkResNode.Height == grownHeight)
            SetWindowHeight(addon, (ushort)Math.Max(0, grownHeight - added));

        var insert = addon->GetNodeById(InsertNodeId);
        if (insert != null && insert->Y == movedInsertY)
            insert->SetYFloat(movedInsertY - added);

        added = 0;
    }

    private static void SetWindowHeight(AtkUnitBase* addon, ushort height)
    {
        var window = addon->WindowNode;
        window->SetHeight(height);
        window->AtkResNode.SetHeight(height);

        var root = window->Component->UldManager.RootNode;
        if (root != null)
        {
            root->SetHeight(height);

            if (root->PrevSiblingNode != null)
                root->PrevSiblingNode->SetHeight(height);
        }

        if (addon->RootNode != null)
            addon->RootNode->SetHeight(height);
    }

    /// <summary>
    /// 在節點清單裡找自己的節點。不能用 SearchNodeById：它查不到後來加進去的節點，
    /// 會變成每次都新建一個，舊的留在畫面上。
    /// </summary>
    private static AtkTextNode* FindNode(AtkUnitBase* addon)
    {
        var node = FindListedNode(addon, NodeId);
        return node != null && node->Type == NodeType.Text ? (AtkTextNode*)node : null;
    }

    /// <summary>PriceInsight 的節點也是後來加的，一樣要從節點清單找。</summary>
    private static AtkResNode* FindListedNode(AtkUnitBase* addon, uint id)
    {
        ref var uld = ref addon->UldManager;

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node != null && node->NodeId == id)
                return node;
        }

        return null;
    }

    /// <summary>建一個文字節點，接在 insert 前面（同一層），樣式抄資訊框裡的一般文字。</summary>
    private static AtkTextNode* CreateNode(AtkUnitBase* addon, AtkResNode* insert)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        if (node == null) return null;

        node->AtkResNode.Type = NodeType.Text;
        node->AtkResNode.NodeId = NodeId;
        node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop;
        node->AtkResNode.X = 16;
        node->AtkResNode.Width = 50;
        node->LineSpacing = 18;
        node->FontSize = 12;

        var style = addon->GetNodeById(StyleNodeId);
        if (style != null && style->Type == NodeType.Text)
        {
            var text = (AtkTextNode*)style;
            node->AtkResNode.Color = text->AtkResNode.Color;
            node->TextColor = text->TextColor;
            node->EdgeColor = text->EdgeColor;
            node->TextFlags = text->TextFlags;
        }
        else
        {
            node->AtkResNode.Color = new ByteColor { R = 255, G = 255, B = 255, A = 255 };
            node->TextColor = new ByteColor { R = 255, G = 255, B = 255, A = 255 };
            node->EdgeColor = new ByteColor { R = 0, G = 0, B = 0, A = 255 };
        }

        node->TextFlags |= TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

        var prev = insert->PrevSiblingNode;
        node->AtkResNode.ParentNode = insert->ParentNode;
        node->AtkResNode.PrevSiblingNode = prev;
        node->AtkResNode.NextSiblingNode = insert;
        insert->PrevSiblingNode = (AtkResNode*)node;

        if (prev != null)
            prev->NextSiblingNode = (AtkResNode*)node;

        addon->UldManager.UpdateDrawNodeList();
        return node;
    }

    /// <summary>插件停用或卸載時把節點拿掉，資訊框回到原本的樣子。</summary>
    private void RemoveNode()
    {
        var addon = (AtkUnitBase*)Services.GameGui.GetAddonByName(Addon).Address;
        if (addon == null) return;

        Restore(addon);
        RemoveAllNodes(addon);
    }

    /// <summary>拿掉所有自己的節點，包括舊版留下、或插件上次沒清掉的。</summary>
    private static void RemoveAllNodes(AtkUnitBase* addon)
    {
        // 上限只是保險，不會真的有這麼多
        for (var i = 0; i < 1000; i++)
        {
            var node = FindNode(addon);
            if (node == null) return;

            Unlink(addon, node);
        }
    }

    private static void Unlink(AtkUnitBase* addon, AtkTextNode* node)
    {
        var res = &node->AtkResNode;
        if (res->PrevSiblingNode != null)
            res->PrevSiblingNode->NextSiblingNode = res->NextSiblingNode;

        if (res->NextSiblingNode != null)
            res->NextSiblingNode->PrevSiblingNode = res->PrevSiblingNode;

        if (res->ParentNode != null && res->ParentNode->ChildNode == res)
            res->ParentNode->ChildNode = res->PrevSiblingNode;

        addon->UldManager.UpdateDrawNodeList();
        res->Destroy(true);
    }

    /// <summary>
    /// 「持有：背包 3・康楚薇 17（3天前）」，太長自己換行（文字節點不會自動折行）。
    /// 沒有持有又不顯示時回傳 null。
    /// </summary>
    private ReadOnlySeString? Describe(List<OwnershipEntry> entries)
    {
        if (entries.Count == 0 && !Config.ShowWhenNone) return null;

        var builder = new SeStringBuilder();
        builder.PushColorRgba(0xD4, 0xB9, 0x6A, 0xFF).Append("持有：").PopColor();

        if (entries.Count == 0)
        {
            PushStale(builder).Append("沒有").PopColor();
            return builder.ToReadOnlySeString();
        }

        var now = DateTime.UtcNow;
        var width = Width("持有：");

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var text = Format(entry, now);

            if (i > 0)
            {
                if (width + Width("・" + text) > LineWidth)
                {
                    builder.AppendNewLine();
                    width = 0;
                }
                else
                {
                    builder.Append("・");
                    width += Width("・");
                }
            }

            var stale = entry.Seen is { } seen && (now - seen).TotalDays >= Config.StaleDays;
            if (stale) PushStale(builder);
            builder.Append(text);
            if (stale) builder.PopColor();

            width += Width(text);
        }

        return builder.ToReadOnlySeString();
    }

    private string Format(OwnershipEntry entry, DateTime now)
    {
        var text = entry.Label + " " + entry.Total;

        if (entry.Hq > 0)
            text += entry.Normal > 0 ? "（HQ " + entry.Hq + "）" : "（HQ）";

        if (Config.ShowAge && entry.Seen is { } time)
            text += "（" + Age(now - time) + "）";

        return text;
    }

    private static float Width(string text) => text.Sum(c => c < 0x2E80 ? 0.5f : 1f);

    public static string Age(TimeSpan age) =>
        age.TotalMinutes < 1 ? "剛剛" :
        age.TotalHours < 1 ? $"{(int)age.TotalMinutes}分鐘前" :
        age.TotalDays < 1 ? $"{(int)age.TotalHours}小時前" :
        $"{(int)age.TotalDays}天前";

    private static SeStringBuilder PushStale(SeStringBuilder builder) => builder.PushColorRgba(0x8C, 0x8C, 0x8C, 0xFF);

    public void Dispose() => Stop();

    /// <summary>
    /// 字串陣列裡我們改寫的一格。同一次顯示資訊框可能更新好幾次，改過的字串會留在陣列裡；
    /// 再看到自己寫的字串時，要從原本的內容重新改，不然會一直往後疊。
    /// </summary>
    private sealed class Field
    {
        private readonly int index;
        private byte[]? original;
        private byte[]? written;

        public Field(int index)
        {
            this.index = index;
        }

        public void Forget() => original = written = null;

        /// <summary>change = null 表示這次不改；之前改過的話換回原本的內容。</summary>
        public void Apply(StringArrayData* strings, Func<byte[], byte[]>? change)
        {
            var current = Read(strings);
            var source = written != null && current.AsSpan().SequenceEqual(written) ? original! : current;

            if (change == null)
            {
                if (!ReferenceEquals(source, current))
                    Write(strings, source);

                Forget();
                return;
            }

            var result = change(source);
            Write(strings, result);

            original = source;
            written = result;
        }

        private byte[] Read(StringArrayData* strings)
        {
            var value = strings->StringArray[index];
            if (value.Value == null) return Array.Empty<byte>();

            return value.AsSpan().ToArray();
        }

        private void Write(StringArrayData* strings, byte[] value)
        {
            // 結尾補 0：遊戲讀的是 C 字串
            var terminated = new byte[value.Length + 1];
            value.CopyTo(terminated, 0);
            strings->SetValue(index, terminated, false, true, true);
        }
    }
}

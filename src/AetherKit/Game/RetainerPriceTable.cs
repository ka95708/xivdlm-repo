using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using AetherKit.Core;

namespace AetherKit.Game;

/// <summary>
/// 出售清單旁邊的價格表（類似 DR 的出售清單介面）：每一件一行，
/// 我的價格（可以直接改）、商店價（向 NPC 買的價格）、市場最低價、市場參考價（濾掉離群值、扣掉壓價金額）。
/// 商店價、最低價、參考價點一下就把我的價格改成那個數字；市場價只顯示查過、還沒過期的快取。
/// 改價直接呼叫遊戲的改價函式（同 DR），一律在遊戲執行緒做。
/// </summary>
public sealed unsafe class RetainerPriceTable
{
    private readonly Plugin plugin;
    private readonly RetainerWork work;

    /// <summary>正在編輯的價格（格子編號 → 輸入中的數字）。</summary>
    private readonly Dictionary<int, int> editing = new();

    public RetainerPriceTable(Plugin plugin, RetainerWork work)
    {
        this.plugin = plugin;
        this.work = work;
    }

    private RetainerWorkConfig Config => plugin.Config.RetainerWork;

    public void Draw()
    {
        if (!Config.ShowPriceTable) return;

        var list = Addons.Get("RetainerSellList");
        if (list == null || list->RootNode == null)
        {
            editing.Clear();
            return;
        }

        // 直接蓋在出售清單上（跟著它移動），預設高度放得下 20 行（一個雇員最多 20 件）
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(list->X, list->Y), ImGuiCond.Always);
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        ImGui.SetNextWindowSize(new Vector2(600, rowHeight * 23 + ImGui.GetTextLineHeightWithSpacing() * 2), ImGuiCond.FirstUseEver);

        var open = true;
        if (ImGui.Begin("出售價格###aetherkit-retainer-prices", ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
            DrawContent();

        ImGui.End();

        if (!open)
        {
            Config.ShowPriceTable = false;
            plugin.Save();
        }
    }

    private void DrawContent()
    {
        var busy = work.Running;
        if (busy) ImGui.BeginDisabled();

        if (ImGui.Button("全部改價")) work.AdjustAll();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("每一件都改成參考價（沒查過或超過 10 分鐘的順便查市場）");

        ImGui.SameLine();
        if (ImGui.Button("全部收回")) work.RetrieveAll();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("全部收回給自己（背包滿了就停）");

        ImGui.SameLine();
        if (ImGui.Button("清除快取")) work.ClearCache();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("查過的市場價全部丟掉，之後改價會重查");

        if (busy) ImGui.EndDisabled();

        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(work.Status);
            ImGui.SameLine();
            if (ImGui.SmallButton("停止")) work.Stop("手動停止");
        }

        if (work.QueuedCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"新上架排隊查價中：{work.QueuedCount} 件");
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##prices", 6, flags)) return;

        ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("數量", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("我的價格", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("參考價", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("最低價", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableHeadersRow();

        foreach (var row in RetainerWork.MarketRows())
        {
            ImGui.TableNextRow();
            ImGui.PushID(row.Slot);

            var quote = work.CachedQuote(row.Item, row.Hq);

            ImGui.TableNextColumn();
            DrawItem(row.Item, row.Hq);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Quantity.ToString());
            if (ImGui.IsItemHovered()) QuantityTooltip(row);

            ImGui.TableNextColumn();
            DrawPriceInput(row);

            ImGui.TableNextColumn();
            if (quote is { } r)
                PriceCell("reference", r.Reference == 0 ? 0 : work.Undercut(r.Reference), row, r, "參考價",
                    r.Reference == r.Lowest
                        ? "別人的最低價，再扣掉壓價金額。"
                        : $"濾掉過低的離群值後是 {r.Reference:N0}，再扣掉壓價金額。");
            else ImGui.TextDisabled("—");

            ImGui.TableNextColumn();
            if (quote is { } q) PriceCell("lowest", q.Lowest, row, q, "最低價", "別人上架的最低單價（不扣壓價）。");
            else ImGui.TextDisabled("—");

            ImGui.TableNextColumn();
            if (busy) ImGui.BeginDisabled();
            if (ImGui.SmallButton("改")) work.AdjustOne(row.Slot, row.Item, row.Hq);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("改成參考價（沒查過或超過 10 分鐘的順便查市場）");
            ImGui.SameLine();
            if (ImGui.SmallButton("收")) work.RetrieveOne(row.Slot, row.Item, row.Hq);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("收回給自己");
            if (busy) ImGui.EndDisabled();

            ImGui.PopID();
        }

        ImGui.EndTable();
        ImGui.TextDisabled("參考價、最低價點一下就把我的價格改成那個數字，滑鼠移上去看別人上架的價格；我的價格可以直接輸入。");
    }

    private static readonly Vector4 HqColor = new(1f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 SameColor = new(0.5f, 0.9f, 0.5f, 1f);

    /// <summary>物品：圖示（HQ 用遊戲的 HQ 圖示）＋ 名稱，HQ 名稱後面加遊戲的 HQ 符號、用金色。</summary>
    private static void DrawItem(uint item, bool hq)
    {
        var size = ImGui.GetFrameHeight();
        var icon = 0u;
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item);
        if (row is { } data) icon = data.Icon;

        if (icon != 0 && Services.Textures.TryGetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(icon, hq), out var shared)
                      && shared.GetWrapOrDefault() is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(size));
        }
        else
        {
            ImGui.Dummy(new Vector2(size));
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        var name = RetainerWork.ItemName(item);
        if (hq) ImGui.TextColored(HqColor, $"{name} {(char)Dalamud.Game.Text.SeIconChar.HighQuality}");
        else ImGui.TextUnformatted(name);
    }

    /// <summary>
    /// 我的價格：平常顯示現價，打完離開輸入框（或按 Enter）就改。
    /// 低於 NPC 收購價時紅字，滑鼠移上去看收購價、NPC 販售價。
    /// </summary>
    private void DrawPriceInput((int Slot, uint Item, bool Hq, uint Price, int Quantity) row)
    {
        var value = editing.TryGetValue(row.Slot, out var typed) ? typed : (int)row.Price;

        var vendor = RetainerWork.ShopPrice(row.Item, row.Hq);
        var npcSale = RetainerWork.NpcSalePrice(row.Item);
        var low = vendor > 0 && row.Price < vendor;

        ImGui.SetNextItemWidth(-1);
        if (low) ImGui.PushStyleColor(ImGuiCol.Text, WarnColor);
        ImGui.InputInt("##price", ref value, 0, 0);
        if (low) ImGui.PopStyleColor();

        if (ImGui.IsItemActive()) editing[row.Slot] = value;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            editing.Remove(row.Slot);
            if (value > 0 && value != row.Price) Apply(row.Slot, row.Item, row.Hq, row.Price, (uint)value, "手動輸入");
        }
        else if (!ImGui.IsItemActive())
        {
            editing.Remove(row.Slot);
        }

        if (ImGui.IsItemHovered()) PriceTooltip(row, vendor, npcSale, low);
    }

    /// <summary>
    /// 可以點的價格：點了就把我的價格改成這個數字。0 = 沒有（顯示「—」）。
    /// 滑鼠移上去：說明、查的時間、別人上架的前 10 筆。
    /// </summary>
    private void PriceCell(string id, uint price, (int Slot, uint Item, bool Hq, uint Price, int Quantity) row,
                           RetainerWork.Quote quote, string title, string what)
    {
        if (price == 0)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered()) MarketTooltip(title, quote, "市場上沒有別人在賣。", 0, row);
            return;
        }

        var same = price == row.Price;
        if (same) ImGui.PushStyleColor(ImGuiCol.Text, SameColor);

        if (ImGui.Selectable($"{price:N0}##{id}", false) && !same && !work.Running)
            Apply(row.Slot, row.Item, row.Hq, row.Price, price, title);

        if (same) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) MarketTooltip(title, quote, what, price, row);
    }

    private void Apply(int slot, uint item, bool hq, uint before, uint price, string reason) =>
        Services.Framework.RunOnFrameworkThread(() => work.ApplyPrice(slot, item, hq, before, price, reason));

    // ───────────────────────── 滑鼠提示 ─────────────────────────

    private static readonly Vector4 MutedColor = new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>數量：單價 × 數量 = 總價。</summary>
    private static void QuantityTooltip((int Slot, uint Item, bool Hq, uint Price, int Quantity) row)
    {
        if (!BeginTip("總價")) return;

        var total = (ulong)row.Price * (ulong)row.Quantity;
        KeyValues(("單價", $"{row.Price:N0}", null), ("數量", $"× {row.Quantity}", null), ("總價", $"{total:N0}", HqColor));
        if (RetainerWork.IsPlaceholder(row.Price, row.Quantity))
            Hint("還是上架時填的最高價，等查價改價。", MutedColor);

        EndTip();
    }

    /// <summary>我的價格：NPC 收購價、販售價；比收購價低、比販售價高都提醒。</summary>
    private static void PriceTooltip((int Slot, uint Item, bool Hq, uint Price, int Quantity) row, uint vendor, uint npcSale, bool low)
    {
        if (!BeginTip("我的價格")) return;

        var rows = new List<(string, string, Vector4?)> { ("現在", $"{row.Price:N0}", low ? WarnColor : null) };
        if (vendor > 0) rows.Add(("NPC 收購價", $"{vendor:N0}", low ? WarnColor : null));
        if (npcSale > 0) rows.Add(("NPC 販售價", $"{npcSale:N0}", null));
        KeyValues(rows.ToArray());

        if (low) Hint("比 NPC 收購價還低，直接賣給商店比較划算。", WarnColor);
        if (npcSale > 0 && row.Price > npcSale) Hint("比 NPC 販售價還高，別人會直接跟 NPC 買。", WarnColor);
        Hint("直接輸入數字，離開輸入框就改。", MutedColor);

        EndTip();
    }

    /// <summary>參考價／最低價：說明、查的時間、別人上架的清單（比參考價低的是被濾掉的離群值）、點了會怎樣。</summary>
    private static void MarketTooltip(string title, RetainerWork.Quote quote, string what, uint price,
                                      (int Slot, uint Item, bool Hq, uint Price, int Quantity) row)
    {
        if (!BeginTip(title, AgeText(quote))) return;

        Hint(what, MutedColor);

        if (quote.Listings.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.BeginTable("##listings", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("單價", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("999,999,999").X);
                ImGui.TableSetupColumn("數量", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("× 9999").X);
                ImGui.TableSetupColumn("");
                ImGui.TableHeadersRow();

                var referenceMarked = false;
                foreach (var (listing, quantity) in quote.Listings)
                {
                    ImGui.TableNextRow();
                    var outlier = listing < quote.Reference;
                    var isReference = !referenceMarked && listing == quote.Reference;
                    if (isReference) referenceMarked = true;
                    Vector4? color = outlier ? MutedColor : isReference ? SameColor : null;

                    ImGui.TableNextColumn();
                    RightAligned($"{listing:N0}", color);
                    ImGui.TableNextColumn();
                    RightAligned($"× {quantity}", color);
                    ImGui.TableNextColumn();
                    if (outlier) ImGui.TextColored(MutedColor, "太低，不跟");
                    else if (isReference) ImGui.TextColored(SameColor, "← 參考");
                }

                ImGui.EndTable();
            }
        }

        if (price > 0)
        {
            ImGui.Spacing();
            if (price == row.Price) Hint("跟我現在的價格一樣。", SameColor);
            else Hint($"點一下：我的價格改成 {price:N0}", null);
        }

        EndTip();
    }

    private static bool BeginTip(string title, string? subtitle = null)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * ImGuiHelpers.GlobalScale);
        ImGui.BeginTooltip();

        ImGui.TextColored(HqColor, title);
        if (subtitle != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(MutedColor, subtitle);
        }

        ImGui.Separator();
        return true;
    }

    private static void EndTip()
    {
        ImGui.EndTooltip();
        ImGui.PopStyleVar();
    }

    /// <summary>左邊名稱（灰）、右邊數字（靠右對齊）。</summary>
    private static void KeyValues(params (string Label, string Value, Vector4? Color)[] rows)
    {
        if (!ImGui.BeginTable("##kv", 2, ImGuiTableFlags.SizingFixedFit)) return;

        ImGui.TableSetupColumn("label");
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("99,999,999,999").X);
        foreach (var (label, value, color) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MutedColor, label);
            ImGui.TableNextColumn();
            RightAligned(value, color);
        }

        ImGui.EndTable();
    }

    private static void RightAligned(string text, Vector4? color)
    {
        var offset = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
    }

    private static void Hint(string text, Vector4? color)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetFontSize() * 18);
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static string AgeText(RetainerWork.Quote quote)
    {
        var minutes = (int)(DateTime.Now - quote.At).TotalMinutes;
        return minutes < 1 ? "剛剛查的" : $"{minutes} 分鐘前查的";
    }
}

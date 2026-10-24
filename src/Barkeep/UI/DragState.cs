using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 拖放落點的提示。ImGui 預設的黃框畫在項目外面一圈，會超出分區或清單的邊界；
/// 改成沿著項目內緣自己畫框，畫在視窗自己的 draw list 上，自然被裁切。
/// </summary>
internal static class DropHighlight
{
    public static ImGuiPayloadPtr Accept(string payload)
    {
        var result = ImGui.AcceptDragDropPayload(payload,
            ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);

        if (!result.IsNull)
        {
            var theme = Plugin.Instance.Config.Theme;
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                Paint.Color(theme.Accent), theme.Rounding, ImDrawFlags.None, 2f);
        }

        return result;
    }
}

/// <summary>手動分區裡的項目拖曳排序，也可以跨分區。</summary>
public static class ItemDrag
{
    private const string Payload = "BARKEEP_ITEM";

    private static List<PanelItem>? sourceList;
    private static int sourceIndex = -1;

    public static void Source(List<PanelItem> list, int index, string label)
    {
        if (!ImGui.BeginDragDropSource()) return;

        sourceList = list;
        sourceIndex = index;

        ImGui.SetDragDropPayload(Payload, ReadOnlySpan<byte>.Empty);
        ImGui.TextUnformatted(label);
        ImGui.EndDragDropSource();
    }

    public static bool Target(List<PanelItem> list, int index)
    {
        if (!ImGui.BeginDragDropTarget()) return false;

        var moved = false;
        var payload = DropHighlight.Accept(Payload);

        if (!payload.IsNull && payload.IsDelivery() && sourceList != null && sourceIndex >= 0 && sourceIndex < sourceList.Count)
        {
            var sameList = ReferenceEquals(sourceList, list);
            var from = sourceIndex;
            var item = sourceList[from];

            sourceList.RemoveAt(from);

            var target = sameList && from < index ? index - 1 : index;
            list.Insert(Math.Clamp(target, 0, list.Count), item);

            sourceList = null;
            sourceIndex = -1;
            moved = true;
        }

        ImGui.EndDragDropTarget();
        return moved;
    }
}

/// <summary>
/// 裝備組分區裡拖曳職能小組調整順序。拖的是小組標題，
/// 放下時改寫該分區的 RoleColumns。
/// </summary>
public static class RoleDrag
{
    private const string Payload = "BARKEEP_ROLE";

    private static PanelSection? sourceSection;
    private static JobRole sourceRole;

    /// <summary>
    /// 拖曳中每一幀都會呼叫 Source，所以「上一幀還有呼叫」就代表還在拖。
    /// 放下或取消後自然就停了，不用另外清狀態。
    /// </summary>
    private static int sourceFrame = -10;

    public static void Source(PanelSection section, JobRole role, string label)
    {
        if (!ImGui.BeginDragDropSource()) return;

        sourceSection = section;
        sourceRole = role;
        sourceFrame = Plugin.Instance.FrameCount;

        ImGui.SetDragDropPayload(Payload, ReadOnlySpan<byte>.Empty);
        ImGui.TextUnformatted(label);
        ImGui.EndDragDropSource();
    }

    /// <summary>這個分區裡正有小組被拖著。拖曳時才畫出欄底與新欄的落點。</summary>
    public static bool IsDragging(PanelSection section) =>
        ReferenceEquals(sourceSection, section) && Plugin.Instance.FrameCount - sourceFrame <= 1;

    /// <summary>放到第 column 欄的第 index 個位置。只接受同一個分區裡的小組。</summary>
    public static bool Target(PanelSection section, int column, int index)
    {
        if (!ImGui.BeginDragDropTarget()) return false;

        var moved = false;
        var payload = DropHighlight.Accept(Payload);

        if (!payload.IsNull && payload.IsDelivery() && ReferenceEquals(sourceSection, section))
        {
            section.MoveRole(sourceRole, column, index);
            sourceSection = null;
            moved = true;
        }

        ImGui.EndDragDropTarget();
        return moved;
    }
}

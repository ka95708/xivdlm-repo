using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using GatherBuddy.Config;
using GatherBuddy.Interfaces;
using GatherBuddy.Time;
using OtterGui;
using OtterGui.Table;
using OtterGui.Text;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    internal static bool DrawLocationInput(IGatherable item, ILocation? current, out ILocation? ret)
    {
        const string noPreferred = "無偏好地點";
        var          width       = SetInputWidth * 0.85f;
        ret = current;
        if (item.Locations.Count() == 1)
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, 0.5f));
            ImUtf8.TextFramed(item.Locations.First().Name, ImGui.GetColorU32(ImGuiCol.FrameBg), new Vector2(width, 0));
            DrawLocationTooltip(item.Locations.First());
            return false;
        }

        var text = current?.Name ?? noPreferred;
        ImGui.SetNextItemWidth(width);
        using var combo = ImUtf8.Combo("##Location"u8, text);
        DrawLocationTooltip(current);
        if (!combo)
            return false;

        var changed = false;

        if (ImGui.Selectable(noPreferred, current == null))
        {
            ret     = null;
            changed = true;
        }

        var idx = 0;
        foreach (var loc in item.Locations)
        {
            using var id = ImUtf8.PushId(idx++);
            if (ImUtf8.Selectable(loc.Name, loc.Id == (current?.Id ?? 0)))
            {
                ret     = loc;
                changed = true;
            }

            DrawLocationTooltip(loc);
        }

        return changed;
    }

    internal static void DrawTimeInterval(TimeInterval uptime, bool uptimeDependency = false, bool rightAligned = true)
    {
        var active = uptime.ToTimeString(GatherBuddy.Time.ServerTime, false, out var timeString);
        var colorId = (active, uptimeDependency) switch
        {
            (true, true)   => ColorId.DependentAvailableFish.Value(),
            (true, false)  => ColorId.AvailableItem.Value(),
            (false, true)  => ColorId.DependentUpcomingFish.Value(),
            (false, false) => ColorId.UpcomingItem.Value(),
        };
        using var color = ImRaii.PushColor(ImGuiCol.Text, colorId);
        if (rightAligned)
            ImUtf8.TextRightAligned(timeString);
        else
            ImUtf8.Text(timeString);
        color.Pop();
        if ((uptimeDependency || !char.IsLetter(timeString[0])) && ImGui.IsItemHovered())
        {
            using var tt = ImRaii.Tooltip();

            if (uptimeDependency)
                ImUtf8.TextFramed("出現時段相依條件"u8, 0xFF202080);

            if (!char.IsLetter(timeString[0]))
                ImUtf8.Text($"{uptime.Start}\n{uptime.End}\n{uptime.DurationString()}");
        }
    }

    internal static void HoverTooltip(string text)
    {
        if (!text.StartsWith('\0'))
            ImUtf8.HoverTooltip(text);
    }

    public static void AlignTextToSize(string text, Vector2 size)
    {
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(cursor + new Vector2(ImGui.GetStyle().ItemSpacing.X / 2, (size.Y - ImGui.GetTextLineHeight()) / 2));
        ImUtf8.Text(text);
        ImGui.SameLine();
        ImGui.SetCursorPosY(cursor.Y);
        ImGui.NewLine();
    }


    private static void DrawFormatInput(string label, string tooltip, string oldValue, string defaultValue, Action<string> setValue)
    {
        var       tmp = oldValue;
        using var id  = ImRaii.PushId(label);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 50 * Scale);
        if (ImGui.InputText(string.Empty, ref tmp, 256) && tmp != oldValue)
        {
            setValue(tmp);
            GatherBuddy.Config.Save();
        }

        ImGuiUtil.HoverTooltip(tooltip);

        if (ImGuiUtil.DrawDisabledButton("預設", Vector2.Zero, defaultValue, defaultValue == oldValue))
        {
            setValue(defaultValue);
            GatherBuddy.Config.Save();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
    }

    private static void DrawStatusLine<T>(Table<T> table, string name)
    {
        if (!GatherBuddy.Config.ShowStatusLine)
            return;

        ImGui.SameLine();
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.NewLine();
        ImUtf8.Text($"{table.CurrentItems} / {table.TotalItems} {name} 可見");
        if (table.TotalColumns != table.VisibleColumns)
        {
            ImGui.SameLine(0, 50 * ImGuiHelpers.GlobalScale);
            ImUtf8.Text($"{table.TotalColumns - table.VisibleColumns} 列被隱藏");
        }

        if (typeof(T) == typeof(ExtendedFish))
        {
            if (File.Exists(GatherBuddy.GameData.OverrideFile))
            {
                ImGui.SameLine(0, 50 * ImGuiHelpers.GlobalScale);
                if (ImUtf8.SmallButton("重新匯入魚類覆寫資料") && GatherBuddy.GameData.ReimportOverrides())
                {
                    GatherBuddy.UptimeManager.ResetModifiedUptimes();
                    foreach (var fish in ExtendedFishList.Where(f => f.Data.HasOverridenData))
                        fish.Update();
                }
            }
            
            using (var popup = ImUtf8.PopupContextItem("##Context"u8))
            {
                if (popup)
                    if (ImUtf8.MenuItem("移至備份"u8))
                        try
                        {
                            File.Move(GatherBuddy.GameData.OverrideFile, Path.ChangeExtension(GatherBuddy.GameData.OverrideFile, ".json.bak"),
                                true);
                        }
                        catch (Exception ex)
                        {
                            GatherBuddy.Log.Error($"Could not move fish data override file to backup:\n{ex}");
                        }
            }
            
            if (GatherBuddy.GameData.OverriddenFish > 0)
            {
                ImGui.SameLine(0, 50 * ImGuiHelpers.GlobalScale);
                ImUtf8.Text($"已覆寫 {GatherBuddy.GameData.OverriddenFish} 種魚類");
            }
        }
    }

    private static void DrawClippy()
    {
        const string popupName = "GatherClippy###ClippyPopup";
        const string text      = "找不到想要的物品?";
        if (GatherBuddy.Config.HideClippy)
            return;

        var textSize   = ImGui.CalcTextSize(text).X;
        var buttonSize = new Vector2(Math.Max(200, textSize) * ImGuiHelpers.GlobalScale, ImGui.GetFrameHeight());
        var padding    = ImGuiHelpers.ScaledVector2(9, 9);

        ImGui.SetCursorPos(ImGui.GetWindowSize() - buttonSize - padding);
        using var child = ImRaii.Child("##clippyChild", buttonSize, false, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDecoration);
        if (!child)
            return;

        using var color = ImRaii.PushColor(ImGuiCol.Button, 0xFFA06020);

        if (ImGui.Button(text, buttonSize))
            ImGui.OpenPopup(popupName);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift)
        {
            GatherBuddy.Config.HideClippy = true;
            GatherBuddy.Config.Save();
        }

        ImGuiUtil.HoverTooltip("單擊以開啟本表的使用幫助.\n"
          + "按住 Ctrl + Shift 鍵右鍵本按鈕以永久隱藏");

        color.Pop();
        var windowSize = new Vector2(1024 * ImGuiHelpers.GlobalScale,
            ImGui.GetTextLineHeightWithSpacing() * 13 + 2 * ImGui.GetFrameHeightWithSpacing());
        ImGui.SetNextWindowSize(windowSize);
        ImGui.SetNextWindowPos((ImGui.GetIO().DisplaySize - windowSize) / 2);
        using var popup = ImRaii.Popup(popupName,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.Modal);
        if (!popup)
            return;

        ImGui.BulletText(
            "可以使用諸如 \"物品名稱...\" 之類的文字過濾器，只顯示包含指定字串的條目。它們對大小寫不敏感，並且不會儲存");
        ImGui.BulletText(
            "文字過濾器還支援正規表示式，例如 \"(blue|green)\" 會匹配所有包含藍色或綠色的條目。");
        ImGui.BulletText("類似 \"下次時間\"、\"採集點型別\" 或 \"魚類型別\" 的按鈕過濾器可以透過點選來過濾特定型別。");
        ImGui.BulletText("這些過濾器會儲存。但對於具有活動過濾器的列，過濾按鈕會變為紅色");
        ImGui.NewLine();
        ImGui.BulletText(
            "可以點選標題空白處來按該列對錶格進行升序或降序排序，這會透過一個向上或向下的小三角形來表示");
        ImGui.BulletText(
            "可以右鍵點選標題空白處來開啟表格上下文選單，在其中可以隱藏不感興趣的列");
        ImGui.BulletText(
            "可以透過拖動列的分隔線來調整文字列的大小。調整時會以藍色高亮顯示分隔線。大小會在會話之間儲存");
        ImGui.BulletText(
            "可以透過在空白處左鍵點選並拖動來重新排列大多數列。排列順序會在會話之間儲存");
        ImGui.NewLine();
        ImGui.BulletText(
            "可以右鍵點選物品名稱和其他一些列（如魚餌和釣魚地點）來開啟帶有物件特定選項的右鍵選單");
        ImGui.BulletText("你也可以重新排列標籤頁，但無法儲存");

        ImGui.SetCursorPosY(windowSize.Y - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y);
        if (ImGui.Button("明白了", -Vector2.UnitX))
            ImGui.CloseCurrentPopup();
    }
}

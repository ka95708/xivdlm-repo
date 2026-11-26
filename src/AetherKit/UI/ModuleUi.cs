using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using AetherKit.Core;

namespace AetherKit.UI;

/// <summary>
/// 模組頁共用的版面零件。每個模組頁都用這些組起來，長相和用詞才會一致（規則見 開發指南.md「介面」）。
///
/// 頁首（DrawHeader）的順序：
///   1. 控制列：主要按鈕（開始／停止、開啟視窗……）＋「指令：/aetherkit xxx」
///   2. 狀態：「狀態：…」，下面每行一個「標題：內容」
///   3. 需要的東西：執行時借用的核心能力［核心能力設定］、相依插件
/// 分頁：第一個一律叫「設定」，裡面用 Section 分段。
/// </summary>
public static class ModuleUi
{
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.8f, 0.3f, 1f);
    private static readonly Vector4 Green = new(0.5f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    /// <summary>控制列的主要按鈕。回傳 true = 按下。</summary>
    public static bool Action(string label) => ImGui.Button(label);

    /// <summary>控制列：接在按鈕後面的指令提示（同一行）。</summary>
    public static void Command(string command)
    {
        ImGui.SameLine();
        ImGui.TextDisabled($"指令：{command}");
    }

    /// <summary>狀態區第一行。</summary>
    public static void Status(string status) => ImGui.TextUnformatted($"狀態：{status}");

    /// <summary>狀態區的一行「標題：內容」（進度、已執行、目前目標……）。</summary>
    public static void Detail(string label, string value) => ImGui.TextUnformatted($"{label}：{value}");

    /// <summary>需要的東西：執行時借用的核心能力，旁邊是跳到核心能力頁的按鈕。</summary>
    public static void Borrows(string capabilities)
    {
        ImGui.Spacing();
        ImGui.TextDisabled($"執行時借用核心能力：{capabilities}");
        ImGui.SameLine();
        if (ImGui.SmallButton("核心能力設定"))
            Plugin.Instance.OpenCorePage();
    }

    /// <summary>相依插件的一項。</summary>
    public readonly record struct Dependency(string Name, string Purpose, DependencyLevel Level, bool Loaded);

    public enum DependencyLevel
    {
        /// <summary>沒開不能開始。</summary>
        Required,

        /// <summary>沒開也能跑，但建議開。</summary>
        Recommended,

        /// <summary>開著會互相干擾，建議關掉。</summary>
        Conflict
    }

    /// <summary>需要的東西：相依插件，必要的沒開紅字、建議的沒開黃字、衝突的開著黃字。</summary>
    public static void Dependencies(IEnumerable<Dependency> dependencies)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("相依插件");

        foreach (var dep in dependencies)
        {
            var (mark, color, note) = dep.Level switch
            {
                DependencyLevel.Required => dep.Loaded ? ("已開", Green, "") : ("未開", Red, "（必要，沒開不能開始）"),
                DependencyLevel.Recommended => dep.Loaded ? ("已開", Green, "") : ("未開", Yellow, "（建議開啟）"),
                _ => dep.Loaded ? ("開著", Yellow, "（建議關掉）") : ("沒開", Grey, ""),
            };

            ImGui.TextColored(color, $"[{mark}] {dep.Name}{note}");
            ImGui.SameLine();
            ImGui.TextDisabled(dep.Purpose);
        }
    }

    /// <summary>設定分頁裡的一段標題。</summary>
    public static void Section(string title) => Paint.SettingsHeading(title);

    /// <summary>設定下面的補充說明（灰字）。說明寫「會怎樣」，不寫實作細節。</summary>
    public static void Note(string text) => ImGui.TextDisabled(text);

    /// <summary>自動流程「完成後」：不做事、登出、關閉遊戲。回傳 true 代表改了。</summary>
    public static bool FinishActionCombo(string label, ref FinishAction action)
    {
        var changed = false;
        ImGui.SetNextItemWidth(200);

        if (ImGui.BeginCombo(label, action.Name()))
        {
            foreach (var option in new[] { FinishAction.None, FinishAction.Logout, FinishAction.Shutdown })
            {
                if (!ImGui.Selectable(option.Name(), option == action)) continue;

                action = option;
                changed = true;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("（只用這一次，結束後改回不做事）");

        return changed;
    }

    /// <summary>頁首：這個功能排了登出或關閉遊戲，顯示倒數和「取消」。</summary>
    public static void FinishPending(CoreServices core, string owner)
    {
        var finish = core.Finish;
        if (finish.Pending == FinishAction.None || finish.Owner != owner) return;

        ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), finish.Executing
            ? $"{finish.Pending.Name()}中……"
            : $"完成：{finish.Pending.Countdown(finish.SecondsLeft)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("取消##finish"))
            finish.Cancel();
    }
}

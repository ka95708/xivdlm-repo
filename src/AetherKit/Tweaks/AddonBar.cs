using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 貼在遊戲視窗右上角外面的一排按鈕，所有小功能共用。
/// 小功能每幀登記「哪個遊戲視窗、按鈕叫什麼、按了做什麼」，排版、位置、收合都在這裡處理：
/// - 位置：遊戲視窗標題列正上方、靠右；上面放不下（視窗貼著螢幕頂端）就改貼在視窗下緣外面。
/// - 同一個視窗按鈕超過 3 個，收成一個「AetherKit」選單。
/// - 有選項的小功能，最右邊多一個「選項」，點開可以直接改。
/// 用 ImGui 畫：不在遊戲視窗裡插原生節點，遊戲改版比較不會壞。
/// </summary>
public sealed unsafe class AddonBar
{
    private const int MaxInline = 3;

    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

    private sealed record Entry(string Label, Action Click, bool Enabled, string? Tooltip);

    private sealed class Bar
    {
        public readonly List<Entry> Buttons = new();
        public readonly List<(string Title, Action Draw)> Options = new();
    }

    private readonly Dictionary<string, Bar> bars = new();

    /// <summary>在 addon 這個遊戲視窗上加一個按鈕（只有這一幀，每幀都要登記）。</summary>
    public void Button(string addon, string label, Action click, bool enabled = true, string? tooltip = null) =>
        BarOf(addon).Buttons.Add(new Entry(label, click, enabled, tooltip));

    /// <summary>在 addon 的「選項」選單裡加一段設定。</summary>
    public void Options(string addon, string title, Action draw) => BarOf(addon).Options.Add((title, draw));

    private Bar BarOf(string addon)
    {
        if (!bars.TryGetValue(addon, out var bar))
            bars[addon] = bar = new Bar();
        return bar;
    }

    internal void Clear()
    {
        foreach (var bar in bars.Values)
        {
            bar.Buttons.Clear();
            bar.Options.Clear();
        }
    }

    internal void Draw()
    {
        foreach (var (name, bar) in bars)
        {
            if (bar.Buttons.Count == 0 && bar.Options.Count == 0) continue;

            var addon = Addons.Get(name);
            if (addon == null || addon->RootNode == null) continue;

            var width = addon->RootNode->Width * addon->Scale;
            var height = addon->RootNode->Height * addon->Scale;
            var barHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().WindowPadding.Y * 2;

            // 預設貼在標題列上方（右下角對齊遊戲視窗右上角）；上面放不下就貼在下緣
            if (addon->Y - barHeight - 2 >= 0)
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(addon->X + width, addon->Y - 2), ImGuiCond.Always, new Vector2(1, 1));
            else
                ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(addon->X + width, addon->Y + height + 2), ImGuiCond.Always, new Vector2(1, 0));

            if (ImGui.Begin($"##aetherkit-bar-{name}", Flags))
                DrawContent(name, bar);

            ImGui.End();
        }
    }

    private static void DrawContent(string name, Bar bar)
    {
        if (bar.Buttons.Count <= MaxInline)
        {
            for (var i = 0; i < bar.Buttons.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                DrawButton(bar.Buttons[i]);
            }
        }
        else
        {
            if (ImGui.Button("AetherKit")) ImGui.OpenPopup($"menu-{name}");

            if (ImGui.BeginPopup($"menu-{name}"))
            {
                foreach (var entry in bar.Buttons)
                {
                    if (ImGui.MenuItem(entry.Label, string.Empty, false, entry.Enabled)) entry.Click();
                    if (entry.Tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(entry.Tooltip);
                }

                ImGui.EndPopup();
            }
        }

        if (bar.Options.Count == 0) return;

        if (bar.Buttons.Count > 0) ImGui.SameLine();
        if (ImGui.Button("選項")) ImGui.OpenPopup($"options-{name}");

        if (ImGui.BeginPopup($"options-{name}"))
        {
            for (var i = 0; i < bar.Options.Count; i++)
            {
                if (bar.Options.Count > 1)
                {
                    if (i > 0) ImGui.Separator();
                    ImGui.TextDisabled(bar.Options[i].Title);
                }

                ImGui.PushID(i);
                bar.Options[i].Draw();
                ImGui.PopID();
            }

            ImGui.EndPopup();
        }
    }

    private static void DrawButton(Entry entry)
    {
        if (!entry.Enabled) ImGui.BeginDisabled();
        if (ImGui.Button(entry.Label)) entry.Click();
        if (!entry.Enabled) ImGui.EndDisabled();

        if (entry.Tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(entry.Tooltip);
    }
}

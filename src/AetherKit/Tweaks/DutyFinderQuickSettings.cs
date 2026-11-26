using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;
using AetherKit.UI;

namespace AetherKit.Tweaks;

/// <summary>
/// 任務搜尋器快速設定（DR 的 OptimizedDutyFinderSetting）：任務搜尋器／團隊任務搜尋器上「目前設定」那排小圖示，
/// 換成可以直接點的開關（中途加入、解除限制、等級同步、最低裝等、無視增益、探索模式、限制練級隨機、分配方式），
/// 不用再開設定視窗。暗的 = 關，亮的 = 開；分配方式點一下換下一種。
///
/// 原本那排圖示會被藏起來，換成 AetherKit 畫的一排，關掉小功能就還原。
/// 改設定用核心共用的 DutyFinderSettings（遊戲自己的套用函式，跟按設定視窗的確定一樣會存檔）。
/// </summary>
public sealed unsafe class DutyFinderQuickSettings : Tweak
{
    private static readonly string[] Windows = { "ContentsFinder", "RaidFinder" };

    // 原本顯示目前設定的那排圖示
    private const uint SettingIconsNode = 6;

    private sealed record Entry(DutyFinderOption Option, uint Icon, uint Tooltip);

    private static readonly Entry[] Entries =
    {
        new(DutyFinderOption.JoinPartyInProgress, 60644, 2519),
        new(DutyFinderOption.UnrestrictedParty, 60641, 10008),
        new(DutyFinderOption.LevelSync, 60649, 12696),
        new(DutyFinderOption.MinimumIl, 60642, 10010),
        new(DutyFinderOption.SilenceEcho, 60647, 12691),
        new(DutyFinderOption.ExplorerMode, 60648, 13038),
        new(DutyFinderOption.LimitedLevelingRoulette, 60640, 13030),
        new(DutyFinderOption.LootRule, 0, 0)
    };

    public override string Id => "duty-finder-quick-settings";
    public override string Name => "任務搜尋器快速設定";
    public override string Description => "任務搜尋器上「目前設定」那排圖示變成可以直接點的開關（解除限制、等級同步、分配方式……），不用開設定視窗。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Button;
    public override string Where => "任務搜尋器、團隊任務搜尋器左下方原本顯示設定的那排圖示";

    protected override void OnDisable() =>
        Services.Framework.RunOnFrameworkThread(() => SetNativeIcons(true));

    /// <summary>遊戲執行緒：把原本那排圖示藏起來（視窗每次打開都是新的，所以每幀檢查）。</summary>
    public override void Update(DateTime now) => SetNativeIcons(false);

    // [改記憶體] 藏起／還原任務搜尋器原本的設定圖示（見 改版檢查清單.md）
    private static void SetNativeIcons(bool visible)
    {
        foreach (var name in Windows)
        {
            var addon = Addons.Get(name);
            if (addon == null) continue;

            var node = addon->GetNodeById(SettingIconsNode);
            if (node != null && node->IsVisible() != visible)
                node->ToggleVisibility(visible);
        }
    }

    public override void Draw()
    {
        foreach (var name in Windows)
        {
            var addon = Addons.Get(name);
            if (addon == null) continue;

            var node = addon->GetNodeById(SettingIconsNode);
            if (node == null) continue;

            DrawRow(name, addon, node);
        }
    }

    private void DrawRow(string name, AtkUnitBase* addon, AtkResNode* node)
    {
        var scale = addon->Scale;
        var icon = 24f * scale;
        var spacing = 2f * scale;

        // 貼齊原本那排圖示的左邊，上下置中
        var y = node->ScreenY + (node->Height * scale - icon) / 2f;
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(node->ScreenX, y), ImGuiCond.Always);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

        // 外框不留邊（圖示才對得準），但只套在這個視窗本身：Begin 完就還原，提示框才有正常的內距
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.Begin($"##aetherkit-duty-settings-{name}", flags);
        ImGui.PopStyleVar();

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, 0));

        if (visible)
        {
            var unrestricted = DutyFinderSettings.IsOn(DutyFinderOption.UnrestrictedParty);

            foreach (var entry in Entries)
            {
                var value = DutyFinderSettings.Get(entry.Option);
                var (iconId, tooltip) = entry.Option == DutyFinderOption.LootRule
                    ? (value == 2 ? 60646u : 60645u, 10022u + Math.Min(value, (byte)2))
                    : (entry.Icon, entry.Tooltip);

                // 分配方式是「通常」時也算沒開（暗）；等級同步只在解除限制時有意義，沒解除限制時更暗
                var alpha = value != 0 ? 1f
                    : entry.Option == DutyFinderOption.LevelSync && !unrestricted ? 0.25f : 0.45f;

                if (IconToggle($"{name}-{entry.Option}", iconId, icon, alpha))
                    Change(entry.Option);

                if (ImGui.IsItemHovered())
                {
                    var state = entry.Option == DutyFinderOption.LootRule ? "點一下換下一種" : value != 0 ? "目前：開" : "目前：關";

                    // 提示框裡的行距用正常值（外面那排把垂直間距設成 0）
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing with { Y = 4f });
                    ImGui.SetTooltip($"{Addons.Text(tooltip)}\n{state}");
                    ImGui.PopStyleVar();
                }

                ImGui.SameLine();
            }

            ImGui.NewLine();
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private static bool IconToggle(string id, uint iconId, float size, float alpha)
    {
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size));

        var texture = Icons.Get(iconId);
        if (texture != null)
        {
            var hovered = ImGui.IsItemHovered();
            var tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, hovered ? Math.Min(1f, alpha + 0.3f) : alpha));
            ImGui.GetWindowDrawList().AddImage(texture.Handle, pos, pos + new Vector2(size), Vector2.Zero, Vector2.One, tint);
        }

        return clicked;
    }

    /// <summary>改設定要在遊戲執行緒做。</summary>
    private void Change(DutyFinderOption option)
    {
        Services.Framework.RunOnFrameworkThread(() =>
        {
            var before = DutyFinderSettings.Get(option);
            DutyFinderSettings.Toggle(option);
            Log.Write($"{option} {before} → {DutyFinderSettings.Get(option)}（{(DutyFinderSettings.UsesGameFunction ? "遊戲函式" : "直接改值")}）");
        });
    }
}

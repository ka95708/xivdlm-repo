using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 招募板自動刷新：招募板上方多一個「自動刷新 ▶」按鈕，按下後每隔幾秒刷新一次招募清單，
/// 再按一次（或關掉招募板）就停。看某一則招募的詳細內容時先不刷新，關掉詳細內容後重新倒數。
/// 刷新呼叫的是招募板按「更新」時遊戲自己做的那個動作。
/// </summary>
public sealed unsafe class PartyFinderRefresh : Tweak
{
    private const string Addon = "LookingForGroup";
    private const string DetailAddon = "LookingForGroupDetail";

    private const int MinSeconds = 5;
    private const int MaxSeconds = 300;

    private bool running;
    private DateTime nextRefresh;

    public override string Id => "party-finder-refresh";
    public override string Name => "招募板自動刷新";
    public override string Description => "招募板加「自動刷新」按鈕，開了之後每隔幾秒自動刷新招募清單。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Button;
    public override string Where => "招募板視窗上方";

    private int Interval => Math.Clamp(Config.PartyFinderRefreshSeconds, MinSeconds, MaxSeconds);

    protected override void OnDisable() => running = false;

    public override void Update(DateTime now)
    {
        if (!running) return;

        if (Addons.Get(Addon) == null)
        {
            running = false;
            return;
        }

        // 正在看詳細內容：不刷新，關掉後重新倒數
        if (Addons.Get(DetailAddon) != null)
        {
            nextRefresh = now.AddSeconds(Interval);
            return;
        }

        if (now < nextRefresh) return;

        var agent = AgentLookingForGroup.Instance();
        if (agent != null) agent->RequestListingsUpdate();
        nextRefresh = now.AddSeconds(Interval);
    }

    public override void AddButtons(AddonBar bar)
    {
        if (Addons.Get(Addon) == null) return;

        if (running)
        {
            var left = Math.Max(0, (int)Math.Ceiling((nextRefresh - DateTime.Now).TotalSeconds));
            bar.Button(Addon, $"自動刷新 ■（{left}）", () => running = false, tooltip: "按一下停止自動刷新");
        }
        else
        {
            bar.Button(Addon, "自動刷新 ▶", Start, tooltip: $"每 {Interval} 秒刷新一次招募清單");
        }

        bar.Options(Addon, Name, DrawSettings);
    }

    private void Start()
    {
        running = true;
        nextRefresh = DateTime.Now.AddSeconds(Interval);
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var seconds = Interval;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("刷新間隔（秒）", ref seconds, 5))
        {
            Config.PartyFinderRefreshSeconds = Math.Clamp(seconds, MinSeconds, MaxSeconds);
            Save();
            if (running) nextRefresh = DateTime.Now.AddSeconds(Interval);
        }
    }
}

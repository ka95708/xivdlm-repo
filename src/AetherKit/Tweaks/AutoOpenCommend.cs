using System;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 任務完成時打開推薦視窗（Simple Tweaks 的 Open Commendation Window Automatically）：
/// 畫面右邊出現「推薦隊友」的通知圖示時，像點它一樣打開推薦視窗。每次出現只開一次，過場中先等。
/// </summary>
public sealed class AutoOpenCommend : Tweak
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private DateTime nextCheck;
    private bool opened;

    public override string Id => "auto-open-commend";
    public override string Name => "任務完成時打開推薦視窗";
    public override string Description => "副本結束、出現推薦隊友的通知時，自動打開推薦視窗。";
    public override TweakGroup Group => TweakGroup.Duty;
    public override TweakKind Kind => TweakKind.Auto;

    public override void Update(DateTime now)
    {
        if (now < nextCheck) return;
        nextCheck = now + CheckInterval;

        if (GameState.InCutscene || GameState.BetweenAreas) return;

        // 通知圖示消失：下一次出現再開
        if (!Addons.IsOpen("_NotificationIcMvp"))
        {
            opened = false;
            return;
        }

        if (opened || Addons.IsOpen("VoteMvp")) return;

        if (!OpenFromNotification()) return;

        opened = true;
        Log.Write("出現推薦通知，打開推薦視窗");
    }

    private static unsafe bool OpenFromNotification()
    {
        var notification = Addons.Get("_Notification");
        if (notification == null) return false;

        // 跟點畫面右邊的推薦通知一樣
        Addons.Fire(notification, 0, 11);
        return true;
    }
}

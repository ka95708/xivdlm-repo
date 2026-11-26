using System;

namespace AetherKit.Core;

/// <summary>自動流程完成後要做什麼。</summary>
public enum FinishAction
{
    None,
    Logout,
    Shutdown
}

public static class FinishActionNames
{
    public static string Name(this FinishAction action) => action switch
    {
        FinishAction.Logout => "登出（回標題畫面）",
        FinishAction.Shutdown => "關閉遊戲",
        _ => "不做事"
    };

    private static string Verb(this FinishAction action) => action == FinishAction.Logout ? "登出" : "關閉遊戲";

    public static string Countdown(this FinishAction action, int seconds) => $"{seconds} 秒後{action.Verb()}";
}

/// <summary>
/// 自動流程「完成」（達到設定的目標，不是手動停止、出錯或卡住）之後登出或關閉遊戲。
/// 先倒數一段時間，期間可以在模組頁取消；時間到送遊戲指令 /logout 或 /shutdown（跟自己打一樣），
/// 再按跳出來的確認框 —— 只認遊戲文字表裡登出（Addon 115）、結束遊戲（Addon 116）那兩句，別的確認框不碰。
/// </summary>
public sealed class FinishService
{
    public const int CountdownSeconds = 30;

    // 確認框的文字（Addon 表）：115「確定要退出並回到標題畫面嗎？」、116「確定要退出並結束遊戲嗎？」
    private const uint LogoutPrompt = 115;
    private const uint ShutdownPrompt = 116;

    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    private readonly Plugin plugin;
    private readonly FeatureLog log = FeatureLog.For("finish");

    private DateTime fireAt;
    private DateTime? confirmUntil;

    public FinishService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>排定要做的事；None = 沒有。</summary>
    public FinishAction Pending { get; private set; }

    /// <summary>是哪個功能排的（模組頁只顯示自己排的）。</summary>
    public string Owner { get; private set; } = string.Empty;

    public int SecondsLeft => Math.Max(0, (int)Math.Ceiling((fireAt - DateTime.Now).TotalSeconds));

    /// <summary>已經送出指令、正在等確認框。</summary>
    public bool Executing => confirmUntil != null;

    /// <summary>某個功能完成了：照設定排登出或關閉遊戲。None 就不做事。</summary>
    public void Schedule(FinishAction action, string owner)
    {
        if (action == FinishAction.None) return;

        Pending = action;
        Owner = owner;
        fireAt = DateTime.Now.AddSeconds(CountdownSeconds);
        confirmUntil = null;

        log.Write($"{owner} 完成：{action.Countdown(CountdownSeconds)}");
        // 會關掉遊戲，一定要讓人看到
        Notify.Info(owner, $"完成，{action.Countdown(CountdownSeconds)}（到 AetherKit 的模組頁可以取消）");
    }

    public void Cancel()
    {
        if (Pending == FinishAction.None) return;

        log.Write($"取消 {Pending.Name()}（{Owner}）");
        Notify.Info("完成後動作", $"已取消{Pending.Name()}。");
        Pending = FinishAction.None;
        confirmUntil = null;
    }

    /// <summary>每幀（遊戲執行緒）。</summary>
    public void Update(DateTime now)
    {
        if (Pending == FinishAction.None) return;

        if (confirmUntil is { } until)
        {
            if (TryConfirm()) return;

            if (now > until)
            {
                log.Write($"{Pending.Name()}：送出指令後 {ConfirmTimeout.TotalSeconds:0} 秒內沒有看到確認框，放棄");
                Notify.Error("完成後動作", $"{Pending.Name()}沒有成功：沒有跳出確認框。");
                Pending = FinishAction.None;
                confirmUntil = null;
            }

            return;
        }

        if (now < fireAt) return;

        var command = Pending == FinishAction.Logout ? "/logout" : "/shutdown";
        log.Write($"{Pending.Name()}：送出 {command}");
        plugin.Executor.Enqueue(command);
        confirmUntil = now + ConfirmTimeout;
    }

    /// <summary>確認框是登出／結束遊戲那一句才按「是」。</summary>
    private unsafe bool TryConfirm()
    {
        var yesno = Addons.Get("SelectYesno");
        if (yesno == null || !yesno->IsVisible) return false;

        var expected = Addons.Text(Pending == FinishAction.Logout ? LogoutPrompt : ShutdownPrompt);
        var prompt = Addons.YesnoPrompt(yesno).Trim();
        if (expected.Length == 0 || !prompt.StartsWith(expected, StringComparison.Ordinal)) return false;

        if (!Addons.ClickYes(yesno)) return false;

        log.Write($"{Pending.Name()}：按了確認框的「是」");
        Pending = FinishAction.None;
        confirmUntil = null;
        return true;
    }
}

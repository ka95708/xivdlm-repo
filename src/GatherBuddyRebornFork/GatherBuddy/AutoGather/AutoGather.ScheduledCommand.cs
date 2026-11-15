using System;
using ECommons.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private enum ScheduledCommandState
    {
        Off,
        Armed,
        WaitingGatherEnd,
        WaitingReady,
        WaitingResume,
    }

    private ScheduledCommandState _scheduledState;
    private DateTime _scheduledExecuteAt;
    private DateTime _scheduledWaitStartedAt;
    private DateTime _scheduledResumeAt;
    private DateTime _scheduledHardResumeAt;
    private bool _scheduledInternalStateChange;
    private bool _scheduledDisabledAutoGather;
    private bool _scheduledUsesLifestream;

    private void HandleEnabledStateChange(bool enabled)
    {
        if (_scheduledInternalStateChange)
            return;

        if (enabled && GatherBuddy.Config.AutoGatherConfig.EnableScheduledCommand)
            ArmScheduledCommand();
        else
            CancelScheduledCommand(false);

        if (enabled)
            _antiStuckManager.OnSessionStart();
    }

    private void ArmScheduledCommand()
    {
        var minutes = Math.Clamp(GatherBuddy.Config.AutoGatherConfig.ScheduledCommandIntervalMinutes, 1, 240);
        _scheduledExecuteAt = DateTime.Now.AddMinutes(minutes);
        _scheduledState = ScheduledCommandState.Armed;
        _scheduledDisabledAutoGather = false;
        GatherBuddy.Log.Information($"排程指令已啟用，{minutes} 分鐘後執行。");
    }

    private void CancelScheduledCommand(bool resumeIfNeeded)
    {
        var shouldResume = resumeIfNeeded && _scheduledDisabledAutoGather;
        _scheduledState = ScheduledCommandState.Off;
        _scheduledDisabledAutoGather = false;
        _scheduledUsesLifestream = false;
        if (shouldResume && !Enabled)
            SetEnabledFromSchedule(true);
    }

    private void SetEnabledFromSchedule(bool enabled)
    {
        _scheduledInternalStateChange = true;
        try
        {
            Enabled = enabled;
        }
        finally
        {
            _scheduledInternalStateChange = false;
        }
    }

    private bool HandleScheduledCommand()
    {
        var config = GatherBuddy.Config.AutoGatherConfig;
        if (!config.EnableScheduledCommand)
        {
            if (_scheduledState != ScheduledCommandState.Off)
                CancelScheduledCommand(true);
            return false;
        }

        switch (_scheduledState)
        {
            case ScheduledCommandState.Off:
                if (Enabled)
                    ArmScheduledCommand();
                return false;

            case ScheduledCommandState.Armed:
                if (!Enabled)
                {
                    CancelScheduledCommand(false);
                    return false;
                }
                if (DateTime.Now < _scheduledExecuteAt)
                    return false;
                _scheduledWaitStartedAt = DateTime.Now;
                _scheduledState = ScheduledCommandState.WaitingGatherEnd;
                GatherBuddy.Log.Information("排程時間到，等待目前採集動作結束。");
                return true;

            case ScheduledCommandState.WaitingGatherEnd:
                AutoStatus = "排程：等待目前採集結束...";
                if (IsGathering && (DateTime.Now - _scheduledWaitStartedAt).TotalSeconds < 120)
                    return true;

                if (config.CloseGatheringUiBeforeScheduledCommand)
                    CloseGatheringAddons();
                StopNavigation();
                _scheduledDisabledAutoGather = true;
                SetEnabledFromSchedule(false);
                _scheduledWaitStartedAt = DateTime.Now;
                _scheduledState = ScheduledCommandState.WaitingReady;
                return true;

            case ScheduledCommandState.WaitingReady:
                AutoStatus = "排程：等待角色可執行指令...";
                if (!CanAct)
                {
                    if ((DateTime.Now - _scheduledWaitStartedAt).TotalSeconds < 180)
                        return true;
                    GatherBuddy.Log.Warning("排程指令等待角色可操作逾時，本輪略過。");
                    ResumeAndRearmScheduledCommand();
                    return true;
                }

                var command = (config.ScheduledCommand ?? string.Empty).Trim();
                if (!command.StartsWith('/'))
                {
                    Communicator.PrintError("[GatherBuddy] 排程指令必須以 / 開頭。");
                    ResumeAndRearmScheduledCommand();
                    return true;
                }

                try
                {
                    Chat.Instance.ExecuteCommand(command);
                    var delay = Math.Clamp(config.ScheduledCommandResumeDelayMinutes, 1, 60);
                    _scheduledResumeAt = DateTime.Now.AddMinutes(delay);
                    _scheduledHardResumeAt = _scheduledResumeAt.AddMinutes(15);
                    _scheduledUsesLifestream = command.StartsWith("/li", StringComparison.OrdinalIgnoreCase);
                    _scheduledState = ScheduledCommandState.WaitingResume;
                    Communicator.Print($"[GatherBuddy] 排程指令已執行，至少 {delay} 分鐘後恢復採集。");
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Error($"排程指令執行失敗: {ex}");
                    ResumeAndRearmScheduledCommand();
                }
                return true;

            case ScheduledCommandState.WaitingResume:
                var remaining = (_scheduledResumeAt - DateTime.Now).TotalSeconds;
                if (remaining > 0)
                {
                    AutoStatus = $"排程：{Math.Ceiling(remaining)} 秒後恢復...";
                    return true;
                }

                if (_scheduledUsesLifestream && Lifestream.Enabled && Lifestream.IsBusy()
                    && DateTime.Now < _scheduledHardResumeAt)
                {
                    AutoStatus = "排程：等待 Lifestream 完成...";
                    return true;
                }

                ResumeAndRearmScheduledCommand();
                return true;

            default:
                return false;
        }
    }

    private void ResumeAndRearmScheduledCommand()
    {
        if (_scheduledDisabledAutoGather && !Enabled)
            SetEnabledFromSchedule(true);

        var minutes = Math.Clamp(GatherBuddy.Config.AutoGatherConfig.ScheduledCommandIntervalMinutes, 1, 240);
        _scheduledDisabledAutoGather = false;
        _scheduledUsesLifestream = false;
        _scheduledExecuteAt = DateTime.Now.AddMinutes(minutes);
        _scheduledState = ScheduledCommandState.Armed;
        Communicator.Print($"[GatherBuddy] 自動採集已恢復，下次排程於 {minutes} 分鐘後執行。");
    }

    public string GetScheduledCommandStatus()
    {
        if (!GatherBuddy.Config.AutoGatherConfig.EnableScheduledCommand)
            return string.Empty;

        return _scheduledState switch
        {
            ScheduledCommandState.Armed => $"下次執行：{Math.Max(0, (_scheduledExecuteAt - DateTime.Now).TotalMinutes):F1} 分鐘後",
            ScheduledCommandState.WaitingGatherEnd => "等待目前採集結束",
            ScheduledCommandState.WaitingReady => "等待角色可操作",
            ScheduledCommandState.WaitingResume when _scheduledUsesLifestream && DateTime.Now >= _scheduledResumeAt
                => "等待 Lifestream 完成",
            ScheduledCommandState.WaitingResume => $"恢復倒數：{Math.Max(0, (_scheduledResumeAt - DateTime.Now).TotalSeconds):F0} 秒",
            _ => string.Empty,
        };
    }
}

using System;

namespace AetherKit.Core;

/// <summary>
/// 操作鎖：會移動角色、點遊戲視窗的流程，同一時間只能有一個在跑，
/// 免得 AutoFate 在趕路時另一個功能去開分解視窗。
/// 拿鎖時要給一個「停止」的方法，核心能力頁的停止按鈕和其他功能的「先停掉它」都用這個。
/// </summary>
public sealed class OperationLock
{
    private Action? stop;

    public string? Owner { get; private set; }

    /// <summary>給人看的名稱，例如「自動Fate」。</summary>
    public string? Label { get; private set; }

    public DateTime Since { get; private set; }

    public bool Busy => Owner != null;

    /// <summary>拿鎖。別人拿著就失敗；自己已經拿著就當成功（可以更新名稱與停止方法）。</summary>
    public bool TryAcquire(string owner, string label, Action stopAction)
    {
        if (Owner != null && Owner != owner) return false;

        if (Owner == null) Since = DateTime.Now;

        Owner = owner;
        Label = label;
        stop = stopAction;
        return true;
    }

    public void Release(string owner)
    {
        if (Owner != owner) return;

        Owner = null;
        Label = null;
        stop = null;
    }

    /// <summary>請目前拿著鎖的流程停下來。它停止時會自己 Release。</summary>
    public void RequestStop()
    {
        var action = stop;
        if (action == null) return;

        try
        {
            action();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, $"停止 {Label} 時發生錯誤");
        }
    }

    /// <summary>拿著鎖的人沒有正常歸還（例如例外）時，給核心能力頁強制清掉。</summary>
    public void ForceClear()
    {
        Owner = null;
        Label = null;
        stop = null;
    }
}

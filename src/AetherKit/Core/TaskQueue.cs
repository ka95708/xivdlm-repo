using System;
using System.Collections.Generic;

namespace AetherKit.Core;

/// <summary>
/// 一步一步執行的工作清單，給「按一下就跑一陣子」的功能用（一鍵分解、修理……）。
/// 每一步是一個函式：回傳 true = 完成，換下一步；false = 還沒好，下一幀再試；null = 失敗，整串中止。
/// 每一步有逾時；一幀最多做一步，介面才有時間反應。由擁有者在自己的更新裡呼叫 Tick。
/// </summary>
public sealed class TaskQueue
{
    private sealed record Step(string Name, Func<bool?> Run, TimeSpan Timeout, TimeSpan DelayBefore);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private DateTime currentStart;
    private DateTime notBefore;

    public bool Busy => current != null || steps.Count > 0;

    /// <summary>目前這一步的名稱，給狀態列顯示。</summary>
    public string? CurrentStep => current?.Name ?? (steps.Count > 0 ? steps.Peek().Name : null);

    /// <summary>整串做完（null）或中止（原因）時呼叫。</summary>
    public event Action<string?>? Finished;

    public TaskQueue Enqueue(string name, Func<bool?> run, double timeoutSeconds = 10, double delayBeforeSeconds = 0)
    {
        steps.Enqueue(new Step(name, run, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(delayBeforeSeconds)));
        return this;
    }

    /// <summary>單純等一段時間。</summary>
    public TaskQueue Delay(double seconds) =>
        Enqueue("等待", () => true, seconds + 5, seconds);

    public void Abort(string reason = "已中止")
    {
        if (!Busy) return;

        steps.Clear();
        current = null;
        Finished?.Invoke(reason);
    }

    public void Tick(DateTime now)
    {
        if (current == null)
        {
            if (steps.Count == 0) return;

            current = steps.Dequeue();
            currentStart = now;
            notBefore = now + current.DelayBefore;
        }

        if (now < notBefore) return;

        bool? result;
        try
        {
            result = current.Run();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, $"工作步驟「{current.Name}」發生錯誤");
            Fail($"{current.Name}：{e.Message}");
            return;
        }

        if (result == null)
        {
            Fail($"{current.Name}失敗");
            return;
        }

        if (result == false)
        {
            if (now - notBefore > current.Timeout)
                Fail($"{current.Name}逾時");

            return;
        }

        current = null;
        if (steps.Count == 0)
            Finished?.Invoke(null);
    }

    private void Fail(string reason)
    {
        steps.Clear();
        current = null;
        Finished?.Invoke(reason);
    }
}

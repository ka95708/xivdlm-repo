using System;
using System.Collections.Generic;

namespace AetherKit.Core;

/// <summary>
/// 一步一步往下做的自動流程，用 C# 迭代器寫：
/// <code>
/// yield return Routine.Until(() =&gt; Addons.IsOpen("Bank"), 5);   // 等到條件成立或超時
/// yield return Routine.Delay(0.5);                               // 等固定時間
/// yield return null;                                             // 等下一次推進
/// yield return SubStep();                                        // 先跑完另一段流程
/// </code>
/// 由擁有它的功能每幀（遊戲執行緒）呼叫 Advance 推進。迭代器裡不能用指標，
/// 碰遊戲記憶體的部分寫成一般方法再呼叫。
/// </summary>
public sealed class Routine
{
    /// <summary>等到條件成立或超時（超時也往下走，流程自己再檢查一次）。</summary>
    public sealed record Wait(Func<bool> Condition, TimeSpan Timeout);

    private readonly Stack<IEnumerator<object?>> stack = new();
    private Func<bool>? waiting;
    private DateTime deadline;

    public static Wait Until(Func<bool> condition, double seconds) => new(condition, TimeSpan.FromSeconds(seconds));

    public static Wait Delay(double seconds) => new(() => false, TimeSpan.FromSeconds(seconds));

    public bool Running => stack.Count > 0;

    public void Start(IEnumerable<object?> routine)
    {
        Clear();
        stack.Push(routine.GetEnumerator());
    }

    public void Clear()
    {
        stack.Clear();
        waiting = null;
    }

    /// <summary>推進到下一個等待為止。流程全部跑完回傳 true。</summary>
    public bool Advance(DateTime now)
    {
        if (waiting != null)
        {
            if (!waiting() && now < deadline) return false;
            waiting = null;
        }

        while (stack.Count > 0)
        {
            var top = stack.Peek();
            var more = top.MoveNext();

            // 流程裡呼叫了 Clear（例如停止）：堆疊已經清掉
            if (stack.Count == 0) return false;

            if (!more)
            {
                stack.Pop();
                continue;
            }

            switch (top.Current)
            {
                case null:
                    return false;
                case Wait wait:
                    if (wait.Condition()) continue;
                    waiting = wait.Condition;
                    deadline = now + wait.Timeout;
                    return false;
                case IEnumerable<object?> sub:
                    stack.Push(sub.GetEnumerator());
                    continue;
            }
        }

        return true;
    }
}

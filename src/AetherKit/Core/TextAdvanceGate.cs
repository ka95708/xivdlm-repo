using System;
using System.Collections.Generic;

namespace AetherKit.Core;

/// <summary>
/// 和 TextAdvance 共存：TextAdvance 開放一份共用的「暫停名單」，名單不是空的它就暫停。
/// AetherKit 的對話類能力作用期間把自己加進去，結束就拿掉，兩邊就不會搶著點同一個視窗。
/// 沒裝 TextAdvance 也沒關係，這份名單只是一個共用資料，沒人看而已。
/// </summary>
public sealed class TextAdvanceGate : IDisposable
{
    private const string DataTag = "TextAdvance.StopRequests";

    private readonly string name;
    private HashSet<string>? requests;
    private bool added;

    public TextAdvanceGate()
    {
        name = Services.PluginInterface.InternalName;

        try
        {
            requests = Services.PluginInterface.GetOrCreateData(DataTag, () => new HashSet<string>());
        }
        catch (Exception e)
        {
            Services.Log.Warning($"無法取得 TextAdvance 暫停名單：{e.Message}");
        }
    }

    /// <summary>TextAdvance 目前是不是因為 AetherKit 暫停中。</summary>
    public bool Pausing => added;

    public void Set(bool pause)
    {
        if (requests == null || pause == added) return;

        if (pause) requests.Add(name);
        else requests.Remove(name);

        added = pause;
    }

    public void Dispose()
    {
        Set(false);

        if (requests != null)
            Services.PluginInterface.RelinquishData(DataTag);

        requests = null;
    }
}

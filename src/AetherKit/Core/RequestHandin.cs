using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AetherKit.Core;

/// <summary>
/// 交道具：NPC 的「交出道具」視窗（Request）出現時，逐格打開道具選單、選第一個候選，
/// 全部填好後按「交出」。照 TextAdvance 的經驗，視窗剛出現時先等一下再動，填完也等一下才按交出。
/// 選單沒跳出來就當作那一格不存在或已經填好，換下一格。按住暫停鍵可以中途停下來自己處理。
/// </summary>
public sealed unsafe class RequestHandin : Capability
{
    private const int MaxSlots = 8;

    // 選道具選單裡的候選時，遊戲自己送的第三個值
    private const uint PickMagic = 1021003;

    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MenuWait = TimeSpan.FromSeconds(1);

    private bool seen;
    private DateTime readyAt;
    private int slot;
    private DateTime menuRequestedAt;
    private bool menuRequested;
    private DateTime nextAction;

    public RequestHandin(CoreServices core) : base(core)
    {
    }

    public override string Id => "handin";
    public override string Name => "交道具";
    public override string Description => "NPC 要你交道具時，自動把道具放進去並按「交出」。";
    public override bool OverlapsTextAdvance => true;

    protected override void OnDeactivated() => Reset();

    private void Reset()
    {
        seen = false;
        slot = 0;
        menuRequested = false;
    }

    protected override void Update(DateTime now)
    {
        var request = Addons.Get("Request");
        if (request == null)
        {
            Reset();
            return;
        }

        if (!seen)
        {
            seen = true;
            readyAt = now + SettleDelay;
        }

        if (now < readyAt || now < nextAction) return;

        var addon = (AddonRequest*)request;
        if (Addons.ClickButton(request, addon->HandOverButton))
        {
            nextAction = now + TimeSpan.FromSeconds(1);
            return;
        }

        var menu = Addons.Get("ContextIconMenu");
        if (menu != null)
        {
            Addons.Fire(menu, 0, 0, PickMagic, 0u, 0);
            menuRequested = false;
            slot++;
            nextAction = now + SettleDelay;
            return;
        }

        if (slot >= MaxSlots) return;

        if (!menuRequested)
        {
            // 打開第 slot 格的道具選單
            Addons.Fire(request, 2, slot, 0, 0);
            menuRequested = true;
            menuRequestedAt = now;
            nextAction = now + TimeSpan.FromMilliseconds(100);
            return;
        }

        // 選單一直沒出來：這一格不存在或已經放好了，換下一格
        if (now - menuRequestedAt > MenuWait)
        {
            menuRequested = false;
            slot++;
        }
    }
}

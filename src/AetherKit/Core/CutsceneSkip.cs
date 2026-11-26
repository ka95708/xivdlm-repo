using System;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;

namespace AetherKit.Core;

/// <summary>
/// 過場跳過（只跳遊戲本來就允許跳過的過場）。
///
/// 主要做法和 TextAdvance 同一個原理：hook 遊戲「過場中處理輸入」的函式，在過場可以跳過時，
/// 呼叫原函式的那一瞬間把「玩家有沒有要跳過」的判斷改成成立，呼叫完立刻改回去。
/// 對遊戲來說就是這一幀玩家按了跳過，接著跳出「要跳過這段過場動畫嗎？」，再由這裡按「是」。
/// 找不到特徵碼（例如遊戲改版）時退回送 Esc 按鍵給遊戲視窗，慢一點但一樣會問要不要跳過。
/// </summary>
public sealed unsafe class CutsceneSkip : Capability
{
    // 過場中處理輸入的函式，以及其中「要不要跳過」那個條件跳轉（jne）
    // [Hook] 過場跳過：特徵碼與 hook，改版後先確認（見 改版檢查清單.md）
    private const string HandleInputSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 79 28 00";

    private const string SkipCheckSignature = "75 11 BA ?? ?? ?? ?? 48 8B CF E8 ?? ?? ?? ?? 84 C0 74 4C";

    private const byte Jne = 0x75;
    private const byte Jmp = 0xEB;

    // 「要跳過這段過場動畫嗎？」
    private const uint SkipPromptRow = 281;

    // 這張地圖淡入淡出畫面時不能跳（TextAdvance 也有這個例外）
    private const uint FadeExceptionTerritory = 670;

    private delegate byte HandleInputDelegate(nint self, float delta);

    private readonly Hook<HandleInputDelegate>? hook;
    private readonly nint skipCheck;

    private bool skipNow;
    private DateTime nextConfirm;
    private DateTime nextEsc;

    public CutsceneSkip(CoreServices core) : base(core)
    {
        try
        {
            var handleInput = Services.SigScanner.ScanText(HandleInputSignature);
            skipCheck = Services.SigScanner.ScanText(SkipCheckSignature);
            hook = Services.GameInterop.HookFromAddress<HandleInputDelegate>(handleInput, Detour);
        }
        catch (Exception e)
        {
            Services.Log.Warning($"過場跳過找不到特徵碼，改用 Esc 模式：{e.Message}");
            hook = null;
        }
    }

    public override string Id => "cutscene";
    public override string Name => "過場跳過";
    public override string Description => "自動跳過可以跳過的過場（不能跳過的照常播放），並確認「要跳過嗎？」。";
    public override bool OverlapsTextAdvance => true;
    public override string? Note => hook != null ? "hook 模式" : "Esc 模式（找不到特徵碼）";

    protected override void OnActivated() => hook?.Enable();

    protected override void OnDeactivated()
    {
        skipNow = false;
        hook?.Disable();
    }

    protected override void Update(DateTime now)
    {
        skipNow = ShouldSkip();
        if (!GameState.InCutscene) return;

        // 「要跳過這段過場動畫嗎？」→ 選第一個（是）
        if (now >= nextConfirm)
        {
            var select = Addons.Get("SelectString");
            if (select != null && Addons.ContainsText(select, Addons.Text(SkipPromptRow)))
            {
                Addons.Fire(select, 0);
                nextConfirm = now + TimeSpan.FromMilliseconds(500);
                return;
            }
        }

        if (hook == null && skipNow && now >= nextEsc)
        {
            SendEscape();
            nextEsc = now + TimeSpan.FromSeconds(1);
        }
    }

    private bool ShouldSkip()
    {
        if (!GameState.InCutscene) return false;

        var territory = Services.ClientState.TerritoryType;
        if (Core.Config.CutsceneKeepZones.Contains(territory)) return false;
        if (territory == FadeExceptionTerritory && Addons.IsOpen("FadeMiddle")) return false;

        return true;
    }

    private byte Detour(nint self, float delta)
    {
        // 只有過場中、而且這段過場本身可以跳過（self+0x38 有值）才動手
        if (!skipNow || !Services.Condition[(ConditionFlag)35] || *(nint*)(self + 0x38) == 0)
            return hook!.Original(self, delta);

        try
        {
            SafeMemory.WriteBytes(skipCheck, new[] { Jmp });
            return hook!.Original(self, delta);
        }
        finally
        {
            SafeMemory.WriteBytes(skipCheck, new[] { Jne });
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>對遊戲視窗送一下 Esc（跟玩家按 Esc 一樣），只在找不到特徵碼時用。</summary>
    private static void SendEscape()
    {
        const uint keyDown = 0x0100, keyUp = 0x0101;
        const int escape = 0x1B;

        var window = FindWindow("FFXIVGAME", null);
        if (window == 0) return;

        PostMessage(window, keyDown, escape, 0);
        PostMessage(window, keyUp, escape, 0);
    }

    public override bool HasSettings => true;

    public override void DrawSettings() => CoreSettingsUi.CutsceneZones(Core);

    public override void Dispose()
    {
        if (hook != null)
        {
            hook.Dispose();
            SafeMemory.WriteBytes(skipCheck, new[] { Jne });
        }
    }
}

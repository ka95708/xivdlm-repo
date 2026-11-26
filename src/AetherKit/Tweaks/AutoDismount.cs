using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 坐騎上自動下坐騎：騎著坐騎按了技能之類的東西，遊戲跳出紅字錯誤訊息（例如「騎乘中無法執行」）時，
/// 自動按一次下坐騎，再按一次技能就能用了。
///
/// 只看畫面中央的紅字錯誤訊息來判斷「玩家想做某件事但被坐騎擋住」，不攔截遊戲的技能函式；
/// 下坐騎用的是一般「下坐騎」動作，跟按坐騎按鈕一樣。技能不會自動補按，要自己再按一次。
/// 預設飛行中、潛水中不動作，免得在空中被放下來；別的自動化（例如自動Fate）執行中也不動作。
/// </summary>
public sealed unsafe class AutoDismount : Tweak
{
    // 一般動作「下坐騎」
    private const uint DismountAction = 23;

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    // 自己按完下坐騎後這段時間內又出現紅字，多半是「這裡不能下坐騎」，先別再按
    private static readonly TimeSpan OwnErrorWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackOff = TimeSpan.FromSeconds(10);

    private volatile bool pending;
    private DateTime lastDismount;
    private DateTime pausedUntil;

    public override string Id => "auto-dismount";
    public override string Name => "坐騎上自動下坐騎";
    public override string Description => "騎著坐騎做了要下坐騎才能做的事，遊戲跳「騎乘狀態下無法進行該操作」時自動下坐騎，再按一次就能用。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;

    protected override void OnEnable() => Services.Toasts.ErrorToast += OnErrorToast;

    protected override void OnDisable()
    {
        Services.Toasts.ErrorToast -= OnErrorToast;
        pending = false;
    }

    /// <summary>
    /// 遊戲文字表（LogMessage）裡「騎乘狀態下無法……」那幾條（台服 7704、7710、7764、11332 等），
    /// 第一次用到時從資料表讀，換語言也對得上。
    /// </summary>
    private static string[]? mountedErrors;

    private static string[] MountedErrors => mountedErrors ??= LoadMountedErrors();

    private static string[] LoadMountedErrors()
    {
        var sheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
        return new uint[] { 7704, 7710, 7764, 11332 }
            .Select(id => sheet.GetRowOrDefault(id)?.Text.ExtractText().Trim() ?? string.Empty)
            .Where(text => text.Length > 0)
            .Distinct()
            .ToArray();
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (!GameState.Mounted) return;

        // 只認「騎乘狀態下無法……」，其他紅字（距離太遠、目標不對……）不下坐騎。
        // 騎著時看到的紅字都記進 log，萬一某種情況沒反應，可以從 log 看遊戲實際說了什麼。
        var text = message.TextValue.Trim();
        var match = MountedErrors.Any(e => text == e);
        Log.Write($"騎乘中紅字：{text}{(match ? "（下坐騎）" : "")}");
        if (match) pending = true;
    }

    public override void Update(DateTime now)
    {
        if (!pending) return;
        pending = false;

        // 剛自己按過下坐騎就又跳紅字：多半是這裡不能下，等一陣子再說
        if (now - lastDismount < OwnErrorWindow)
        {
            pausedUntil = now + BackOff;
            return;
        }

        if (now < pausedUntil || now - lastDismount < Cooldown || !CanDismount()) return;

        ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
        lastDismount = now;
    }

    private bool CanDismount()
    {
        if (!GameState.Mounted || GameState.InEvent || GameState.BetweenAreas || GameState.InCutscene) return false;
        if (Core.Lock.Busy) return false;

        var condition = Services.Condition;
        if (condition[ConditionFlag.Diving]) return false;
        if (condition[ConditionFlag.InFlight] && !Config.AutoDismountInFlight) return false;

        // 坐在別人的多人坐騎後座：不替他做決定
        if (condition[ConditionFlag.RidingPillion]) return false;

        return true;
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var flight = Config.AutoDismountInFlight;
        if (ImGui.Checkbox("飛行中也自動降落", ref flight))
        {
            Config.AutoDismountInFlight = flight;
            Save();
        }

        ImGui.TextDisabled("只對「騎乘狀態下無法……」這種紅字反應；技能本身不會自動補按，下坐騎後再按一次。");
    }
}

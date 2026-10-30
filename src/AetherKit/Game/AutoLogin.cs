using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 自動登入：標題畫面按「開始遊戲」→ 角色清單選指定的角色（不在目前的伺服器分頁就先切過去）
/// → 確認對話框按「是」→ 等到進入遊戲。台服沒有選資料中心這一步。
///
/// 操作方式參考 AutoRetainer 台服版（它用 ECommons 的 AddonMaster）：按鈕是送出按鈕自己的事件，
/// 角色是模擬滑鼠點擊，伺服器分頁是送 callback。跟手動點擊走的是同一條路。
///
/// 安全機制：
///   按住 Shift — 取消這次自動登入
///   每次開遊戲只跑一次 — 登出回標題不會再登入，重新載入插件也不會
///   出錯就停 — 出現錯誤對話框、某一步等太久、角色被鎖住，都停下來並在聊天欄說明
/// </summary>
public sealed unsafe class AutoLogin : IDisposable
{
    private enum Step
    {
        /// <summary>沒在跑：還沒啟用、已經跑完、或被取消。</summary>
        Idle,
        Title,
        Character,
        Confirm,
        Entering
    }

    /// <summary>每一步最多等多久。標題要等遊戲載入與連線，給得最寬。</summary>
    private static TimeSpan TimeoutOf(Step step) => step switch
    {
        Step.Title => TimeSpan.FromMinutes(3),
        Step.Character => TimeSpan.FromSeconds(60),
        Step.Confirm => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(2)
    };

    /// <summary>
    /// 這次開遊戲已經跑過（或取消過）的記號。放在行程的環境變數裡：
    /// 重新載入插件不會清掉，關遊戲重開才會 — 剛好是「每次開遊戲一次」。
    /// </summary>
    private const string SessionMarker = "AETHERKIT_AUTOLOGIN_DONE";

    private const int VkShift = 0x10;

    /// <summary>按完一次之後，至少隔這麼久才會再按，等畫面反應。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly Plugin plugin;

    private Step step = Step.Idle;
    private DateTime stepStarted;
    private DateTime readySince = DateTime.MaxValue;
    private DateTime nextAction;
    private DateTime nextRecord;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public AutoLogin(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private AutoLoginConfig Config => plugin.Config.AutoLogin;

    /// <summary>給設定頁顯示的目前狀態。</summary>
    public string Status { get; private set; } = "待命";

    public bool Running => step != Step.Idle;

    public void Start()
    {
        Services.Framework.Update += OnUpdate;

        if (Environment.GetEnvironmentVariable(SessionMarker) != null)
        {
            Status = "這次開遊戲已經執行過";
            return;
        }

        if (Services.ClientState.IsLoggedIn)
        {
            // 插件是在遊戲中才載入的，這次開遊戲就不用再登入了
            MarkSession();
            Status = "已在遊戲中";
            return;
        }

        if (Config.CharacterId == 0)
        {
            Status = "還沒指定角色";
            return;
        }

        Enter(Step.Title);
        Status = "等待標題畫面…（按住 Shift 取消）";
    }

    public void Stop()
    {
        Services.Framework.Update -= OnUpdate;

        if (Running)
            Finish("模組已停用");
    }

    public void Dispose() => Stop();

    private void Enter(Step next)
    {
        step = next;
        stepStarted = DateTime.Now;
        readySince = DateTime.MaxValue;
    }

    private static void MarkSession() => Environment.SetEnvironmentVariable(SessionMarker, "1");

    private void Finish(string status)
    {
        step = Step.Idle;
        Status = status;
        MarkSession();
    }

    private void Abort(string reason)
    {
        Finish(reason);
        Services.Chat.PrintError($"[AetherKit] 自動登入停止：{reason}");
    }

    private void OnUpdate(IFramework framework)
    {
        RecordCharacters();

        if (!Running) return;

        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0)
        {
            Finish("已取消（按住 Shift）");
            Services.Chat.Print("[AetherKit] 已取消自動登入。");
            return;
        }

        if (Services.ClientState.IsLoggedIn)
        {
            Finish("已登入");
            return;
        }

        // 大廳的錯誤（連線失敗、維護中…）都是只有「確定」的對話框
        if (IsVisible("SelectOk"))
        {
            Abort("出現錯誤訊息");
            return;
        }

        if (DateTime.Now - stepStarted > TimeoutOf(step))
        {
            Abort($"{StepName(step)}等太久");
            return;
        }

        if (DateTime.Now < nextAction) return;

        switch (step)
        {
            case Step.Title:
                TickTitle();
                break;

            case Step.Character:
                TickCharacter();
                break;

            case Step.Confirm:
                TickConfirm();
                break;
        }
    }

    private static string StepName(Step step) => step switch
    {
        Step.Title => "標題畫面",
        Step.Character => "角色清單",
        Step.Confirm => "確認登入",
        _ => "進入遊戲"
    };

    /// <summary>畫面出現後先等設定的秒數再按。回傳 true 代表可以動作了。</summary>
    private bool Settled(bool ready)
    {
        if (!ready)
        {
            readySince = DateTime.MaxValue;
            return false;
        }

        if (readySince == DateTime.MaxValue)
            readySince = DateTime.Now;

        return DateTime.Now - readySince >= TimeSpan.FromSeconds(Math.Max(0, Config.DelaySeconds));
    }

    private void Acted() => nextAction = DateTime.Now + RetryInterval;

    // ── 標題畫面 ────────────────────────────────────────────

    private void TickTitle()
    {
        if (IsVisible("_CharaSelectListMenu"))
        {
            Enter(Step.Character);
            Status = "選擇角色…";
            return;
        }

        var title = GetAddon("_TitleMenu");
        if (!Settled(IsTitleReady(title))) return;

        // 「開始遊戲」按鈕的節點 ID 是 4
        if (ClickButton(title, title->GetComponentButtonById(4)))
            Acted();
    }

    /// <summary>
    /// 標題選單真的可以按了：開場動畫淡入結束（節點 3 完全不透明）、
    /// 按鈕列出現、而且沒有正在連線或選資料中心的視窗擋著。
    /// </summary>
    private static bool IsTitleReady(AtkUnitBase* title)
    {
        if (title == null || !title->IsVisible || !title->IsReady) return false;
        if (title->UldManager.NodeListCount <= 7 || !title->UldManager.NodeList[7]->IsVisible()) return false;

        var fade = title->GetNodeById(3);
        if (fade == null || fade->Color.A != 0xFF) return false;

        return !IsVisible("TitleDCWorldMap") && !IsVisible("TitleConnect");
    }

    // ── 角色清單 ────────────────────────────────────────────

    private void TickCharacter()
    {
        if (IsVisible("SelectYesno"))
        {
            Enter(Step.Confirm);
            Status = "確認登入…";
            return;
        }

        var list = GetAddon("_CharaSelectListMenu");
        if (!Settled(list != null && list->IsVisible && list->IsReady)) return;

        var lobby = AgentLobby.Instance();
        if (lobby == null || lobby->TemporaryLocked) return;

        var entries = lobby->LobbyData.CharaSelectEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i].Value;
            if (entry == null || entry->ContentId != Config.CharacterId) continue;

            if (entry->LoginFlags.HasFlag(CharaSelectCharacterEntryLoginFlags.Locked)
                || entry->LoginFlags.HasFlag(CharaSelectCharacterEntryLoginFlags.NameChangeRequired)
                || entry->LoginFlags.HasFlag(CharaSelectCharacterEntryLoginFlags.MissingExVersionForLogin))
            {
                Abort($"{entry->NameString} 目前不能登入");
                return;
            }

            ClickCharacter(list, i);
            Acted();
            return;
        }

        // 角色不在目前這個伺服器分頁：切到它的伺服器
        SelectWorld(TargetHomeWorld());
    }

    private string TargetHomeWorld()
    {
        foreach (var known in Config.Characters)
        {
            if (known.ContentId == Config.CharacterId)
                return known.HomeWorld;
        }

        return string.Empty;
    }

    private void SelectWorld(string world)
    {
        var server = GetAddon("_CharaSelectWorldServer");
        if (server == null || !server->IsVisible) return;

        var names = RaptureAtkModule.Instance()->AtkArrayDataHolder.StringArrays[1];

        for (var i = 0; i < 16; i++)
        {
            var name = Marshal.PtrToStringUTF8((nint)names->StringArray[i].Value)?.Trim();
            if (string.IsNullOrEmpty(name)) break;
            if (name != world) continue;

            FireCallback(server, 25, 0, i);
            Acted();
            return;
        }

        Abort($"角色清單裡找不到伺服器「{world}」");
    }

    /// <summary>
    /// 在角色清單上點一下第 index 個角色就是登入。照 ECommons 的寫法模擬滑鼠左鍵點擊：
    /// 事件編號是 5 + index，事件資料第 6 個位元組 0 代表左鍵。
    /// </summary>
    private static void ClickCharacter(AtkUnitBase* list, int index)
    {
        var param = (byte)(5 + index);
        var evt = NewEvent(list, param);
        var data = new AtkEventData();
        *((byte*)&data + 6) = 0;

        list->ReceiveEvent(AtkEventType.MouseClick, param, &evt, &data);
    }

    // ── 確認登入 ────────────────────────────────────────────

    private void TickConfirm()
    {
        var yesno = (AddonSelectYesno*)GetAddon("SelectYesno");

        if (yesno == null || !yesno->IsVisible)
        {
            // 對話框關了但還沒登入：已經按過「是」，在進入遊戲的路上
            if (DateTime.Now >= nextAction && nextAction != default)
            {
                Enter(Step.Entering);
                Status = "進入遊戲中…";
            }

            return;
        }

        if (!Settled(yesno->IsReady)) return;

        // 確認是「要用這個角色登入嗎」，不是別的對話框
        var name = TargetName();
        var prompt = yesno->PromptText != null ? yesno->PromptText->NodeText.ToString() : string.Empty;

        if (name.Length > 0 && !prompt.Contains(name))
        {
            Abort("出現的對話框不是登入確認");
            return;
        }

        if (ClickButton(&yesno->AtkUnitBase, yesno->YesButton))
            Acted();
    }

    private string TargetName()
    {
        foreach (var known in Config.Characters)
        {
            if (known.ContentId == Config.CharacterId)
                return known.Name;
        }

        return string.Empty;
    }

    // ── 記錄看過的角色 ──────────────────────────────────────

    /// <summary>
    /// 角色清單開著時，把上面的角色記下來給設定頁選。只看得到目前伺服器分頁的角色，
    /// 切過的分頁都會記到。每秒最多一次。
    /// </summary>
    private void RecordCharacters()
    {
        if (DateTime.Now < nextRecord) return;
        nextRecord = DateTime.Now + TimeSpan.FromSeconds(1);

        if (!IsVisible("_CharaSelectListMenu")) return;

        var lobby = AgentLobby.Instance();
        if (lobby == null) return;

        var worlds = Services.Data.GetExcelSheet<World>();
        var entries = lobby->LobbyData.CharaSelectEntries;
        var changed = false;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i].Value;
            if (entry == null || entry->ContentId == 0) continue;

            var known = Config.Characters.Find(c => c.ContentId == entry->ContentId);
            if (known == null)
            {
                known = new KnownCharacter { ContentId = entry->ContentId };
                Config.Characters.Add(known);
                changed = true;
            }

            var name = entry->NameString;
            var world = worlds.GetRowOrDefault(entry->HomeWorldId)?.Name.ExtractText() ?? string.Empty;

            if (known.Name != name || known.HomeWorldId != entry->HomeWorldId || known.HomeWorld != world)
            {
                known.Name = name;
                known.HomeWorldId = entry->HomeWorldId;
                known.HomeWorld = world;
                changed = true;
            }
        }

        if (changed)
            plugin.Save();
    }

    // ── 介面操作 ────────────────────────────────────────────

    private static AtkUnitBase* GetAddon(string name) =>
        RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);

    private static bool IsVisible(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    private static AtkEvent NewEvent(AtkUnitBase* addon, byte flags) => new()
    {
        Listener = (AtkEventListener*)addon,
        Target = &AtkStage.Instance()->AtkEventTarget,
        State = new AtkEventState { StateFlags = (AtkEventStateFlags)flags }
    };

    /// <summary>按下介面上的按鈕：把按鈕自己身上掛的事件送給它所在的介面，跟滑鼠點下去一樣。</summary>
    private static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || !button->IsEnabled || !button->AtkResNode->IsVisible()) return false;

        var node = button->AtkComponentBase.OwnerNode;
        if (node == null) return false;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;

        var data = new AtkEventData();
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
        return true;
    }

    private static void FireCallback(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];

        for (var i = 0; i < values.Length; i++)
            atkValues[i].SetInt(values[i]);

        addon->FireCallback((uint)values.Length, atkValues, true);
    }
}

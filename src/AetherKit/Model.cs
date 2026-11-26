using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;

namespace AetherKit;

// 這個檔案只放「會存進設定檔」的資料。
// 算出來的屬性一律標 [JsonIgnore]：Newtonsoft 會把有 getter 的屬性也寫進檔案，
// 不標的話設定檔會多出一堆雜訊，而且存檔時會去呼叫那些 getter。

/// <summary>工具列項目要顯示什麼。</summary>
public enum ItemStyle
{
    IconOnly,
    TextOnly,
    IconAndText
}

/// <summary>從遊戲拖進來時的原始身分，之後要做冷卻顯示、可用狀態都靠它。</summary>
public class GameSource
{
    /// <summary>HotbarSlotType 的值。</summary>
    public int Type { get; set; }

    public uint Id { get; set; }
}

/// <summary>一個可執行的東西：工具列按鈕的一個狀態，或面板裡手動放的一個項目。</summary>
public class ActionItem
{
    public string Label { get; set; } = string.Empty;

    /// <summary>名稱底下那一行小字。</summary>
    public string SubLabel { get; set; } = string.Empty;

    public uint IconId { get; set; }

    /// <summary>圖示改灰階、字變淡。開關的「關」通常勾這個，一眼看得出狀態。</summary>
    public bool Grayscale { get; set; }

    /// <summary>一行一個指令，// 開頭是註解。不是 / 開頭的行執行時會被略過，不會變成發言。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>從遊戲拖進來的才有。</summary>
    public GameSource? Source { get; set; }

    public ActionItem Clone()
    {
        var copy = (ActionItem)MemberwiseClone();
        copy.Source = Source == null ? null : new GameSource { Type = Source.Type, Id = Source.Id };
        return copy;
    }
}

/// <summary>快捷鍵。存 Windows 虛擬鍵碼。</summary>
public class Hotkey
{
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }

    /// <summary>觸發後把這個按鍵吃掉，不要傳給遊戲。</summary>
    public bool BlocksGame { get; set; }

    [JsonIgnore]
    public bool IsSet => Key != 0;

    /// <summary>同一組按鍵（都沒設定不算）。兩個項目設成同一組時要提醒。</summary>
    public bool SameAs(Hotkey other) =>
        IsSet && Key == other.Key && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift;

    public void Clear()
    {
        Key = 0;
        Ctrl = Alt = Shift = false;
    }
}

/// <summary>分區的內容從哪來。</summary>
public enum SectionSource
{
    /// <summary>自己拖進來、自己排。</summary>
    Manual,

    /// <summary>讀遊戲的裝備組，按職能分小組。</summary>
    Gearsets,

    /// <summary>讀遊戲的情感動作常用列表。</summary>
    EmoteFavorites
}

/// <summary>職能。裝備組按這個分小組，跟遊戲職業頁一樣把遠程分成物理與魔法。</summary>
public enum JobRole
{
    Tank,
    Healer,
    Melee,
    PhysicalRanged,
    MagicalRanged,
    Crafter,
    Gatherer
}

/// <summary>
/// 欄的共用操作：面板的分區、套裝列表的職能小組都是「幾欄，每欄由上到下」。
/// 兩邊拖曳的規則一樣，所以搬動的邏輯也只寫一份。
/// </summary>
public static class Columns
{
    /// <summary>
    /// 把 item 搬到第 column 欄的第 index 個位置。
    /// newColumn = 在第 column 個位置插一個新欄、只放它（拖到兩欄之間或最右邊）；
    /// column 超出欄數時也是開新欄。搬走後變空的欄會被拿掉。
    /// </summary>
    public static void Move<T>(List<List<T>> columns, T item, int column, int index, bool newColumn)
    {
        for (var c = 0; c < columns.Count; c++)
        {
            var at = columns[c].IndexOf(item);
            if (at < 0) continue;

            columns[c].RemoveAt(at);

            // 同一欄往下搬：移除後後面的位置往前一格
            if (!newColumn && c == column && at < index)
                index--;

            break;
        }

        // 空欄等插完再拿掉，前面算好的欄號才不會跑掉
        if (newColumn || column >= columns.Count)
        {
            columns.Insert(Math.Clamp(column, 0, columns.Count), new List<T> { item });
        }
        else
        {
            var target = columns[column];
            target.Insert(Math.Clamp(index, 0, target.Count), item);
        }

        columns.RemoveAll(c => c.Count == 0);
    }
}

/// <summary>
/// 有開關／循環狀態的按鈕：工具列按鈕、面板裡自訂區塊的項目都是。
/// 外觀永遠是目前狀態的名稱與圖示；按一次執行目前狀態的指令，然後換下一個。
/// </summary>
public abstract class StatefulItem
{
    /// <summary>游標提示靠它指到某個項目。拖曳排序搬的是同一個物件，ID 不會變。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>至少一個。由 ConfigStore 載入時補齊，這裡不在 getter 裡偷偷修改。</summary>
    public List<ActionItem> States { get; set; } = new();

    public int StateIndex { get; set; }

    /// <summary>
    /// 按下等於點一下。工具列按鈕與自訂項目是執行，展開面板的卡片是開關它的面板。
    /// </summary>
    public Hotkey Hotkey { get; set; } = new();

    /// <summary>可以按的條件（條件組）。Guid.Empty = 一直可以按。工具列按鈕、開關、面板的自訂項目都有。</summary>
    public Guid EnableConditionSetId { get; set; }

    /// <summary>不能按的時候藏起來，不是變灰。</summary>
    public bool HideWhenDisabled { get; set; }

    /// <summary>是不是開關：開著時按下執行關掉的指令（OffCommand），關著時執行打開的指令（目前狀態的 Command）。</summary>
    [JsonIgnore]
    public virtual bool IsToggle => false;

    /// <summary>開關：關掉的指令。打開的指令是目前狀態的 Command。</summary>
    public string OffCommand { get; set; } = string.Empty;

    /// <summary>開關沒有狀態來源時，自己記的開或關。</summary>
    public bool IsOn { get; set; }

    /// <summary>開關的狀態來源：這一條成立 = 開著。null = 自己記（IsOn）。</summary>
    public ConditionEntry? StateSource { get; set; }

    [JsonIgnore]
    public ActionItem Current => States[Math.Clamp(StateIndex, 0, States.Count - 1)];

    [JsonIgnore]
    public bool Cycles => States.Count > 1;

    public void Advance()
    {
        if (States.Count < 2) return;

        StateIndex = (StateIndex + 1) % States.Count;
    }
}

/// <summary>自訂區塊裡的一個項目。</summary>
public class PanelItem : StatefulItem
{
    /// <summary>是開關（不是一般項目）。</summary>
    public bool Toggle { get; set; }

    [JsonIgnore]
    public override bool IsToggle => Toggle;

    public static PanelItem From(ActionItem state) => new() { States = { state } };

    /// <summary>複製一份：設定全部一樣，ID 換新、快捷鍵清掉（免得兩個一起觸發）。</summary>
    public PanelItem Duplicate()
    {
        var copy = JsonConvert.DeserializeObject<PanelItem>(JsonConvert.SerializeObject(this))!;
        copy.Id = Guid.NewGuid();
        copy.Hotkey = new Hotkey();
        return copy;
    }
}

/// <summary>
/// 面板裡的一個分區：固定寬高的一塊，內容超出就自己捲動。
/// 分區放在面板的欄裡，由上到下，位置靠拖曳調整。
/// </summary>
public class PanelSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public SectionSource Source { get; set; } = SectionSource.Manual;

    public float Width { get; set; } = 200f;
    public float Height { get; set; } = 160f;

    /// <summary>
    /// 一個項目有多寬：每幀照整個面板裡最長的名稱算（PanelWindow.Layout），同一個面板都一樣寬，不存檔。欄數 = 分區寬度能塞幾個。
    /// </summary>
    [JsonIgnore]
    public float ItemWidth { get; set; } = 180f;

    /// <summary>一排幾欄。0 = 自動（分區寬度塞得下幾欄就幾欄）。套裝列表照職能小組分欄，不看這個。</summary>
    public int ColumnCount { get; set; }

    /// <summary>放得下名稱的最小格寬（整個面板一樣），分區最窄只能縮到這裡。每幀算，不存檔。</summary>
    [JsonIgnore]
    public float MinItemWidth { get; set; } = 80f;

    /// <summary>Source == Manual 時的內容。</summary>
    public List<PanelItem> Items { get; set; } = new();

    /// <summary>
    /// 套裝列表的職能小組怎麼分欄：每個內層清單是一欄，由上到下。像遊戲的職業頁那樣固定擺，不自動流動。
    /// 預設是空的而不是先填好：Newtonsoft 載入時會沿用預設的清單再往後加，先填好的話每次載入都會變兩倍。
    /// </summary>
    public List<List<JobRole>> RoleColumns { get; set; } = new();

    /// <summary>沒調過時的擺法：左坦克近戰、中治療遠程、右生產採集。</summary>
    private static readonly JobRole[][] DefaultRoleColumns =
    {
        new[] { JobRole.Tank, JobRole.Melee },
        new[] { JobRole.Healer, JobRole.PhysicalRanged, JobRole.MagicalRanged },
        new[] { JobRole.Crafter, JobRole.Gatherer }
    };

    /// <summary>實際的分欄：照使用者排的，重複的去掉、空欄去掉，漏掉的職能補在最後一欄。</summary>
    public List<List<JobRole>> ResolvedRoleColumns()
    {
        var source = RoleColumns.Count > 0
            ? RoleColumns
            : DefaultRoleColumns.Select(column => column.ToList()).ToList();

        var seen = new HashSet<JobRole>();
        var result = new List<List<JobRole>>();

        foreach (var column in source)
        {
            var roles = column.Where(seen.Add).ToList();
            if (roles.Count > 0)
                result.Add(roles);
        }

        var missing = Enum.GetValues<JobRole>().Where(seen.Add).ToList();
        if (missing.Count > 0)
        {
            if (result.Count == 0)
                result.Add(new List<JobRole>());

            result[^1].AddRange(missing);
        }

        return result;
    }

    /// <summary>把一組搬到第 column 欄的第 index 個位置。column 等於欄數 = 開一個新欄。</summary>
    public void MoveRole(JobRole role, int column, int index)
    {
        var columns = ResolvedRoleColumns();
        Columns.Move(columns, role, column, index, newColumn: false);
        RoleColumns = columns;
    }

    [JsonIgnore]
    public bool IsAuto => Source != SectionSource.Manual;

    /// <summary>套裝列表自己按職能分小組，小組標題就取代了分區標題。</summary>
    [JsonIgnore]
    public bool HasGroups => Source == SectionSource.Gearsets;
}

/// <summary>
/// 由工具列項目展開的面板。有自己的寬高，內容超出就捲動（上下、左右都會）。
/// 分區放在欄裡：每個內層清單是一欄，由上到下。欄的位置固定，不隨面板寬度換行。
/// </summary>
public class PanelConfig
{
    public float Width { get; set; } = 420f;
    public float Height { get; set; } = 280f;

    /// <summary>預設空的，理由同 RoleColumns。</summary>
    public List<List<PanelSection>> Columns { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<PanelSection> Sections => Columns.SelectMany(column => column);

    public void RemoveSection(PanelSection section)
    {
        foreach (var column in Columns)
            column.Remove(section);

        Columns.RemoveAll(c => c.Count == 0);
    }

    /// <summary>搬到指定位置；還不在面板裡的分區也可以，等於新增在那裡。</summary>
    public void MoveSection(PanelSection section, int column, int index, bool newColumn) =>
        AetherKit.Columns.Move(Columns, section, column, index, newColumn);
}

/// <summary>工具列項目按下去做什麼。</summary>
public enum ItemKind
{
    /// <summary>滑鼠移上去展開面板。</summary>
    Panel,

    /// <summary>直接執行。多個狀態時每按一次換一個。</summary>
    Button,

    /// <summary>開關：開著時按下執行「關掉的指令」，關著時執行「打開的指令」。狀態可以跟著遊戲或插件。</summary>
    Toggle
}

/// <summary>
/// 工具列的一個項目。面板只有一個狀態，按鈕可以有很多個（開關、循環）。
/// 面板直接存在項目裡面，不另外用 ID 對照 — 刪項目就連面板一起刪，不會留下孤兒。
/// </summary>
public class TopBarItem : StatefulItem
{
    public ItemKind Kind { get; set; } = ItemKind.Panel;
    public ItemStyle Style { get; set; } = ItemStyle.IconAndText;

    /// <summary>Kind == Panel 時的內容。</summary>
    public PanelConfig? Panel { get; set; }

    [JsonIgnore]
    public override bool IsToggle => Kind == ItemKind.Toggle;

    /// <summary>
    /// 複製一份：設定全部一樣，但 ID 都換新的（面板、分區、項目也是），快捷鍵清掉免得兩個一起觸發。
    /// </summary>
    public TopBarItem Duplicate()
    {
        var copy = JsonConvert.DeserializeObject<TopBarItem>(JsonConvert.SerializeObject(this))!;
        copy.Id = Guid.NewGuid();
        copy.Hotkey = new Hotkey();

        if (copy.Panel != null)
        {
            foreach (var section in copy.Panel.Sections)
            {
                section.Id = Guid.NewGuid();
                foreach (var item in section.Items)
                {
                    item.Id = Guid.NewGuid();
                    item.Hotkey = new Hotkey();
                }
            }
        }

        return copy;
    }

    public static TopBarItem NewPanel(string label) => new()
    {
        Kind = ItemKind.Panel,
        States = { new ActionItem { Label = label } },
        Panel = new PanelConfig()
    };

    public static TopBarItem NewButton(string label) => new()
    {
        Kind = ItemKind.Button,
        States = { new ActionItem { Label = label } }
    };
}

/// <summary>工具列。自由拖曳位置，不吸附螢幕邊緣。</summary>
public class TopBarConfig
{
    public Vector2 Position { get; set; } = new(400, 40);
    public List<TopBarItem> Items { get; set; } = new();

    /// <summary>顯示條件組。Guid.Empty = 永遠顯示。</summary>
    public Guid ConditionSetId { get; set; }
}

/// <summary>在角色選擇畫面看過的一個角色。</summary>
public class KnownCharacter
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint HomeWorldId { get; set; }
    public string HomeWorld { get; set; } = string.Empty;
}

/// <summary>自動登入：開遊戲後自動按「開始遊戲」、選角色、確認登入。</summary>
public class AutoLoginConfig
{
    /// <summary>要登入的角色。0 = 還沒指定，什麼都不做。</summary>
    public ulong CharacterId { get; set; }

    /// <summary>到過角色選擇畫面就會記下來的角色，給設定頁選。預設空的，理由同 RoleColumns。</summary>
    public List<KnownCharacter> Characters { get; set; } = new();

    /// <summary>每個畫面出現後先等多久再按，介面還沒準備好就按會沒反應。</summary>
    public float DelaySeconds { get; set; } = 1f;
}

/// <summary>持有狀況：在物品資訊框裡列出這個物品放在哪裡、有幾個。</summary>
public class ItemOwnershipConfig
{
    /// <summary>雇員、鞍囊、投影台的資料超過幾天沒更新就用灰色顯示。</summary>
    public int StaleDays { get; set; } = 7;

    /// <summary>在快取來源旁標示資料是多久前看到的。</summary>
    public bool ShowAge { get; set; } = true;

    /// <summary>身上以外的地方也有時，在右上角的持有數量後面補上全部的數量。</summary>
    public bool ShowTotal { get; set; } = true;

    /// <summary>完全沒有持有時也顯示一行「持有：沒有」。</summary>
    public bool ShowWhenNone { get; set; }

    /// <summary>
    /// 雇員物品欄、鞍囊、投影台最後一次被打開的時間（UTC），鍵見 Ownership.SeenKey。
    /// 遊戲的快取不記時間，只好自己記。這是紀錄不是偏好，不跟著匯出。
    /// </summary>
    public Dictionary<string, DateTime> LastSeen { get; set; } = new();

    /// <summary>
    /// 雇員 ID → 名字。遊戲的雇員資料要去過傳喚鈴才有，在那之前只讀得到 ID，
    /// 所以讀得到名字時記下來。跟 LastSeen 一樣是紀錄，不跟著匯出。
    /// </summary>
    public Dictionary<ulong, string> RetainerNames { get; set; } = new();
}

/// <summary>FATE 類型，從遊戲資料的地圖圖示分（一般圖示與帝國主題的圖示各一組）。</summary>
public enum FateKind
{
    Slay,
    Boss,
    Collect,
    Defend,
    Escort,
    Special,
    Other
}

/// <summary>自動Fate 的輸出交給哪個自動循環插件。</summary>
public enum RotationPlugin
{
    Rsr,
    WrathCombo
}

/// <summary>自動Fate：在目前區域循環打臨危受命。</summary>
public class AutoFateConfig
{
    /// <summary>雙色寶石的物品 ID。</summary>
    public const uint BicolorGemstone = 26807;

    private const string RsrStart = "/rotation manual";
    private const string RsrStop = "/rotation off";

    // Wrath 的「/wrath auto target …」會順便把自動循環關掉，所以要先設目標模式、最後才開
    private const string WrathStart = "/wrath auto target damage Manual\n/wrath auto target healer Manual\n/wrath auto on";
    private const string WrathStop = "/wrath auto off";

    /// <summary>輸出用哪個插件。目標由自動Fate 選，插件只打目前選中的目標。</summary>
    public RotationPlugin Rotation { get; set; } = RotationPlugin.Rsr;

    /// <summary>開始打時送的指令（多行，一行一個）。</summary>
    [JsonIgnore]
    public string StartCommands => Rotation == RotationPlugin.WrathCombo ? WrathStart : RsrStart;

    /// <summary>停止打時送的指令。</summary>
    [JsonIgnore]
    public string StopCommands => Rotation == RotationPlugin.WrathCombo ? WrathStop : RsrStop;

    [JsonIgnore]
    public string RotationName => Rotation == RotationPlugin.WrathCombo ? "Wrath Combo" : "RSR";

    /// <summary>近戰職業（坦克、近戰）離目標邊緣多近才停下來打。</summary>
    public float MeleeRange { get; set; } = 2.5f;

    /// <summary>遠程與治療離目標邊緣多近才停下來打。技能射程是 25，留一點餘裕。</summary>
    public float RangedRange { get; set; } = 20f;

    /// <summary>雙色寶石到這個數量就停（上限 1500）。</summary>
    public bool StopOnGemstones { get; set; } = true;

    public int GemstoneLimit { get; set; } = 1400;

    /// <summary>開始後過了這麼多分鐘就停（例如掛一個晚上）。</summary>
    public bool StopOnTimer { get; set; }

    public int StopAfterMinutes { get; set; } = 480;

    /// <summary>自動停止（寶石到量、時間到、卡住、出錯）後傳送（傳到哪看 StopAetheryte）。手動停止不傳。</summary>
    public bool TeleportOnStop { get; set; } = true;

    /// <summary>自動停止後傳送到哪（傳送清單的 AetheryteId）；0 = 所在地圖最近的以太之光。</summary>
    public uint StopAetheryte { get; set; }

    /// <summary>同上的 SubIndex：住宅用來分辨是哪一間，大水晶是 0。</summary>
    public byte StopAetheryteSub { get; set; }

    /// <summary>達到停止條件（寶石到量、時間到）之後：登出或關閉遊戲（回水晶完才做）。手動停止、出錯、卡住不算。</summary>
    public Core.FinishAction AfterFinish { get; set; }

    /// <summary>多區域：目前區域沒有能打的 FATE 時，輪流傳送到下一個區域。</summary>
    public bool MultiZone { get; set; }

    /// <summary>多區域輪替的區域（TerritoryType），依序輪流。</summary>
    public List<uint> Zones { get; set; } = new();

    /// <summary>沒有能打的 FATE 持續這麼多秒才換區域，剛打完時常常下一個馬上就刷。</summary>
    public int ZoneWaitSeconds { get; set; } = 30;


    /// <summary>離目的地超過這個距離就先上坐騎。</summary>
    public bool UseMount { get; set; } = true;

    public float MountDistance { get; set; } = 30f;

    /// <summary>同一張地圖裡有離 FATE 比較近的以太之光，而且能省下至少 ShortcutSaving 米，就先傳送過去再走。</summary>
    public bool ShortcutTeleport { get; set; } = true;

    public float ShortcutSaving { get; set; } = 300f;

    /// <summary>有加成（★）的 FATE 優先。</summary>
    public bool PreferBonus { get; set; } = true;

    /// <summary>剩不到這麼多秒的 FATE 不去。</summary>
    public int MinSecondsLeft { get; set; } = 90;

    /// <summary>進度超過這個百分比的 FATE 不去，到了也拿不到好評價。</summary>
    public int MaxProgress { get; set; } = 80;

    /// <summary>
    /// 要打的 FATE 類型。預設只打打怪、頭目、防衛這三種靠戰鬥就能完成的。
    /// 有預設內容的清單要標 Replace：Dalamud 讀設定檔時預設是「加進既有清單」，
    /// 不標的話每次載入都會多一份預設值，取消勾選的項目也會自己跑回來。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<FateKind> Kinds { get; set; } = new() { FateKind.Slay, FateKind.Boss, FateKind.Defend };

    /// <summary>繳交型 FATE：身上道具到這個數量就去交（7 個以上算全額貢獻）。FATE 快結束時不管幾個都先交。</summary>
    public int TurnInAt { get; set; } = 7;

    /// <summary>要找 NPC 對話才會開始的 FATE：走過去對話、按確認讓它開始。</summary>
    public bool StartNpcFates { get; set; } = true;

    /// <summary>坦克職業進 FATE 時自動開防禦姿態。</summary>
    public bool TankStance { get; set; } = true;

    /// <summary>執行期間自動拒絕別人發起的交易。</summary>
    public bool RefuseTrade { get; set; } = true;

    /// <summary>陸行鳥夥伴剩不到這麼多秒（或沒叫出來）就餵基薩爾野菜。</summary>
    public bool SummonChocobo { get; set; } = true;

    public int ChocoboMinSeconds { get; set; } = 300;

    /// <summary>叫出陸行鳥後設的戰術（遊戲 BuddyAction 表：4 自由、5 防護、6 進攻、7 治療）；0 = 不改。</summary>
    public uint ChocoboStance { get; set; } = 7;

    /// <summary>
    /// 兩場 FATE 之間借用核心的修理能力自己修裝備。門檻在核心能力頁設定（所有功能共用）。
    /// </summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>兩場 FATE 之間，有精煉度滿的裝備就開精製魔晶石視窗全部精製（跟「一鍵精製魔晶石」同一套流程）。</summary>
    public bool AutoMaterialize { get; set; }

    /// <summary>被野外菁英怪（名牌有怪物等級標記、不屬於 FATE）纏上時不打，跑遠甩開。</summary>
    public bool FleeElites { get; set; } = true;

    /// <summary>
    /// 一出現就優先打的怪（BNpcName ID）。預設是迷失少女和迷失者：它們不會主動攻擊、
    /// 也不一定算在 FATE 的怪裡，不特別指定的話自動循環常常會略過。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<uint> PriorityNames { get; set; } = new() { 6737, 6738 };

    /// <summary>優先目標在這個距離內就過去打。</summary>
    public float PriorityRange { get; set; } = 60f;

    /// <summary>不去的 FATE（Fate ID）。</summary>
    public List<uint> Blacklist { get; set; } = new();

    /// <summary>在哪場 FATE 死過幾次（Fate ID → 次數）。紀錄用，不影響選 FATE。</summary>
    public Dictionary<uint, int> FateDeaths { get; set; } = new();
}

/// <summary>插件訊息：插件印到聊天欄的訊息不顯示，改收進訊息紀錄視窗。</summary>
public class PluginLogConfig
{
    /// <summary>插件印了錯誤訊息時，跳 Dalamud 的通知。</summary>
    public bool NotifyErrors { get; set; } = true;

    /// <summary>這些來源（訊息開頭的 [插件名稱]）照樣顯示在聊天欄。預設空的，全部都收起來。</summary>
    public List<string> ShowInChat { get; set; } = new();
}

/// <summary>游標提示圖示外面要不要有動畫。</summary>
public enum CursorAnimation
{
    None,

    /// <summary>外圈兩段頭粗尾細的弧線繞著轉，表示「正在運作」。</summary>
    Ring
}

/// <summary>
/// 一個游標提示：某個有狀態的項目（工具列按鈕或面板裡的自訂項目）停在某個狀態時，
/// 在滑鼠旁顯示圖示。它自己沒有開關，只讀項目的狀態 — 項目是唯一的依據，兩邊不會對不上。
/// 圖示另外挑，項目原本的圖示不一定適合縮小放在游標旁。
/// </summary>
public class CursorIndicator
{
    /// <summary>
    /// 跟著哪個項目。設定檔裡的欄位名稱沿用最早只能選工具列按鈕時的 ButtonId，
    /// 已經設好的游標提示才不會因為改名而失效。
    /// </summary>
    [JsonProperty("ButtonId")]
    public Guid ItemId { get; set; }

    /// <summary>項目停在這個狀態時顯示（從 0 算）。</summary>
    public int StateIndex { get; set; } = 1;

    public uint IconId { get; set; }

    public CursorAnimation Animation { get; set; } = CursorAnimation.Ring;
}

/// <summary>游標提示。游標旁不適合放太多東西，所以最多兩個；外觀兩個共用。</summary>
public class CursorConfig
{
    public const int MaxIndicators = 2;

    /// <summary>預設空的，理由同 RoleColumns。</summary>
    public List<CursorIndicator> Indicators { get; set; } = new();

    /// <summary>圖示的直徑。外圈畫在它外面。</summary>
    public float Size { get; set; } = 28f;

    /// <summary>離游標尖端多遠，預設在右下方。</summary>
    public Vector2 Offset { get; set; } = new(20f, 20f);

    public float Opacity { get; set; } = 0.9f;

    /// <summary>外圈每秒轉幾圈。</summary>
    public float Speed { get; set; } = 0.8f;

    /// <summary>按住滑鼠在遊戲畫面上（轉視角）時先藏起來，那時候遊戲游標也看不到。</summary>
    public bool HideWhileDragging { get; set; } = true;
}

/// <summary>
/// 具名色票與尺寸。所有繪製都從這裡拿值，不在各處寫死，
/// 改一個值整個插件跟著變。
/// </summary>
public class Theme
{
    // ── 編輯 ──
    /// <summary>拖曳調整面板、分區大小時一格多少像素，寬高都對齊到它的倍數。1 = 不對齊。</summary>
    public float SizeStep { get; set; } = 10f;

    // ── 底 ──
    public Vector4 BarBackground { get; set; } = new(0.07f, 0.08f, 0.10f, 0.60f);
    public Vector4 PanelBackground { get; set; } = new(0.07f, 0.08f, 0.10f, 0.92f);
    public Vector4 Border { get; set; } = new(1f, 1f, 1f, 0.12f);

    // ── 項目 ──
    public Vector4 RowBackground { get; set; } = new(1f, 1f, 1f, 0.04f);
    public Vector4 RowHover { get; set; } = new(1f, 1f, 1f, 0.12f);
    public Vector4 RowActive { get; set; } = new(1f, 1f, 1f, 0.20f);

    // ── 文字 ──
    public Vector4 TextPrimary { get; set; } = new(0.92f, 0.93f, 0.95f, 1f);
    public Vector4 TextSecondary { get; set; } = new(0.92f, 0.93f, 0.95f, 0.55f);

    /// <summary>提示字，比副文字再淡一點。</summary>
    public Vector4 TextMuted { get; set; } = new(0.92f, 0.93f, 0.95f, 0.40f);

    // ── 狀態 ──
    public Vector4 Accent { get; set; } = new(0.38f, 0.65f, 1f, 1f);
    public Vector4 Danger { get; set; } = new(1f, 0.48f, 0.45f, 1f);
    public Vector4 Positive { get; set; } = new(0.45f, 0.85f, 0.50f, 1f);

    // ── 編輯模式 ──
    /// <summary>編輯模式下分區的外框。</summary>
    public Vector4 Outline { get; set; } = new(1f, 1f, 1f, 0.16f);

    /// <summary>滑過、拖曳中、可放置時的外框與把手。</summary>
    public Vector4 OutlineActive { get; set; } = new(1f, 1f, 1f, 0.55f);

    // ── 職能底色：坦克藍、治療綠、輸出紅、生產紫、採集金 ──
    public Vector4 RoleTank { get; set; } = new(0.25f, 0.42f, 0.78f, 0.35f);
    public Vector4 RoleHealer { get; set; } = new(0.24f, 0.56f, 0.33f, 0.35f);
    public Vector4 RoleDps { get; set; } = new(0.62f, 0.24f, 0.26f, 0.35f);
    public Vector4 RoleCrafter { get; set; } = new(0.45f, 0.32f, 0.63f, 0.35f);
    public Vector4 RoleGatherer { get; set; } = new(0.60f, 0.50f, 0.20f, 0.35f);

    // ── 工具列：一條半透明的底，上面貼著單行的小卡片 ──
    public float BarHeight { get; set; } = 28f;
    public float BarIconSize { get; set; } = 20f;

    /// <summary>卡片比整條的底更深一點，疊在上面看得出一顆一顆的按鈕。</summary>
    public Vector4 ChipBackground { get; set; } = new(0.07f, 0.08f, 0.10f, 0.85f);
    public Vector4 ChipBorder { get; set; } = new(1f, 1f, 1f, 0.14f);

    /// <summary>面板正展開中的那張卡片。</summary>
    public Vector4 ChipOpen { get; set; } = new(0.38f, 0.65f, 1f, 0.22f);

    // ── 尺寸 ──
    public float Rounding { get; set; } = 6f;
    public float BorderWidth { get; set; } = 1f;
    public float RowHeight { get; set; } = 30f;
    public float IconSize { get; set; } = 22f;
    public float Padding { get; set; } = 8f;

    /// <summary>基本間隔。其他間距都是它的倍數，見下面的衍生值。</summary>
    public float Gap { get; set; } = 4f;

    // ── 衍生值：由上面算出來，不存檔，也不給使用者調 ──

    /// <summary>分區與分區之間、小組與小組之間。</summary>
    [JsonIgnore]
    public float BlockGap => Gap * 2;

    /// <summary>工具列按鈕與面板之間的透明橋接。</summary>
    [JsonIgnore]
    public float Bridge => Gap * 2;

    /// <summary>編輯模式外框畫在內容外面多遠。</summary>
    [JsonIgnore]
    public float OutlineInset => Gap;

    public Vector4 RoleColor(JobRole role) => role switch
    {
        JobRole.Tank => RoleTank,
        JobRole.Healer => RoleHealer,
        JobRole.Crafter => RoleCrafter,
        JobRole.Gatherer => RoleGatherer,
        _ => RoleDps
    };
}

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
    public static PanelItem From(ActionItem state) => new() { States = { state } };
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

    /// <summary>一個項目有多寬。欄數 = 分區寬度能塞幾個。</summary>
    public float ItemWidth { get; set; } = 180f;

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
    Button
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

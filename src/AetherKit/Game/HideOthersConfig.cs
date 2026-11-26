namespace AetherKit;

/// <summary>隱藏其他人（HideOthers）的設定。</summary>
public class HideOthersConfig
{
    public HideRule Players { get; set; } = new();

    /// <summary>召喚物：召喚師、學者、機工士等職業叫出來的。</summary>
    public HideRule Pets { get; set; } = new();

    public HideRule Chocobos { get; set; } = new();

    /// <summary>小寵物（跟在身邊的那種）。</summary>
    public HideRule Minions { get; set; } = new();
}

/// <summary>一種東西要不要藏、誰的照樣顯示。召喚物、陸行鳥、小寵物看的是主人。</summary>
public class HideRule
{
    public bool Hide { get; set; } = true;

    public bool ShowParty { get; set; }

    public bool ShowFriends { get; set; }

    public bool ShowCompany { get; set; }
}

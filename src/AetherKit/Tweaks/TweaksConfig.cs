using System.Collections.Generic;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>小功能：哪些開著，以及各小功能自己的選項。</summary>
public class TweaksConfig
{
    /// <summary>開著的小功能（存 Id）。預設全部關，使用者自己挑要開哪些。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Enabled { get; set; } = new();

    /// <summary>一鍵分解：略過 HQ 道具。</summary>
    public bool DesynthSkipHq { get; set; } = true;

    /// <summary>肖像更新確認：自動更新後在聊天欄提示。</summary>
    public bool PortraitUpdateNotify { get; set; } = true;

    /// <summary>招募板自動刷新：每幾秒刷新一次。</summary>
    public int PartyFinderRefreshSeconds { get; set; } = 10;

    /// <summary>坐騎上自動下坐騎：飛行中也降落（預設只在地面上才下）。</summary>
    public bool AutoDismountInFlight { get; set; }

    /// <summary>周圍玩家：只列出這個距離（米）以內的玩家。</summary>
    public int NearbyPlayersRange { get; set; } = 50;

    /// <summary>周圍玩家：有人選你為目標時畫線標出是誰。</summary>
    public bool NearbyPlayersLines { get; set; } = true;

    /// <summary>周圍玩家：有人選你為目標時在聊天欄提示。</summary>
    public bool NearbyPlayersChat { get; set; } = true;

    /// <summary>收進收藏櫃：略過套裝（套裝列表）裡用到的裝備，免得套裝缺件。</summary>
    public bool CabinetSkipGearset { get; set; } = true;

    /// <summary>移除重複幻化：染色不同的不算重複，留著。</summary>
    public bool DresserDuplicateKeepDyed { get; set; } = true;

    /// <summary>取出套裝部件：只取出投影台裡已經湊齊一整套的。</summary>
    public bool OutfitCompleteOnly { get; set; } = true;

    /// <summary>取出套裝部件：有染色的部件不取（組成套裝會洗掉染色）。</summary>
    public bool OutfitSkipDyed { get; set; } = true;

    /// <summary>自動鎖定快捷列：進入戰鬥時鎖定。</summary>
    public bool HotbarLockCombatStart { get; set; } = true;

    /// <summary>自動鎖定快捷列：戰鬥結束時解鎖。</summary>
    public bool HotbarUnlockCombatEnd { get; set; }

    /// <summary>自動鎖定快捷列：換地圖時鎖定。</summary>
    public bool HotbarLockZoneChange { get; set; } = true;

    /// <summary>自動鎖定快捷列：十字快捷列也一起。</summary>
    public bool HotbarLockCross { get; set; } = true;

    /// <summary>鏡頭最大距離：滾輪能拉到多遠（遊戲預設 20）。</summary>
    public float CameraMaxDistance { get; set; } = 40f;

    /// <summary>確認框預設選「是」：關掉的分類（存分類 Id）。預設全部開。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> AlwaysYesOff { get; set; } = new();

    /// <summary>確認框預設選「是」：視窗上有勾選框時先把游標放在勾選框上。</summary>
    public bool AlwaysYesSelectCheckBox { get; set; } = true;

    /// <summary>確認框預設選「是」：勾選框已經勾了就直接放在「是」上。</summary>
    public bool AlwaysYesIgnoreTicked { get; set; } = true;

    /// <summary>確認框預設選「是」：一般確認框的例外文字。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> AlwaysYesExceptions { get; set; } = new();

    /// <summary>Shift＋右鍵：開著商店、雇員出售清單時直接賣。</summary>
    public bool QuickSell { get; set; } = true;

    /// <summary>Shift＋右鍵：放到另一個開著的背包。</summary>
    public bool QuickTransfer { get; set; } = true;
}

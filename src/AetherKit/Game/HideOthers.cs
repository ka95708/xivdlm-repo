using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AetherKit.Game;

/// <summary>
/// 隱藏其他人（照 Visibility 插件的做法）：每幀看身邊的玩家、召喚物、陸行鳥、小寵物，
/// 要藏的就在物件的 RenderFlags 加上「不畫模型、不畫名牌」兩個位元，不藏了再拿掉。
/// 只拿掉自己加的：記住藏過哪些物件，物件換掉（離開、換人）就不碰，免得把遊戲自己藏的東西叫出來。
/// 不分地圖，啟用就一直藏；停用、卸載時全部還原。
/// </summary>
public sealed unsafe class HideOthers : IDisposable
{
    /// <summary>VisibilityFlags.Model | VisibilityFlags.Nameplate（2 | 2048）。</summary>
    private const int HiddenBits = 2 | 2048;

    public enum Kind { Player, Pet, Chocobo, Minion }

    private readonly Plugin plugin;

    /// <summary>藏起來的物件：位址 → 當時的種類（同一個位址換成別的物件就不算）。</summary>
    private readonly Dictionary<nint, ObjectKind> hidden = new();

    /// <summary>這一幀藏了的（每幀重用）。</summary>
    private readonly HashSet<nint> seen = new();

    private readonly int[] counts = new int[4];
    private bool running;

    public HideOthers(Plugin plugin) => this.plugin = plugin;

    private HideOthersConfig Config => plugin.Config.HideOthers;

    /// <summary>目前藏了幾個（頁首顯示用）。</summary>
    public int Count(Kind kind) => counts[(int)kind];

    public void Start()
    {
        if (running) return;

        running = true;
        Services.Framework.Update += OnUpdate;
    }

    public void Stop()
    {
        if (!running) return;

        running = false;
        Services.Framework.Update -= OnUpdate;
        ShowAll();
    }

    public void Dispose() => Stop();

    private void OnUpdate(IFramework framework)
    {
        Array.Clear(counts);

        var player = Services.Objects.LocalPlayer;
        if (player == null)
        {
            ShowAll();
            return;
        }

        var me = (Character*)player.Address;
        seen.Clear();

        foreach (var obj in Services.Objects)
        {
            if (obj.Address == player.Address || obj.Address == 0) continue;
            if (Classify(obj.ObjectKind, obj.SubKind) is not { } kind) continue;

            var hide = ShouldHide((Character*)obj.Address, kind, me);
            if (hide)
            {
                Hide(obj.Address, obj.ObjectKind);
                seen.Add(obj.Address);
                counts[(int)kind]++;
            }
            else
            {
                Show(obj.Address, obj.ObjectKind);
            }
        }

        // 藏過、但這一幀不在物件表裡的（離開了）：位址可能已經給別的東西用，不碰，只忘掉
        if (hidden.Count > seen.Count)
        {
            var gone = new List<nint>();
            foreach (var address in hidden.Keys)
                if (!seen.Contains(address)) gone.Add(address);
            foreach (var address in gone) hidden.Remove(address);
        }
    }

    /// <summary>玩家、召喚物、陸行鳥、小寵物；其他（怪物、NPC…）回 null。</summary>
    private static Kind? Classify(ObjectKind objectKind, byte subKind) => objectKind switch
    {
        ObjectKind.Player => Kind.Player,
        ObjectKind.Companion => Kind.Minion,
        ObjectKind.BattleNpc when subKind == (byte)BattleNpcSubKind.Pet => Kind.Pet,
        ObjectKind.BattleNpc when subKind == (byte)BattleNpcSubKind.Chocobo => Kind.Chocobo,
        _ => null,
    };

    private bool ShouldHide(Character* character, Kind kind, Character* me)
    {
        var rule = kind switch
        {
            Kind.Player => Config.Players,
            Kind.Pet => Config.Pets,
            Kind.Chocobo => Config.Chocobos,
            _ => Config.Minions,
        };

        if (!rule.Hide) return false;

        // 召喚物、陸行鳥看 OwnerId，小寵物看 CompanionOwnerId；主人是自己就不藏
        var ownerId = kind switch
        {
            Kind.Player => character->GameObject.EntityId,
            Kind.Minion => character->CompanionOwnerId,
            _ => character->GameObject.OwnerId,
        };

        if (ownerId == me->GameObject.EntityId) return false;

        var owner = kind == Kind.Player ? character : FindCharacter(ownerId);
        if (owner == null) return true;

        if (rule.ShowParty && (owner->IsPartyMember || owner->IsAllianceMember)) return false;
        if (rule.ShowFriends && owner->IsFriend) return false;
        if (rule.ShowCompany && SameCompany(owner, me)) return false;

        return true;
    }

    private static Character* FindCharacter(uint entityId)
    {
        if (entityId is 0 or 0xE0000000) return null;

        var obj = Services.Objects.SearchById(entityId);
        return obj is { ObjectKind: ObjectKind.Player } ? (Character*)obj.Address : null;
    }

    private static bool SameCompany(Character* owner, Character* me)
    {
        var mine = me->FreeCompanyTagString;
        return mine.Length > 0 && owner->FreeCompanyTagString == mine;
    }

    private void Hide(nint address, ObjectKind kind)
    {
        var obj = (CsGameObject*)address;
        obj->RenderFlags |= HiddenBits; // [改記憶體] 不畫模型、名牌
        hidden[address] = kind;
    }

    private void Show(nint address, ObjectKind kind)
    {
        if (!hidden.TryGetValue(address, out var was)) return;

        hidden.Remove(address);
        if (was != kind) return;

        var obj = (CsGameObject*)address;
        obj->RenderFlags &= ~HiddenBits; // [改記憶體] 還原自己加的位元
    }

    /// <summary>把藏過、而且還在物件表裡同一個位址的東西叫回來。</summary>
    private void ShowAll()
    {
        if (hidden.Count == 0) return;

        foreach (var obj in Services.Objects)
            if (hidden.ContainsKey(obj.Address))
                Show(obj.Address, obj.ObjectKind);

        hidden.Clear();
    }
}

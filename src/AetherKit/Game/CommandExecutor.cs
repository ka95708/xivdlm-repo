using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace AetherKit.Game;

/// <summary>
/// 執行佇列。遊戲一幀只處理一個指令，所以排隊逐幀送出，也保證在遊戲的主迴圈裡執行。
///
/// 能直接呼叫遊戲函式的就不繞指令：換套裝、做情感動作都有現成的函式，
/// 不用擔心指令格式或在地化寫法對不上。其餘的插件指令交給 Dalamud、再其餘丟給遊戲的 shell。
/// </summary>
public unsafe partial class CommandExecutor : IDisposable
{
    private readonly Queue<(string Description, Action Run)> queue = new();

    public CommandExecutor()
    {
        Services.Framework.Update += OnUpdate;
    }

    /// <summary>
    /// 執行一個項目。direct = 用 Source 直接呼叫遊戲函式（自動分區的套裝與情感動作）；
    /// 手動項目的指令可能被使用者改過（例如情感動作後面加一句話），所以照指令跑。
    /// </summary>
    public void Run(ActionItem item, bool direct)
    {
        if (direct && item.Source is { } source && TryDirect(source))
            return;

        Enqueue(item.Command);
    }

    public void Enqueue(string command)
    {
        foreach (var line in Lines(command))
        {
            if (!IsCommand(line))
            {
                // 純文字丟給遊戲會變成發言，送到哪個頻道看當下選的是哪個 — 可能是小隊、部隊甚至呼喊。
                // 打錯字不該變成公開訊息，所以一律擋下來；要說話就寫 /s、/p 這類指令
                Services.Chat.PrintError($"[AetherKit] 略過不是 / 開頭的指令：{line}");
                continue;
            }

            queue.Enqueue((line, () => Execute(line)));
        }
    }

    /// <summary>有內容的行：去掉空白行與 // 開頭的註解。</summary>
    public static IEnumerable<string> Lines(string command)
    {
        foreach (var line in command.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("//"))
                yield return trimmed;
        }
    }

    public static bool IsCommand(string line) => line.StartsWith('/');

    /// <summary>不是 / 開頭、執行時會被略過的行，編輯時標出來用。</summary>
    public static List<string> InvalidLines(string command)
    {
        var result = new List<string>();

        foreach (var line in Lines(command))
        {
            if (!IsCommand(line))
                result.Add(line);
        }

        return result;
    }

    private bool TryDirect(GameSource source)
    {
        switch ((HotbarSlotType)source.Type)
        {
            case HotbarSlotType.GearSet:
                var gearset = (int)source.Id;
                queue.Enqueue(($"換套裝 #{gearset + 1}", () => EquipGearset(gearset)));
                return true;

            case HotbarSlotType.Emote:
                var emote = (ushort)source.Id;
                queue.Enqueue(($"情感動作 #{emote}", () => ExecuteEmote(emote)));
                return true;

            default:
                return false;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (queue.Count == 0) return;
        if (!Services.ClientState.IsLoggedIn) return;

        var (description, run) = queue.Dequeue();
        try
        {
            Services.Log.Debug($"執行：{description}");
            run();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, $"執行失敗：{description}");
        }
    }

    private static void EquipGearset(int index)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null || !module->IsValidGearset(index))
        {
            Services.Log.Warning($"套裝 #{index + 1} 不存在");
            return;
        }

        module->EquipGearset(index, 0);
    }

    private static void ExecuteEmote(ushort emoteId)
    {
        var agent = AgentEmote.Instance();
        if (agent == null) return;

        if (!agent->CanUseEmote(emoteId))
        {
            Services.Log.Warning($"情感動作 #{emoteId} 現在不能用");
            return;
        }

        agent->ExecuteEmote(emoteId, null, true, true);
    }

    /// <summary>/gearset change "名稱"：遊戲的指令只吃編號，名稱要自己換成編號。</summary>
    [GeneratedRegex("^/(?:gearset|gs)\\s+change\\s+\"(.+)\"\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GearsetByName();

    private static bool TryGearsetByName(string command)
    {
        var match = GearsetByName().Match(command);
        if (!match.Success) return false;

        var name = match.Groups[1].Value;
        var module = RaptureGearsetModule.Instance();
        if (module == null) return true;

        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;

            var entry = module->GetGearset(i);
            if (entry == null || entry->NameString != name) continue;

            module->EquipGearset(i, 0);
            return true;
        }

        Services.Log.Warning($"找不到名稱是「{name}」的套裝");
        return true;
    }

    private static void Execute(string command)
    {
        if (TryGearsetByName(command)) return;

        // 先讓 Dalamud 處理插件指令，其餘是遊戲的指令，交給遊戲的 shell。
        // 進到這裡的一定是 / 開頭，純文字在 Enqueue 就擋掉了
        if (Services.Commands.ProcessCommand(command)) return;

        var uiModule = UIModule.Instance();
        if (uiModule == null) return;

        var shell = uiModule->GetRaptureShellModule();
        if (shell == null) return;

        var utf8 = Utf8String.FromString(command);
        try
        {
            shell->ExecuteCommandInner(utf8, uiModule);
        }
        finally
        {
            utf8->Dtor(true);
        }
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        queue.Clear();
    }
}

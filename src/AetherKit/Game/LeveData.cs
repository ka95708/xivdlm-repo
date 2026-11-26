using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 製作理符的資料，全部從遊戲表讀：
/// - Leve：名稱、職業（LeveAssignmentType 5–12 = 木工師…烹調師）、等級、交貨 NPC 的位置（LevelLevemete）。
/// - CraftLeve：要交的道具與數量。
/// - 發行人：同一張地圖裡、離交貨 NPC 最近、身上有「理符發行處」（GuildleveAssignment）的 NPC。
/// </summary>
public static class LeveData
{
    /// <summary>一個製作理符。</summary>
    public sealed record Info(
        uint Id,
        string Name,
        uint Job,
        string JobName,
        int Level,
        uint ItemId,
        string ItemName,
        int ItemCount,
        uint Territory,
        uint ClientNpc,
        string ClientName,
        Vector3 ClientPosition,
        uint IssuerNpc,
        string IssuerName,
        Vector3 IssuerPosition);

    // LeveAssignmentType 5（木工師）到 12（烹調師）對應 ClassJob 8 到 15
    private const uint FirstCraftAssignment = 5;
    private const uint LastCraftAssignment = 12;
    private const uint FirstCrafterJob = 8;

    private static readonly Dictionary<uint, Info?> cache = new();

    /// <summary>某個理符的詳細資料；不是製作理符、或找不到 NPC 時回傳 null。</summary>
    public static Info? Get(uint leveId)
    {
        if (cache.TryGetValue(leveId, out var cached)) return cached;

        var info = Read(leveId);
        cache[leveId] = info;
        return info;
    }

    private static string JobName(Lumina.Excel.ExcelSheet<ClassJob> jobs, uint assignment) =>
        jobs.GetRowOrDefault(FirstCrafterJob + assignment - FirstCraftAssignment)?.Name.ExtractText() ?? "?";

    private static Info? Read(uint leveId)
    {
        if (Services.Data.GetExcelSheet<Leve>().GetRowOrDefault(leveId) is not { } leve) return null;

        var assignment = leve.LeveAssignmentType.RowId;
        if (assignment is < FirstCraftAssignment or > LastCraftAssignment) return null;

        if (Services.Data.GetExcelSheet<CraftLeve>().GetRowOrDefault(leve.DataId.RowId) is not { } craft) return null;
        if (craft.Item.Count == 0 || craft.Item[0].RowId == 0) return null;

        if (leve.LevelLevemete.ValueNullable is not { } clientLevel) return null;

        var territory = clientLevel.Territory.RowId;
        var clientPosition = new Vector3(clientLevel.X, clientLevel.Y, clientLevel.Z);
        var clientNpc = clientLevel.Object.RowId;

        var (issuerNpc, issuerPosition) = FindIssuer(territory, clientPosition);
        if (issuerNpc == 0) return null;

        var names = Services.Data.GetExcelSheet<ENpcResident>();
        var itemId = craft.Item[0].RowId;

        return new Info(
            leveId,
            leve.Name.ExtractText(),
            FirstCrafterJob + assignment - FirstCraftAssignment,
            JobName(Services.Data.GetExcelSheet<ClassJob>(), assignment),
            leve.ClassJobLevel,
            itemId,
            Services.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}",
            craft.ItemCount[0],
            territory,
            clientNpc,
            names.GetRowOrDefault(clientNpc)?.Singular.ExtractText() ?? $"#{clientNpc}",
            clientPosition,
            issuerNpc,
            names.GetRowOrDefault(issuerNpc)?.Singular.ExtractText() ?? $"#{issuerNpc}",
            issuerPosition);
    }

    /// <summary>這張地圖裡離 near 最近、會發理符的 NPC（ENpcData 裡有 GuildleveAssignment）。</summary>
    private static (uint Npc, Vector3 Position) FindIssuer(uint territory, Vector3 near)
    {
        var npcs = Services.Data.GetExcelSheet<ENpcBase>();
        var assignments = Services.Data.GetExcelSheet<GuildleveAssignment>();

        var best = (Npc: 0u, Position: Vector3.Zero);
        var bestDistance = float.MaxValue;

        foreach (var level in Services.Data.GetExcelSheet<Level>())
        {
            if (level.Territory.RowId != territory) continue;

            var npcId = level.Object.RowId;
            if (npcId is < 1_000_000 or >= 2_000_000) continue;
            if (npcs.GetRowOrDefault(npcId) is not { } npc) continue;
            if (!npc.ENpcData.Any(d => d.RowId != 0 && assignments.HasRow(d.RowId))) continue;

            var position = new Vector3(level.X, level.Y, level.Z);
            var distance = Vector3.Distance(position, near);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = (npcId, position);
        }

        return best;
    }
}

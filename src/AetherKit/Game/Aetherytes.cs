using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>以太之光（大水晶）：哪些已經解鎖、在哪張地圖、哪個最近。</summary>
public static unsafe class Aetherytes
{
    /// <summary>一個傳送點。住宅的 Sub = 傳送清單裡的 SubIndex（同一個 Id 下分辨是哪一間），Housing = true。</summary>
    public readonly record struct Entry(uint Id, string Name, uint Territory, Vector3? Position, byte Sub = 0, bool Housing = false);

    private static List<Entry> cache = new();
    private static List<Entry> housingCache = new();
    private static System.DateTime cachedAt;

    /// <summary>已解鎖、可以傳送的大水晶（不含住宅、小水晶）。設定頁每幀都會問，快取 5 秒。</summary>
    public static List<Entry> Unlocked()
    {
        Refresh();
        return cache;
    }

    /// <summary>傳送清單裡的住宅（部隊房屋、個人房屋、合租房屋、公寓）。</summary>
    public static List<Entry> Housing()
    {
        Refresh();
        return housingCache;
    }

    private static void Refresh()
    {
        var now = System.DateTime.Now;
        if (now - cachedAt < System.TimeSpan.FromSeconds(5) && cache.Count > 0) return;

        (cache, housingCache) = Read();
        cachedAt = now;
    }

    private static (List<Entry> Aetherytes, List<Entry> Housing) Read()
    {
        var telepo = Telepo.Instance();
        if (telepo == null) return (new List<Entry>(), new List<Entry>());

        telepo->UpdateAetheryteList();

        var sheet = Services.Data.GetExcelSheet<Aetheryte>();
        var result = new List<Entry>();
        var housing = new List<Entry>();

        foreach (var info in telepo->TeleportList)
        {
            var row = sheet.GetRowOrDefault(info.AetheryteId);

            // 傳送清單裡不是大水晶的就是住宅：Aetheryte 表有各住宅區的「公會房屋」「個人房屋」專用列（IsAetheryte = false）。
            // 傳送清單不一定填區號、門牌，EstateType 對一般水晶也不是 0，都不能拿來分辨。
            if (row is not { IsAetheryte: true } crystal)
            {
                housing.Add(new Entry(info.AetheryteId, HousingName(info, row), info.TerritoryId, null, info.SubIndex, true));
                continue;
            }

            result.Add(new Entry(info.AetheryteId, crystal.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{info.AetheryteId}",
                info.TerritoryId, PositionOf(crystal)));
        }

        return (result, housing);
    }

    /// <summary>住宅的顯示名稱：表上的名稱（公會房屋、個人房屋…）＋住宅區；有區號門牌就加上。</summary>
    private static string HousingName(TeleportInfo info, Aetheryte? row)
    {
        var kind = row?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } name ? name : $"住宅 #{info.AetheryteId}";
        var where = ZoneName(info.TerritoryId);
        var address = info.Ward != 0 || info.Plot != 0 ? $" 第 {info.Ward + 1} 區 {info.Plot + 1} 號" : "";
        var sub = info.SubIndex != 0 ? $" #{info.SubIndex}" : "";
        return $"{kind}（{where}{address}）{sub}";
    }

    /// <summary>這張地圖裡離 position 最近的已解鎖大水晶；不知道位置時挑第一個。</summary>
    public static Entry? Nearest(uint territory, Vector3? position)
    {
        var inZone = Unlocked().Where(a => a.Territory == territory).ToList();
        if (inZone.Count == 0) return null;
        if (position is not { } pos) return inZone[0];

        return inZone
            .OrderBy(a => a.Position is { } p ? Distance2D(p, pos) : float.MaxValue)
            .First();
    }

    public static bool Teleport(uint aetheryteId, byte sub = 0) => Telepo.Instance()->Teleport(aetheryteId, sub);

    public static string ZoneName(uint territory) =>
        Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText()
        is { Length: > 0 } name
            ? name
            : $"區域 #{territory}";

    /// <summary>
    /// 水晶在地圖上的座標：Aetheryte 表的第一個 Level；沒填的話（實測憶想之地 3 個水晶全都沒填）改用地圖上的水晶圖示換算。
    /// 用圖示換算的沒有高度（Y = 0），比距離要用水平距離。
    /// </summary>
    private static Vector3? PositionOf(Aetheryte row)
    {
        if (row.Level.Count > 0 && row.Level[0].ValueNullable is { } level && level.RowId != 0)
            return new Vector3(level.X, level.Y, level.Z);

        if (row.Territory.ValueNullable?.Map.ValueNullable is not { } map) return null;
        if (!Services.Data.GetSubrowExcelSheet<MapMarker>().TryGetRow(map.MapMarkerRange, out var markers)) return null;

        foreach (var marker in markers)
        {
            // DataType 3 = 以太之光，DataKey = Aetheryte 列號
            if (marker.DataType != 3 || marker.DataKey.RowId != row.RowId) continue;

            // 地圖圖示是 2048 像素的地圖座標：世界座標 = (像素 - 1024) / 縮放 - 偏移
            var scale = map.SizeFactor / 100f;
            return new Vector3((marker.X - 1024f) / scale - map.OffsetX, 0, (marker.Y - 1024f) / scale - map.OffsetY);
        }

        return null;
    }

    /// <summary>水平距離（圖示換算的水晶座標沒有高度）。</summary>
    public static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}

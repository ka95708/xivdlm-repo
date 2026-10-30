using System;
using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Data.Files;

namespace AetherKit.UI;

/// <summary>取遊戲圖示，並提供不可用狀態要用的灰階版本。</summary>
public static class Icons
{
    // 灰階得自己轉一張貼圖出來，所以要快取，並在卸載時釋放
    private static readonly Dictionary<uint, IDalamudTextureWrap?> grayscaleCache = new();

    public static IDalamudTextureWrap? Get(uint id)
    {
        if (id == 0) return null;

        // 一定要走 TryGetFromGameIcon：GetFromGameIcon 對不存在的 ID 會丟
        // IconNotFoundException，在繪製迴圈裡等於整個視窗掛掉。
        return Services.Textures.TryGetFromGameIcon(new GameIconLookup(id), out var shared)
            ? shared.GetWrapOrDefault()
            : null;
    }

    /// <summary>
    /// 灰階沒辦法用 tint 做出來（疊色是逐通道相乘，算不出亮度），
    /// 只能把原圖讀出來自己換算再造一張貼圖，所以一定要快取。
    /// </summary>
    public static IDalamudTextureWrap? GetGrayscale(uint id)
    {
        if (id == 0) return null;

        if (grayscaleCache.TryGetValue(id, out var cached))
            return cached ?? Get(id);

        IDalamudTextureWrap? made = null;

        try
        {
            if (Services.Textures.TryGetIconPath(new GameIconLookup(id), out var path))
            {
                var tex = Services.Data.GetFile<TexFile>(path);
                if (tex != null)
                {
                    // Lumina 的 ImageData 是 A8R8G8B8，在記憶體裡的位元組順序是 B,G,R,A
                    var pixels = (byte[])tex.ImageData.Clone();

                    for (var i = 0; i + 3 < pixels.Length; i += 4)
                    {
                        // Rec.709 亮度權重，直接平均會讓綠色太暗、藍色太亮
                        var luma = (byte)((pixels[i] * 0.0722f)
                                          + (pixels[i + 1] * 0.7152f)
                                          + (pixels[i + 2] * 0.2126f));

                        pixels[i] = luma;
                        pixels[i + 1] = luma;
                        pixels[i + 2] = luma;
                    }

                    made = Services.Textures.CreateFromRaw(
                        RawImageSpecification.Bgra32(tex.Header.Width, tex.Header.Height), pixels);
                }
            }
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, $"灰階轉換失敗: {id}");
        }

        grayscaleCache[id] = made;
        return made ?? Get(id);
    }

    /// <summary>插件卸載時釋放自己造的貼圖。</summary>
    public static void Dispose()
    {
        foreach (var texture in grayscaleCache.Values)
            texture?.Dispose();

        grayscaleCache.Clear();
    }
}

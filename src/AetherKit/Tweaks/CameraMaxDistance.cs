using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace AetherKit.Tweaks;

/// <summary>
/// 鏡頭最大距離（取代 Cammy，只搬「最大距離」這一項）：滾輪能把鏡頭拉得比遊戲預設的 20 更遠。
/// 遊戲沒有這個設定，只能改鏡頭的最大距離欄位；切換地圖、過場後遊戲可能改回去，所以每幀檢查、不一樣才寫。
/// 關掉時改回遊戲預設 20。
/// </summary>
public sealed unsafe class CameraMaxDistance : Tweak
{
    /// <summary>遊戲預設的最大距離。</summary>
    private const float GameDefault = 20f;

    private const float Min = GameDefault;
    private const float Max = 100f;

    public override string Id => "camera-max-distance";
    public override string Name => "鏡頭最大距離";
    public override string Description => "滾輪可以把鏡頭拉得比遊戲預設更遠。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "滑鼠滾輪拉遠鏡頭";

    public override void Update(DateTime now) => Apply(Math.Clamp(Config.CameraMaxDistance, Min, Max));

    protected override void OnDisable() => Apply(GameDefault);

    private void Apply(float distance)
    {
        var manager = CameraManager.Instance();
        if (manager == null) return;

        var camera = manager->GetActiveCamera();
        if (camera == null || Math.Abs(camera->MaxDistance - distance) < 0.01f) return;

        // [改記憶體] 鏡頭的最大距離欄位（同 Cammy）
        camera->MaxDistance = distance;
        Log.Write($"鏡頭最大距離設成 {distance:0.#}");
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var distance = Config.CameraMaxDistance;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("最大距離", ref distance, Min, Max, "%.0f"))
        {
            Config.CameraMaxDistance = Math.Clamp(distance, Min, Max);
            Save();
        }

        ImGui.TextDisabled($"遊戲預設 {GameDefault:0}；關掉這個小功能就改回預設。拉太遠時鏡頭可能穿進牆或地形。");
    }
}

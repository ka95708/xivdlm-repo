using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 肖像動作逐格調整：肖像編輯視窗上方多一個「逐格調整」按鈕，打開後可以一格一格前後移動動作、
/// 拖拉滑桿直接跳到某個時間點，停在想要的那一格再存檔。
///
/// 設定時間點用的是肖像預覽自己的「指定時間載入動作」（讀取存好的肖像時遊戲也是這樣做），
/// 暫停／播放跟取消勾選「播放動作」一樣；改完標記為已修改，存檔按鈕才會亮。
/// 動作總長度從角色骨架上的動畫讀出來，只是拿來顯示和限制範圍。
/// </summary>
public sealed unsafe class PortraitAnimationFrames : Tweak
{
    private const string Addon = "BannerEditor";

    // 動作以每秒 30 格計
    private const float FramesPerSecond = 30f;

    // 讀不到動作長度時，滑桿最多拉到這裡
    private const int FallbackFrames = 300;

    private bool open;

    public override string Id => "portrait-animation-frames";
    public override string Name => "肖像動作逐格調整";
    public override string Description => "肖像編輯時可以一格一格調整動作、拖拉滑桿選時間點，停在想要的那一格再存檔。";
    public override TweakGroup Group => TweakGroup.Portrait;
    public override TweakKind Kind => TweakKind.Button;
    public override string Where => "肖像編輯視窗上方的「逐格調整」";

    protected override void OnDisable() => open = false;

    public override void AddButtons(AddonBar bar)
    {
        if (Addons.Get(Addon) == null)
        {
            open = false;
            return;
        }

        bar.Button(Addon, open ? "收起逐格調整" : "逐格調整", () => open = !open, tooltip: Description);
    }

    public override void Draw()
    {
        if (!open) return;

        var addon = Addons.Get(Addon);
        var view = CharaView();
        if (addon == null || addon->RootNode == null || view == null) return;

        var chara = view->GetCharacter();
        if (chara == null) return;

        // 貼在肖像編輯視窗下緣外面、靠左
        var height = addon->RootNode->Height * addon->Scale;
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(addon->X, addon->Y + height + 2), ImGuiCond.Always);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##aetherkit-portrait-frames", flags))
            DrawControls(view, chara);

        ImGui.End();
    }

    private void DrawControls(CharaViewPortrait* view, Character* chara)
    {
        var frame = view->GetAnimationTime();
        var total = TotalFrames(chara);
        var paused = view->IsAnimationPaused();

        float? target = null;

        if (ImGui.Button("-10")) target = frame - 10;
        ImGui.SameLine();
        if (ImGui.Button("-1")) target = frame - 1;
        ImGui.SameLine();

        if (ImGui.Button(paused ? "播放" : "暫停"))
            SetPaused(view, !paused);

        ImGui.SameLine();
        if (ImGui.Button("取整")) target = MathF.Round(frame);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("停到最接近的整數格");

        ImGui.SameLine();
        if (ImGui.Button("+1")) target = frame + 1;
        ImGui.SameLine();
        if (ImGui.Button("+10")) target = frame + 10;

        ImGui.SameLine();
        ImGui.TextDisabled($"第 {frame:0.##} 格／{total}（{frame / FramesPerSecond:0.00} 秒）");

        var slider = frame;
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##frame", ref slider, 0, total, "%.2f")) target = slider;

        if (target is { } value)
            SetFrame(view, chara, Math.Clamp(value, 0, total));
    }

    /// <summary>用指定時間點重新載入目前的動作，然後暫停在那一格。</summary>
    private static void SetFrame(CharaViewPortrait* view, Character* chara, float frame)
    {
        var pose = chara->Timeline.BannerTimelineRowId;
        if (pose == 0) return;

        view->SetPoseTimed(pose, frame);
        SetPaused(view, true);

        var state = EditorState();
        if (state != null && !state->HasDataChanged) state->SetHasChanged(true);
    }

    /// <summary>暫停或播放動作，並讓編輯視窗上的「播放動作」勾選框跟著一致。</summary>
    private static void SetPaused(CharaViewPortrait* view, bool paused)
    {
        view->ToggleAnimationPlayback(paused);

        var editor = (AddonBannerEditor*)Addons.Get(Addon);
        if (editor != null && editor->PlayAnimationCheckbox != null)
            editor->PlayAnimationCheckbox->SetChecked(!paused);
    }

    private static AgentBannerEditorState* EditorState()
    {
        var agent = AgentBannerEditor.Instance();
        return agent == null ? null : agent->EditorState;
    }

    private static CharaViewPortrait* CharaView()
    {
        var state = EditorState();
        return state == null ? null : state->CharaView;
    }

    /// <summary>目前動作總共幾格：從角色骨架上第一個動畫的長度換算；讀不到就用預設值。</summary>
    private static int TotalFrames(Character* chara)
    {
        var drawObject = (CharacterBase*)chara->GameObject.DrawObject;
        if (drawObject == null || drawObject->Skeleton == null) return FallbackFrames;

        var skeleton = drawObject->Skeleton;
        if (skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null) return FallbackFrames;

        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null || animated->AnimationControls.Length == 0) return FallbackFrames;

        var control = animated->AnimationControls[0].Value;
        if (control == null) return FallbackFrames;

        var binding = control->Binding.ptr;
        if (binding == null || binding->Animation.ptr == null) return FallbackFrames;

        // 動畫尾端有一小段是銜接用的，扣掉不算
        var duration = binding->Animation.ptr->Duration - 0.5f;
        return duration > 0 ? (int)MathF.Round(duration * FramesPerSecond) : FallbackFrames;
    }
}

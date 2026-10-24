using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 所有右鍵選單共用的外觀與元件。選單長得都一樣，使用者才不用每個重新認識；
/// 顏色從主題拿，跟面板是同一套。
///
/// 用法：if (Menu.Begin("id")) { ...; Menu.End(); }
/// </summary>
public static class Menu
{
    private const float MinWidth = 200f;

    private static Theme Theme => Plugin.Instance.Config.Theme;

    private const int StyleVars = 6;
    private const int StyleColors = 3;

    private static void Push()
    {
        var theme = Theme;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, theme.Rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, theme.BorderWidth);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Rounding * 0.5f);

        // 選單要比面板不透明一點，不然底下的內容會干擾閱讀
        var background = theme.PanelBackground with { W = MathF.Max(theme.PanelBackground.W, 0.97f) };

        ImGui.PushStyleColor(ImGuiCol.PopupBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, theme.Border);
        ImGui.PushStyleColor(ImGuiCol.Text, theme.TextPrimary);
    }

    private static void Pop()
    {
        ImGui.PopStyleColor(StyleColors);
        ImGui.PopStyleVar(StyleVars);
    }

    /// <summary>開始一個選單。回傳 true 時呼叫端必須在最後呼叫 End()。</summary>
    public static bool Begin(string id)
    {
        Push();
        ImGui.SetNextWindowSizeConstraints(new Vector2(MinWidth, 0), new Vector2(float.MaxValue, float.MaxValue));

        if (ImGui.BeginPopup(id))
            return true;

        Pop();
        return false;
    }

    public static void End()
    {
        ImGui.EndPopup();
        Pop();
    }

    /// <summary>選單最上面那一行：這個選單是針對什麼開的。</summary>
    public static void Header(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    /// <summary>選單中間的小節標題，用來把「動作」和「設定」分開。</summary>
    public static void Section(string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void Separator()
    {
        ImGui.Spacing();
        ImGui.Separator();
    }

    /// <summary>一般的選項。</summary>
    public static bool Item(string label, bool selected = false) =>
        ImGui.MenuItem(label, string.Empty, selected);

    /// <summary>這個選單最主要的那個動作，用強調色讓它一眼就找得到。</summary>
    public static bool Primary(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        var clicked = ImGui.MenuItem(label);
        ImGui.PopStyleColor();
        return clicked;
    }

    /// <summary>會刪東西的選項，用紅字標出來，而且固定放在最下面。</summary>
    public static bool Danger(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
        var clicked = ImGui.MenuItem(label);
        ImGui.PopStyleColor();
        return clicked;
    }
}

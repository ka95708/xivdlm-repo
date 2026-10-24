using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 管理視窗的「匯入匯出」分頁。匯出一律是整份設定；匯入時先看裡面有什麼，
/// 再勾要取代哪幾部分 — 例如只拿別人的工具列，自己的主題和條件組留著。
/// </summary>
public class TransferSettings
{
    private readonly Plugin plugin;

    private Configuration? pending;
    private string message = string.Empty;
    private bool messageIsError;

    // 匯入時要取代哪幾部分
    private bool takeTopBar = true;
    private bool takeConditions = true;
    private bool takeCursor = true;
    private bool takeTheme = true;

    public TransferSettings(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        Paint.SettingsHeading("匯出");
        ImGui.TextDisabled("把整份設定（工具列與面板、條件組、游標提示、主題）複製成一行文字。");

        if (ImGui.Button("複製到剪貼簿"))
        {
            ImGui.SetClipboardText(ConfigTransfer.Export(plugin.Config));
            Show("已複製。可以貼到記事本保存，或傳給別人匯入。", false);
        }

        Paint.SettingsHeading("匯入");
        ImGui.TextDisabled("先複製別人給的設定文字，再按下面的按鈕。套用前會先讓你確認要取代哪些部分。");

        if (ImGui.Button("從剪貼簿讀取"))
            Read();

        if (pending != null)
            DrawPending(pending);

        if (message.Length > 0)
        {
            ImGui.Spacing();
            var theme = plugin.Config.Theme;
            ImGui.TextColored(messageIsError ? theme.Danger : theme.Positive, message);
        }
    }

    private void Show(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    private void Read()
    {
        pending = ConfigTransfer.Import(ImGui.GetClipboardText() ?? string.Empty, out var error);

        if (pending == null)
        {
            Show(error, true);
            return;
        }

        // 讀到的內容裡沒有的部分就不勾，避免把自己的設定換成空的
        takeTopBar = pending.TopBar.Items.Count > 0;
        takeConditions = pending.ConditionSets.Count > 0;
        takeCursor = pending.Cursor.Indicators.Count > 0;
        takeTheme = true;

        message = string.Empty;
    }

    private void DrawPending(Configuration incoming)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("讀到的設定包含：");

        var sections = incoming.TopBar.Items
            .Where(item => item.Panel != null)
            .Sum(item => item.Panel!.Sections.Count());

        ImGui.Checkbox($"工具列與面板（{incoming.TopBar.Items.Count} 個項目、{sections} 個分區，含工具列位置）", ref takeTopBar);
        ImGui.Checkbox($"條件組（{incoming.ConditionSets.Count} 組）", ref takeConditions);
        ImGui.Checkbox($"游標提示（{incoming.Cursor.Indicators.Count} 個）", ref takeCursor);
        ImGui.Checkbox("主題", ref takeTheme);

        // 工具列會用到條件組、游標提示會用到工具列按鈕，只拿一邊時提醒一下
        if (takeTopBar && !takeConditions && incoming.TopBar.ConditionSetId != System.Guid.Empty)
            ImGui.TextDisabled("・工具列有設顯示條件，沒一起匯入條件組的話會變成永遠顯示。");

        if (takeCursor && !takeTopBar && incoming.Cursor.Indicators.Count > 0)
            ImGui.TextDisabled("・游標提示跟著的是對方的工具列按鈕，沒一起匯入工具列的話會找不到按鈕。");

        ImGui.Spacing();

        var any = takeTopBar || takeConditions || takeCursor || takeTheme;

        if (!any)
            ImGui.BeginDisabled();

        ImGui.PushStyleColor(ImGuiCol.Text, plugin.Config.Theme.Danger);
        var apply = ImGui.Button("套用（取代勾選的部分）");
        ImGui.PopStyleColor();

        if (!any)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("取消"))
            pending = null;

        if (!apply) return;

        Apply(incoming);
        pending = null;
        Show("已套用。", false);
    }

    private void Apply(Configuration incoming)
    {
        var config = plugin.Config;

        if (takeTopBar)
            config.TopBar = incoming.TopBar;

        if (takeConditions)
            config.ConditionSets = incoming.ConditionSets;

        if (takeCursor)
            config.Cursor = incoming.Cursor;

        if (takeTheme)
            config.Theme = incoming.Theme;

        plugin.AfterImport();
    }
}

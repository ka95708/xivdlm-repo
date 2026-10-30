using System.Collections.Generic;
using System.Linq;
using AetherKit.Modules;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 管理視窗的「匯入匯出」頁。單位是「部分」：每個模組一部分，再加上主題。
/// 匯出時勾要帶哪些；匯入時只列出字串裡真的有的，再勾要套用哪些，沒勾的保留自己的。
/// 例如只拿別人的工具列，自己的主題留著。
/// </summary>
public class TransferSettings
{
    private readonly Plugin plugin;

    /// <summary>匯出要帶的部分。預設全部。</summary>
    private readonly HashSet<string> exportParts = new();
    private bool exportPartsInitialized;

    private ImportedSettings? pending;

    /// <summary>匯入要套用的部分。</summary>
    private readonly HashSet<string> applyParts = new();

    private string message = string.Empty;
    private bool messageIsError;

    public TransferSettings(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>這個版本認得的所有部分：模組依清單順序，主題放最後。</summary>
    private IEnumerable<(string Id, string Name)> KnownParts =>
        plugin.Modules.Select(m => (m.Id, m.Name)).Append((ConfigTransfer.ThemePart, "主題"));

    public void Draw()
    {
        DrawExport();
        DrawImport();

        if (message.Length > 0)
        {
            ImGui.Spacing();
            var theme = plugin.Config.Theme;
            ImGui.TextColored(messageIsError ? theme.Danger : theme.Positive, message);
        }
    }

    private void DrawExport()
    {
        Paint.SettingsHeading("匯出");

        if (!exportPartsInitialized)
        {
            foreach (var (id, _) in KnownParts)
                exportParts.Add(id);

            exportPartsInitialized = true;
        }

        foreach (var (id, name) in KnownParts)
            PartCheckbox($"{name}##export", id, exportParts);

        if (exportParts.Count == 0)
            ImGui.BeginDisabled();

        if (ImGui.Button("複製到剪貼簿"))
        {
            ImGui.SetClipboardText(ConfigTransfer.Export(plugin.Config, plugin.Modules, exportParts));
            Show("已複製。可以貼到記事本保存，或傳給別人匯入。", false);
        }

        if (exportParts.Count == 0)
            ImGui.EndDisabled();
    }

    private void DrawImport()
    {
        Paint.SettingsHeading("匯入");
        ImGui.TextDisabled("先複製設定文字，再按下面的按鈕。套用前會先讓你勾選要取代哪些部分。");

        if (ImGui.Button("從剪貼簿讀取"))
            Read();

        if (pending != null)
            DrawPending(pending);
    }

    private static void PartCheckbox(string label, string id, HashSet<string> set)
    {
        var on = set.Contains(id);
        if (!ImGui.Checkbox(label, ref on)) return;

        if (on)
            set.Add(id);
        else
            set.Remove(id);
    }

    private void Show(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    private void Read()
    {
        pending = ConfigTransfer.Import(ImGui.GetClipboardText() ?? string.Empty, plugin.Modules, out var error);

        if (pending == null)
        {
            Show(error, true);
            return;
        }

        // 預設勾選字串裡有、而且真的有內容的部分，避免把自己的設定換成空的
        applyParts.Clear();

        foreach (var id in pending.Parts)
        {
            var module = plugin.Modules.FirstOrDefault(m => m.Id == id);
            if (id == ConfigTransfer.ThemePart || module != null && module.HasSettings(pending.Config))
                applyParts.Add(id);
        }

        message = string.Empty;
    }

    private void DrawPending(ImportedSettings incoming)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("讀到的設定包含：");

        foreach (var (id, name) in KnownParts)
        {
            if (incoming.Parts.Contains(id))
                PartCheckbox($"{name}##apply", id, applyParts);
        }

        // 別的（較新的）版本才有的模組，這個版本套用不了，列出來讓使用者知道
        foreach (var id in incoming.Parts)
        {
            if (KnownParts.All(part => part.Id != id))
                ImGui.TextDisabled($"・{id}：這個版本沒有這個模組，略過");
        }

        ImGui.Spacing();

        var any = applyParts.Count > 0;

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

        Apply(incoming.Config);
        pending = null;
        Show("已套用。", false);
    }

    private void Apply(Configuration incoming)
    {
        var config = plugin.Config;

        foreach (var module in plugin.Modules)
        {
            if (applyParts.Contains(module.Id))
                module.CopySettings(incoming, config);
        }

        if (applyParts.Contains(ConfigTransfer.ThemePart))
            config.Theme = incoming.Theme;

        plugin.AfterImport();
    }
}

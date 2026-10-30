using System;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>模組頁裡的一個分頁。</summary>
public readonly record struct ModuleTab(string Title, Action Draw);

/// <summary>
/// 一個功能模組。插件本身只提供共用的基礎（設定檔、指令執行、主題、快捷鍵總覽、匯入匯出、
/// 圖示瀏覽器、右鍵選單），功能都做成模組，使用者在管理視窗左側清單裡個別開關。
///
/// 停用的模組不加視窗、不畫東西、不偵測按鍵，跟沒裝一樣；但它的設定還留在設定檔裡，
/// 重新啟用時原封不動。新增模組：實作這個介面，再到 Plugin 的模組清單裡加一行。
/// </summary>
public interface IModule : IDisposable
{
    /// <summary>存進設定檔記錄啟用狀態、匯出時標記內容用，發布後不要再改。</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>模組頁最上面的一句說明。</summary>
    string Description { get; }

    /// <summary>啟用：把自己的視窗加進視窗系統、開始監聽事件。</summary>
    void Enable(WindowSystem windows);

    /// <summary>停用：把 Enable 做的事全部收回。</summary>
    void Disable(WindowSystem windows);

    /// <summary>每幀、所有視窗畫之前。</summary>
    void BeforeWindows() { }

    /// <summary>每幀、所有視窗畫完之後。</summary>
    void AfterWindows() { }

    /// <summary>模組頁「啟用」旁邊的控制項（例如工具列的編輯模式）。只在啟用時畫。</summary>
    void DrawHeader() { }

    /// <summary>模組頁的分頁。只在啟用時出現。</summary>
    IEnumerable<ModuleTab> SettingsTabs => Array.Empty<ModuleTab>();

    /// <summary>這份設定裡有沒有這個模組的內容。匯出、匯入時用來決定要不要列出它。</summary>
    bool HasSettings(Configuration config) => false;

    /// <summary>把這個模組的設定從一份設定複製到另一份。匯出時抽出、匯入時套用都用這個。</summary>
    void CopySettings(Configuration from, Configuration to) { }
}

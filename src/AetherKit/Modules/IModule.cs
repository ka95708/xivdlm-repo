using System;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>模組頁裡的一個分頁。</summary>
public readonly record struct ModuleTab(string Title, Action Draw);

/// <summary>設定視窗左邊清單的分組。</summary>
public enum ModuleGroup
{
    /// <summary>長時間、會操作角色的大功能，有開始和停止。</summary>
    Automation,

    /// <summary>開了就自己運作的功能。</summary>
    Feature
}

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

    ModuleGroup Group => ModuleGroup.Feature;

    /// <summary>自動流程正在執行（左邊清單會標出來）。</summary>
    bool Running => false;

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

    /// <summary>
    /// 能不能在設定裡整個關掉。像小功能這種裡面每一項都有自己開關的模組，
    /// 外層再一個開關只會重複，設成 false 就永遠開著、不顯示「啟用」。
    /// 自動流程預設也不能關：它們本來就要按「開始」才會動，啟用開關沒有意義。
    /// </summary>
    bool CanDisable => Group != ModuleGroup.Automation;

    /// <summary>
    /// 新裝或剛加進來時預設關著，使用者在管理視窗打開才開（例如啟用就會改變畫面的「隱藏其他人」）。
    /// 啟用狀態改存在 Configuration.EnabledModules。
    /// </summary>
    bool OffByDefault => false;

    /// <summary>模組頁的分頁。只在啟用時出現。</summary>
    IEnumerable<ModuleTab> SettingsTabs => Array.Empty<ModuleTab>();

    /// <summary>不分頁、直接畫整頁內容的模組用；有值時忽略 SettingsTabs。</summary>
    Action? DrawPage => null;

    /// <summary>這份設定裡有沒有這個模組的內容。匯出、匯入時用來決定要不要列出它。</summary>
    bool HasSettings(Configuration config) => false;

    /// <summary>把這個模組的設定從一份設定複製到另一份。匯出時抽出、匯入時套用都用這個。</summary>
    void CopySettings(Configuration from, Configuration to) { }
}

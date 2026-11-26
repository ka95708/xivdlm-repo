using System;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 一個小功能：掛在某個遊戲視窗或事件上的小改動，開了就自己運作。
/// 全部放在「小功能」模組裡一起管理；新增一個小功能 = 寫一個子類別，再加進 TweaksModule 的清單。
///
/// 需要核心能力（自動確認、操作鎖……）時在 Enable 借用、Disable 歸還，使用者不用先開任何東西。
/// 要在遊戲視窗上加按鈕，覆寫 AddButtons 登記到共用的按鈕列，不要自己畫。
/// </summary>
public abstract class Tweak
{
    protected Plugin Plugin { get; private set; } = null!;

    protected CoreServices Core => Plugin.Core;

    protected TweaksConfig Config => Plugin.Config.Tweaks;

    /// <summary>存進設定檔記錄開關，發布後不要改。</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>清單上的一句說明。</summary>
    public abstract string Description { get; }

    /// <summary>清單上放在哪一組。</summary>
    public abstract TweakGroup Group { get; }

    public abstract TweakKind Kind { get; }

    /// <summary>會在哪裡出現（例如「分解清單上方的按鈕」），免得開了卻找不到。沒有就 null。</summary>
    public virtual string? Where => null;

    /// <summary>借用核心能力、拿操作鎖時用的名字，核心能力頁會顯示。</summary>
    protected string Owner => $"小功能：{Name}";

    /// <summary>這個小功能自己的 log 檔（tweak-Id-日期.log），出問題時拿來查。</summary>
    protected FeatureLog Log => FeatureLog.For($"tweak-{Id}");

    private string lastError = string.Empty;

    /// <summary>給模組記錄例外用。同一個錯誤每幀都會發生，連續相同的只記一次。</summary>
    internal bool LogError(Exception e)
    {
        var text = e.ToString();
        if (text == lastError) return false;

        lastError = text;
        Log.Write($"錯誤 {text}");
        return true;
    }

    public bool Enabled { get; private set; }

    internal void Attach(Plugin plugin) => Plugin = plugin;

    internal void SetEnabled(bool value)
    {
        if (value == Enabled) return;

        Enabled = value;
        Log.Write(value ? "開啟" : "關閉");
        if (value)
        {
            OnEnable();
        }
        else
        {
            OnDisable();
            Core.ReleaseAll(Owner);
        }
    }

    protected virtual void OnEnable() { }

    protected virtual void OnDisable() { }

    /// <summary>開著時每幀在遊戲執行緒呼叫。</summary>
    public virtual void Update(DateTime now) { }

    /// <summary>開著時每幀在繪製時呼叫：登記要加在遊戲視窗上的按鈕。</summary>
    public virtual void AddButtons(AddonBar bar) { }

    /// <summary>開著時每幀在繪製時呼叫，畫按鈕列以外的東西（例如自己的視窗）。</summary>
    public virtual void Draw() { }

    public virtual bool HasSettings => false;

    /// <summary>清單上展開後的設定。</summary>
    public virtual void DrawSettings() { }

    protected void Save() => Plugin.Save();
}

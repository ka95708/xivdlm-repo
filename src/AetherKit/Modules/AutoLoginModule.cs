using System.Collections.Generic;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 自動登入模組：開遊戲後自動按「開始遊戲」、選設定裡指定的角色、確認登入。
/// 帳號登入是啟動器在開遊戲前做的，這裡從標題畫面才開始接手。
/// </summary>
public sealed class AutoLoginModule : IModule
{
    private readonly AutoLoginSettings settings;

    public AutoLoginModule(Plugin plugin)
    {
        Login = new AutoLogin(plugin);
        settings = new AutoLoginSettings(plugin, Login);
    }

    public AutoLogin Login { get; }

    public string Id => "auto-login";
    public string Name => "自動登入";
    public string Description => "開遊戲後自動按「開始遊戲」、選擇指定的角色並登入。按住 Shift 可以取消。";

    public void Enable(WindowSystem windows) => Login.Start();

    public void Disable(WindowSystem windows) => Login.Stop();

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", settings.Draw); }
    }

    public bool HasSettings(Configuration config) => config.AutoLogin.CharacterId != 0;

    public void CopySettings(Configuration from, Configuration to) => to.AutoLogin = from.AutoLogin;

    public void Dispose() => Login.Dispose();
}

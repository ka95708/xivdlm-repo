using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AetherKit.Game;

/// <summary>
/// 快捷鍵。工具列與面板裡的每個項目都可以設，按下等於點一下；
/// 展開面板的卡片則是開關它的面板。
///
/// 偵測走 Win32 GetKeyboardState，攔截走 Dalamud 的 IKeyState，兩邊刻意分開：
/// IKeyState 是遊戲自己的按鍵陣列，Dalamud 文件直說「讀它幾乎沒意義」，
/// 而且我們攔截時會往裡面寫 false — 如果連偵測也讀它，等於自己把自己消音。
/// </summary>
public unsafe class HotkeyManager
{
    private const int KeyCount = 256;

    // 修飾鍵的虛擬鍵碼。GetKeyboardState 的這幾格已經把左右鍵合併好了。
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private readonly Plugin plugin;
    private readonly byte[] keyboard = new byte[KeyCount];
    private readonly bool[] wasDown = new bool[KeyCount];
    private readonly HashSet<Guid> firing = new();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    public HotkeyManager(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>工具列模組啟用時開始偵測、停用時停止。</summary>
    public void Start() => Services.Framework.Update += OnUpdate;

    public void Stop()
    {
        Services.Framework.Update -= OnUpdate;
        Capturing = null;
        firing.Clear();
    }

    /// <summary>正在等使用者按下組合鍵的那個快捷鍵，設定視窗用。</summary>
    public Hotkey? Capturing { get; set; }

    /// <summary>
    /// 繪製執行緒每幀寫進來的快照。ImGui 的狀態只能在它自己的執行緒上問，
    /// 所以先存起來給遊戲執行緒讀。
    /// </summary>
    public volatile bool ImGuiWantsKeyboard;

    private void OnUpdate(IFramework framework)
    {
        if (!GetKeyboardState(keyboard)) return;

        if (Capturing != null)
        {
            Capture();
            RememberState();
            return;
        }

        // 打字時不觸發。編輯模式下照常觸發，跟點擊一樣，改完就能直接測
        if (IsTyping())
        {
            RememberState();
            return;
        }

        Trigger();
        RememberState();
    }

    private bool IsDown(int vk) => vk is > 0 and < KeyCount && (keyboard[vk] & 0x80) != 0;

    private void RememberState()
    {
        for (var i = 0; i < KeyCount; i++)
            wasDown[i] = (keyboard[i] & 0x80) != 0;
    }

    private void Trigger()
    {
        var ctrl = IsDown(VkControl);
        var alt = IsDown(VkMenu);
        var shift = IsDown(VkShift);

        foreach (var entry in ItemDirectory.All(plugin.Config))
        {
            var item = entry.Item;
            var hotkey = item.Hotkey;
            if (!hotkey.IsSet) continue;

            var match = IsDown(hotkey.Key)
                        && ctrl == hotkey.Ctrl
                        && alt == hotkey.Alt
                        && shift == hotkey.Shift;

            if (!match)
            {
                firing.Remove(item.Id);
                continue;
            }

            if (hotkey.BlocksGame)
                BlockGameKey(hotkey.Key);

            // 只在按下的那一刻觸發一次，按住不連發
            if (!firing.Add(item.Id)) continue;

            if (entry.OpensPanel != null)
                plugin.Toolbar.Panels.RequestToggle(entry.OpensPanel);
            else
                plugin.Press(item);
        }
    }

    /// <summary>把遊戲看到的按鍵狀態清掉。只能寫 0，而且冷門鍵不在有效清單裡。</summary>
    private static void BlockGameKey(int vk)
    {
        if (!Services.KeyState.IsVirtualKeyValid(vk)) return;

        try
        {
            Services.KeyState[vk] = false;
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, $"攔截按鍵失敗: {vk}");
        }
    }

    private void Capture()
    {
        var hotkey = Capturing;
        if (hotkey == null) return;

        if (JustPressed(0x1B)) // Esc 清除
        {
            hotkey.Clear();
            Capturing = null;
            plugin.Save();
            return;
        }

        for (var vk = 0x07; vk < KeyCount; vk++)
        {
            if (IsModifier(vk)) continue;
            if (!JustPressed(vk)) continue;

            hotkey.Key = vk;
            hotkey.Ctrl = IsDown(VkControl);
            hotkey.Alt = IsDown(VkMenu);
            hotkey.Shift = IsDown(VkShift);
            Capturing = null;
            plugin.Save();
            return;
        }
    }

    private bool JustPressed(int vk) => IsDown(vk) && !wasDown[vk];

    /// <summary>修飾鍵本身不能當主鍵，滑鼠鍵（0x01–0x06）也排除在外。</summary>
    private static bool IsModifier(int vk) => vk
        is VkShift or VkControl or VkMenu
        or 0xA0 or 0xA1 // 左右 Shift
        or 0xA2 or 0xA3 // 左右 Ctrl
        or 0xA4 or 0xA5 // 左右 Alt
        or 0x5B or 0x5C; // 左右 Win

    /// <summary>插件的輸入框、或遊戲自己的聊天欄有焦點時不要觸發。</summary>
    private bool IsTyping()
    {
        if (ImGuiWantsKeyboard) return true;

        var atk = RaptureAtkModule.Instance();
        return atk != null && atk->IsTextInputActive();
    }

    public static string Describe(Hotkey hotkey)
    {
        if (!hotkey.IsSet) return "（未設定）";

        var text = string.Empty;
        if (hotkey.Ctrl) text += "Ctrl + ";
        if (hotkey.Alt) text += "Alt + ";
        if (hotkey.Shift) text += "Shift + ";

        return text + ((VirtualKey)hotkey.Key).GetFancyName();
    }
}

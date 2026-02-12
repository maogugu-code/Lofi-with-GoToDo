using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;

namespace WindowFocusLoggerMod
{
    [BepInPlugin("com.yourname.windowfocuslogger", "Window Focus Logger", "1.0.0")]
    public class WindowFocusLoggerPlugin : BaseUnityPlugin
    {
        private IntPtr _lastMouseTopLevelHwnd = IntPtr.Zero;
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private float _timer;
        private ConfigEntry<string> _exeWhitelistConfig;
        private HashSet<string> _exeWhitelist;
        private bool _showWhitelistPanel;
        private string _whitelistEditBuffer = string.Empty;
        private UnityEngine.Vector2 _whitelistScroll;
        private float _pomodoroCheckTimer;
        private bool _isPomodoroWorking;
        private bool _hasPomodoroState;

        // 轮询间隔，避免每帧都调用 Win32 API
        private const float PollIntervalSeconds = 0.15f;
        private const float PomodoroCheckIntervalSeconds = 0.5f;

        private void Awake()
        {
            _exeWhitelistConfig = Config.Bind(
                "Filter",
                "ExeWhitelist",
                "explorer.exe,searchhost.exe,startmenuexperiencehost.exe,shellexperiencehost.exe,taskhostw.exe,dwm.exe,textinputhost.exe,lockapp.exe,applicationframehost.exe,ctfmon.exe,sihost.exe,runtimebroker.exe,systemsettings.exe,smartscreen.exe,taskmgr.exe,conhost.exe",
                "EXE 白名单。面板中按“每行一个 EXE”编辑；程序会自动兼容逗号/分号/换行分隔。"
            );

            _exeWhitelist = BuildExeWhitelist(_exeWhitelistConfig.Value);

            _exeWhitelistConfig.SettingChanged += (_, __) =>
            {
                _exeWhitelist = BuildExeWhitelist(_exeWhitelistConfig.Value);
                Logger.LogInfo($"EXE 白名单已更新，共 {_exeWhitelist.Count} 项");
            };

            Logger.LogInfo("Window Focus Logger 已加载");
            Logger.LogInfo($"EXE 白名单已加载，共 {_exeWhitelist.Count} 项");
        }

        private void Update()
        {
            HandleHotkeys();
            CheckPomodoroStateByUiText();

            _timer += UnityEngine.Time.unscaledDeltaTime;
            if (_timer < PollIntervalSeconds)
                return;

            _timer = 0f;
            CheckMouseFocusWindow();
        }

        private void CheckPomodoroStateByUiText()
        {
            _pomodoroCheckTimer += UnityEngine.Time.unscaledDeltaTime;
            if (_pomodoroCheckTimer < PomodoroCheckIntervalSeconds)
                return;

            _pomodoroCheckTimer = 0f;

            bool detected = ContainsVisibleUiText("创作中");
            if (!_hasPomodoroState || detected != _isPomodoroWorking)
            {
                _hasPomodoroState = true;
                _isPomodoroWorking = detected;
                Logger.LogInfo(_isPomodoroWorking
                    ? "番茄钟状态变化 -> 创作中"
                    : "番茄钟状态变化 -> 非创作中");
            }
        }

        private static bool ContainsVisibleUiText(string keyword)
        {
            try
            {
                var components = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Component>();
                foreach (var component in components)
                {
                    if (component == null)
                        continue;

                    string typeName = component.GetType().FullName;
                    if (typeName != "TMPro.TextMeshProUGUI" && typeName != "UnityEngine.UI.Text")
                        continue;

                    var behaviour = component as UnityEngine.Behaviour;
                    if (behaviour != null && !behaviour.isActiveAndEnabled)
                        continue;

                    PropertyInfo textProp = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (textProp == null)
                        continue;

                    string value = textProp.GetValue(component, null) as string;
                    if (!string.IsNullOrEmpty(value) && value.Contains(keyword))
                        return true;
                }
            }
            catch
            {
                // 忽略异常，避免影响主流程
            }

            return false;
        }

        private void HandleHotkeys()
        {
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F11))
            {
                _showWhitelistPanel = !_showWhitelistPanel;
                if (_showWhitelistPanel)
                {
                    _whitelistEditBuffer = ConvertConfigToEditorText(_exeWhitelistConfig.Value);
                }

                Logger.LogInfo(_showWhitelistPanel
                    ? "已打开白名单设置面板 (F11)"
                    : "已关闭白名单设置面板 (F11)");
            }
        }

        private void CheckMouseFocusWindow()
        {
            if (!GetCursorPos(out POINT point))
                return;

            // 鼠标下命中的通常是子控件窗口，先拿到它
            IntPtr childHwnd = WindowFromPoint(point);
            if (childHwnd == IntPtr.Zero)
                return;

            // 鼠标命中窗口提升到顶层父窗口（优先 GA_ROOT，避免某些 owner 链导致空标题）
            IntPtr mouseTopLevelHwnd = GetAncestor(childHwnd, GA_ROOT);
            if (mouseTopLevelHwnd == IntPtr.Zero)
                mouseTopLevelHwnd = GetAncestor(childHwnd, GA_ROOTOWNER);
            if (mouseTopLevelHwnd == IntPtr.Zero)
                mouseTopLevelHwnd = childHwnd;

            // 前台窗口通常能拿到浏览器活动标签页标题/文档标题
            IntPtr foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == IntPtr.Zero)
                foregroundHwnd = mouseTopLevelHwnd;

            if (mouseTopLevelHwnd == _lastMouseTopLevelHwnd && foregroundHwnd == _lastForegroundHwnd)
                return;

            _lastMouseTopLevelHwnd = mouseTopLevelHwnd;
            _lastForegroundHwnd = foregroundHwnd;

            string mouseTitle = GetWindowTitle(mouseTopLevelHwnd);
            string foregroundTitle = GetWindowTitle(foregroundHwnd);
            string resolvedTitle = !string.IsNullOrWhiteSpace(foregroundTitle)
                ? foregroundTitle
                : (!string.IsNullOrWhiteSpace(mouseTitle) ? mouseTitle : GetWindowTitle(childHwnd));

            int mousePid = 0;
            GetWindowThreadProcessId(mouseTopLevelHwnd, out mousePid);

            int foregroundPid = 0;
            uint foregroundTid = GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

            string processName = "Unknown";
            if (foregroundPid > 0)
            {
                try
                {
                    processName = Process.GetProcessById(foregroundPid).ProcessName;
                }
                catch (Exception e)
                {
                    processName = $"GetProcessError: {e.GetType().Name}";
                }
            }

            string exeName = processName + ".exe";
            if (IsExeWhitelisted(exeName))
                return;

            IntPtr focusControlHwnd = IntPtr.Zero;
            if (foregroundTid != 0)
            {
                GUITHREADINFO gui = new GUITHREADINFO
                {
                    cbSize = Marshal.SizeOf(typeof(GUITHREADINFO))
                };

                if (GetGUIThreadInfo(foregroundTid, ref gui))
                {
                    focusControlHwnd = gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : gui.hwndActive;
                }
            }

            Logger.LogInfo(
                $"焦点窗口变更(非白名单) -> Title: \"{resolvedTitle}\", Exe: \"{exeName}\", Process: \"{processName}\" (PID: {foregroundPid}), MouseTopTitle: \"{mouseTitle}\", ForegroundTitle: \"{foregroundTitle}\", ChildHwnd: 0x{childHwnd.ToInt64():X}, MouseTopHwnd: 0x{mouseTopLevelHwnd.ToInt64():X}, ForegroundHwnd: 0x{foregroundHwnd.ToInt64():X}, FocusControlHwnd: 0x{focusControlHwnd.ToInt64():X}, MousePID: {mousePid}");
        }

        private static HashSet<string> BuildExeWhitelist(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(csv))
                return set;

            foreach (string raw in csv.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = NormalizeExeName(raw);
                if (!string.IsNullOrWhiteSpace(token))
                    set.Add(token);
            }

            return set;
        }

        private bool IsExeWhitelisted(string exeName)
        {
            if (_exeWhitelist == null || _exeWhitelist.Count == 0)
                return false;

            return _exeWhitelist.Contains(NormalizeExeName(exeName));
        }

        private static string NormalizeExeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized = value.Trim().Trim('"').ToLowerInvariant();
            if (!normalized.EndsWith(".exe"))
                normalized += ".exe";

            return normalized;
        }

        private static string ConvertConfigToEditorText(string configValue)
        {
            var set = BuildExeWhitelist(configValue);
            return string.Join(Environment.NewLine, set);
        }

        private static string ConvertEditorTextToConfig(string editorValue)
        {
            var set = BuildExeWhitelist(editorValue);
            return string.Join(",", set);
        }

        private void OnGUI()
        {
            if (!_showWhitelistPanel)
                return;

            const float panelWidth = 760f;
            const float panelHeight = 520f;
            var rect = new UnityEngine.Rect(
                (UnityEngine.Screen.width - panelWidth) * 0.5f,
                (UnityEngine.Screen.height - panelHeight) * 0.5f,
                panelWidth,
                panelHeight);

            UnityEngine.GUI.Box(rect, "Window Focus Logger - EXE 白名单设置");

            UnityEngine.GUILayout.BeginArea(new UnityEngine.Rect(rect.x + 12f, rect.y + 32f, rect.width - 24f, rect.height - 44f));
            UnityEngine.GUILayout.Label("说明：白名单内进程不会输出焦点日志。每行一个 EXE，例如：explorer.exe");
            UnityEngine.GUILayout.Label("快捷键：F11 打开/关闭面板");

            _whitelistScroll = UnityEngine.GUILayout.BeginScrollView(_whitelistScroll, UnityEngine.GUILayout.Height(350f));
            _whitelistEditBuffer = UnityEngine.GUILayout.TextArea(_whitelistEditBuffer, UnityEngine.GUILayout.ExpandHeight(true));
            UnityEngine.GUILayout.EndScrollView();

            UnityEngine.GUILayout.BeginHorizontal();
            if (UnityEngine.GUILayout.Button("保存并应用", UnityEngine.GUILayout.Height(32f)))
            {
                _exeWhitelistConfig.Value = ConvertEditorTextToConfig(_whitelistEditBuffer);
                _exeWhitelistConfig.ConfigFile.Save();
                Logger.LogInfo("白名单已保存并应用");
            }

            if (UnityEngine.GUILayout.Button("恢复当前配置", UnityEngine.GUILayout.Height(32f)))
            {
                _whitelistEditBuffer = ConvertConfigToEditorText(_exeWhitelistConfig.Value);
                Logger.LogInfo("已恢复为当前配置内容");
            }

            if (UnityEngine.GUILayout.Button("选择 EXE 并追加", UnityEngine.GUILayout.Height(32f)))
            {
                AppendExeByFileDialog();
            }

            if (UnityEngine.GUILayout.Button("关闭", UnityEngine.GUILayout.Height(32f)))
            {
                _showWhitelistPanel = false;
            }
            UnityEngine.GUILayout.EndHorizontal();

            UnityEngine.GUILayout.EndArea();
        }

        private void AppendExeByFileDialog()
        {
            try
            {
                using (var vistaDialog = new Ookii.Dialogs.WinForms.VistaOpenFileDialog())
                {
                    vistaDialog.Title = "选择要加入白名单的 EXE";
                    vistaDialog.Filter = "Executable Files (*.exe)|*.exe";
                    vistaDialog.Multiselect = false;
                    vistaDialog.CheckFileExists = true;

                    var result = vistaDialog.ShowDialog();
                    if (result != System.Windows.Forms.DialogResult.OK)
                        return;

                    string selectedPath = vistaDialog.FileName;
                    string fileName = Path.GetFileName(selectedPath);
                    string exe = NormalizeExeName(fileName);
                    if (string.IsNullOrWhiteSpace(exe))
                        return;

                    var set = BuildExeWhitelist(_whitelistEditBuffer);
                    if (set.Add(exe))
                    {
                        _whitelistEditBuffer = string.Join(Environment.NewLine, set);
                        Logger.LogInfo($"已追加 EXE 到白名单编辑区: {exe}");
                    }
                    else
                    {
                        Logger.LogInfo($"EXE 已存在于白名单编辑区: {exe}");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"选择 EXE 失败: {e.GetType().Name} - {e.Message}");
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        private const uint GA_ROOT = 2;
        private const uint GA_ROOTOWNER = 3;
    }
}

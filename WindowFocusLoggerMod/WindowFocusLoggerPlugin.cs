using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Bootstrap;
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
        private ConfigEntry<string> _exeBlacklistConfig;
        private HashSet<string> _exeBlacklist;
        private ConfigEntry<bool> _useBlacklistModeConfig;
        private bool _showWhitelistPanel;
        private string _whitelistEditBuffer = string.Empty;
        private string _blacklistEditBuffer = string.Empty;
        private string _autoPromptEditBuffer = string.Empty;
        private string _cooldownEditBuffer = string.Empty;
        private UnityEngine.Vector2 _whitelistScroll;
        private UnityEngine.Vector2 _blacklistScroll;
        private UnityEngine.Vector2 _panelContentScroll;
        private UnityEngine.Rect _settingsPanelRect = new UnityEngine.Rect(16f, 120f, 760f, 780f);
        private bool _isResizingPanel;
        private UnityEngine.Vector2 _resizeStartMouse;
        private UnityEngine.Vector2 _resizeStartSize;
        private float _pomodoroCheckTimer;
        private bool _isPomodoroWorking;
        private bool _hasPomodoroState;
        private ConfigEntry<bool> _autoTriggerAiChatConfig;
        private ConfigEntry<float> _autoTriggerCooldownSecondsConfig;
        private ConfigEntry<string> _autoTriggerPromptTemplateConfig;
        private float _autoTriggerCooldownTimer;
        private string _lastTriggeredExe = string.Empty;
        private string _lastTriggeredTitle = string.Empty;

        // 轮询间隔，避免每帧都调用 Win32 API
        private const float PollIntervalSeconds = 0.15f;
        private const float PomodoroCheckIntervalSeconds = 0.5f;
        private const float MinPanelWidth = 560f;
        private const float MinPanelHeight = 420f;
        private const float PanelResizeHandleSize = 22f;

        private void Awake()
        {
            _exeWhitelistConfig = Config.Bind(
                "Filter",
                "ExeWhitelist",
                "chill with you.exe,explorer.exe,searchhost.exe,startmenuexperiencehost.exe,shellexperiencehost.exe,taskhostw.exe,dwm.exe,textinputhost.exe,lockapp.exe,applicationframehost.exe,ctfmon.exe,sihost.exe,runtimebroker.exe,systemsettings.exe,smartscreen.exe,taskmgr.exe,conhost.exe,msedge.exe,chrome.exe,firefox.exe,code.exe,devenv.exe,idea64.exe,pycharm64.exe,rider64.exe,webstorm64.exe,notepad++.exe,typora.exe,obsidian.exe,notion.exe,winword.exe,excel.exe,powerpnt.exe,outlook.exe,onenote.exe,wps.exe,wpscloudsvr.exe,et.exe,wpp.exe,pdfxedit.exe,acrord32.exe",
                "EXE 白名单。面板中按“每行一个 EXE”编辑；程序会自动兼容逗号/分号/换行分隔。"
            );

            _exeWhitelist = BuildExeWhitelist(_exeWhitelistConfig.Value);

            _exeBlacklistConfig = Config.Bind(
                "Filter",
                "ExeBlacklist",
                "steam.exe,epicgameslauncher.exe,riotclientservices.exe,leagueclient.exe,leagueclientux.exe,dota2.exe,cs2.exe,valorant-win64-shipping.exe,genshinimpact.exe,honkaistarrail.exe,zenlesszonezero.exe,vrc.exe,vrchat.exe,minecraft.exe,vlc.exe,potplayer64.exe,potplayermini64.exe,mpc-hc64.exe,kodi.exe,spotify.exe,cloudmusic.exe,qqmusic.exe,bilibili.exe,tencentvideo.exe,youku.exe,iqiyiapp.exe",
                "EXE 黑名单。仅在黑名单模式下生效，命中这些进程才会被识别为干扰。"
            );

            _exeBlacklist = BuildExeWhitelist(_exeBlacklistConfig.Value);

            _useBlacklistModeConfig = Config.Bind(
                "Filter",
                "UseBlacklistMode",
                true,
                "false=白名单模式（非白名单视为干扰）；true=黑名单模式（命中黑名单视为干扰）。"
            );

            _autoTriggerAiChatConfig = Config.Bind(
                "AIChat",
                "EnableAutoTriggerInPomodoro",
                true,
                "启用后：在“创作中”状态下，鼠标焦点切到非白名单窗口时，自动触发 AIChat 发送消息。"
            );

            _autoTriggerCooldownSecondsConfig = Config.Bind(
                "AIChat",
                "AutoTriggerCooldownSeconds",
                600f,
                "自动触发冷却时间（秒），避免短时间内频繁触发。"
            );

            _autoTriggerPromptTemplateConfig = Config.Bind(
                "AIChat",
                "AutoTriggerPromptTemplate",
                "我正在创作中，但注意力被外部窗口打断了。请用温柔、简短的话（1-2句）提醒我回到创作。当前干扰窗口：{title}（{exe}）。",
                "自动触发给 AIChat 的提示词模板，支持 {title} 和 {exe} 占位符。"
            );

            _exeWhitelistConfig.SettingChanged += (_, __) =>
            {
                _exeWhitelist = BuildExeWhitelist(_exeWhitelistConfig.Value);
                Logger.LogInfo($"EXE 白名单已更新，共 {_exeWhitelist.Count} 项");
            };

            _exeBlacklistConfig.SettingChanged += (_, __) =>
            {
                _exeBlacklist = BuildExeWhitelist(_exeBlacklistConfig.Value);
                Logger.LogInfo($"EXE 黑名单已更新，共 {_exeBlacklist.Count} 项");
            };

            Logger.LogInfo("Window Focus Logger 已加载");
            Logger.LogInfo($"EXE 白名单已加载，共 {_exeWhitelist.Count} 项");
            Logger.LogInfo($"EXE 黑名单已加载，共 {_exeBlacklist.Count} 项");
        }

        private void Update()
        {
            HandleHotkeys();
            CheckPomodoroStateByUiText();

            if (_autoTriggerCooldownTimer > 0f)
            {
                _autoTriggerCooldownTimer -= UnityEngine.Time.unscaledDeltaTime;
                if (_autoTriggerCooldownTimer < 0f)
                    _autoTriggerCooldownTimer = 0f;
            }

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
                    _blacklistEditBuffer = ConvertConfigToEditorText(_exeBlacklistConfig.Value);
                    _autoPromptEditBuffer = _autoTriggerPromptTemplateConfig.Value ?? string.Empty;
                    _cooldownEditBuffer = _autoTriggerCooldownSecondsConfig.Value.ToString("0.##");
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
            if (!ShouldTreatAsDistraction(exeName))
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

            string modeText = _useBlacklistModeConfig.Value ? "命中黑名单" : "非白名单";
            Logger.LogInfo(
                $"焦点窗口变更({modeText}) -> Title: \"{resolvedTitle}\", Exe: \"{exeName}\", Process: \"{processName}\" (PID: {foregroundPid}), MouseTopTitle: \"{mouseTitle}\", ForegroundTitle: \"{foregroundTitle}\", ChildHwnd: 0x{childHwnd.ToInt64():X}, MouseTopHwnd: 0x{mouseTopLevelHwnd.ToInt64():X}, ForegroundHwnd: 0x{foregroundHwnd.ToInt64():X}, FocusControlHwnd: 0x{focusControlHwnd.ToInt64():X}, MousePID: {mousePid}");

            TryTriggerAiChatInPomodoro(resolvedTitle, exeName);
        }

        private void TryTriggerAiChatInPomodoro(string windowTitle, string exeName)
        {
            if (!_autoTriggerAiChatConfig.Value)
                return;

            if (!_hasPomodoroState || !_isPomodoroWorking)
                return;

            if (_autoTriggerCooldownTimer > 0f)
                return;

            string normalizedExe = NormalizeExeName(exeName);
            string normalizedTitle = (windowTitle ?? string.Empty).Trim();
            if (normalizedExe == _lastTriggeredExe && string.Equals(normalizedTitle, _lastTriggeredTitle, StringComparison.Ordinal))
                return;

            var aiPlugin = FindAiChatPlugin();
            if (aiPlugin == null)
            {
                Logger.LogWarning("自动触发 AIChat 失败：未找到 AIChat 插件实例");
                return;
            }

            if (IsAiChatBusy(aiPlugin))
            {
                Logger.LogInfo("AIChat 正在处理上一条消息，本次自动触发已跳过");
                return;
            }

            MethodInfo routineMethod = aiPlugin.GetType().GetMethod("AIProcessRoutine", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (routineMethod == null)
            {
                Logger.LogWarning("自动触发 AIChat 失败：未找到 AIProcessRoutine 方法");
                return;
            }

            string prompt = BuildAutoPrompt(windowTitle, exeName);
            var routine = routineMethod.Invoke(aiPlugin, new object[] { prompt }) as System.Collections.IEnumerator;
            if (routine == null)
            {
                Logger.LogWarning("自动触发 AIChat 失败：AIProcessRoutine 返回为空");
                return;
            }

            (aiPlugin as UnityEngine.MonoBehaviour)?.StartCoroutine(routine);

            _lastTriggeredExe = normalizedExe;
            _lastTriggeredTitle = normalizedTitle;
            _autoTriggerCooldownTimer = Math.Max(0f, _autoTriggerCooldownSecondsConfig.Value);

            Logger.LogInfo($"已自动触发 AIChat：Title=\"{windowTitle}\", Exe=\"{exeName}\"");
        }

        private string BuildAutoPrompt(string windowTitle, string exeName)
        {
            string template = _autoTriggerPromptTemplateConfig.Value;
            if (string.IsNullOrWhiteSpace(template))
                template = "我正在创作中，被窗口 {title}（{exe}）分心了，请提醒我回到创作。";

            return template
                .Replace("{title}", string.IsNullOrWhiteSpace(windowTitle) ? "<无标题窗口>" : windowTitle)
                .Replace("{exe}", string.IsNullOrWhiteSpace(exeName) ? "unknown.exe" : exeName);
        }

        private static UnityEngine.Object FindAiChatPlugin()
        {
            try
            {
                foreach (var kv in Chainloader.PluginInfos)
                {
                    var info = kv.Value;
                    if (info == null || info.Instance == null)
                        continue;

                    string guid = info.Metadata?.GUID ?? string.Empty;
                    string name = info.Metadata?.Name ?? string.Empty;
                    string typeName = info.Instance.GetType().FullName ?? string.Empty;

                    bool isAiChat =
                        guid.IndexOf("chillaimod", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        guid.IndexOf("aichat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("aichat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("AIMod", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isAiChat)
                        return info.Instance;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static bool IsAiChatBusy(UnityEngine.Object aiPlugin)
        {
            if (aiPlugin == null)
                return false;

            try
            {
                FieldInfo busyField = aiPlugin.GetType().GetField("_isProcessing", BindingFlags.Instance | BindingFlags.NonPublic);
                if (busyField != null && busyField.FieldType == typeof(bool))
                {
                    return (bool)busyField.GetValue(aiPlugin);
                }
            }
            catch
            {
                // ignore
            }

            return false;
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

        private bool IsExeBlacklisted(string exeName)
        {
            if (_exeBlacklist == null || _exeBlacklist.Count == 0)
                return false;

            return _exeBlacklist.Contains(NormalizeExeName(exeName));
        }

        private bool ShouldTreatAsDistraction(string exeName)
        {
            if (_useBlacklistModeConfig.Value)
                return IsExeBlacklisted(exeName);

            return !IsExeWhitelisted(exeName);
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

            _settingsPanelRect.x = 16f;
            _settingsPanelRect.y = UnityEngine.Mathf.Clamp(_settingsPanelRect.y, 8f, UnityEngine.Mathf.Max(8f, UnityEngine.Screen.height - _settingsPanelRect.height - 8f));

            var currentEvent = UnityEngine.Event.current;
            var resizeHandleRect = new UnityEngine.Rect(
                _settingsPanelRect.xMax - PanelResizeHandleSize - 6f,
                _settingsPanelRect.yMax - PanelResizeHandleSize - 6f,
                PanelResizeHandleSize,
                PanelResizeHandleSize);

            if (currentEvent != null)
            {
                if (currentEvent.type == UnityEngine.EventType.MouseDown &&
                    currentEvent.button == 1 &&
                    currentEvent.control &&
                    resizeHandleRect.Contains(currentEvent.mousePosition))
                {
                    _isResizingPanel = true;
                    _resizeStartMouse = currentEvent.mousePosition;
                    _resizeStartSize = new UnityEngine.Vector2(_settingsPanelRect.width, _settingsPanelRect.height);
                    currentEvent.Use();
                }

                if (_isResizingPanel && currentEvent.button == 1 && currentEvent.type == UnityEngine.EventType.MouseDrag)
                {
                    var delta = currentEvent.mousePosition - _resizeStartMouse;
                    _settingsPanelRect.width = UnityEngine.Mathf.Clamp(_resizeStartSize.x + delta.x, MinPanelWidth, UnityEngine.Screen.width - 24f);
                    _settingsPanelRect.height = UnityEngine.Mathf.Clamp(_resizeStartSize.y + delta.y, MinPanelHeight, UnityEngine.Screen.height - 16f);
                    currentEvent.Use();
                }

                if (_isResizingPanel &&
                    (currentEvent.type == UnityEngine.EventType.MouseUp || currentEvent.rawType == UnityEngine.EventType.MouseUp) &&
                    currentEvent.button == 1)
                {
                    _isResizingPanel = false;
                    currentEvent.Use();
                }
            }

            UnityEngine.GUI.Box(_settingsPanelRect, "Window Focus Logger - 自动触发与白名单设置");

            UnityEngine.GUILayout.BeginArea(new UnityEngine.Rect(_settingsPanelRect.x + 12f, _settingsPanelRect.y + 32f, _settingsPanelRect.width - 24f, _settingsPanelRect.height - 44f));
            _panelContentScroll = UnityEngine.GUILayout.BeginScrollView(_panelContentScroll, UnityEngine.GUILayout.ExpandHeight(true));
            UnityEngine.GUILayout.Label("说明：白名单内进程不会输出焦点日志。每行一个 EXE，例如：explorer.exe");
            UnityEngine.GUILayout.Label("快捷键：F11 打开/关闭面板");
            UnityEngine.GUILayout.Label("面板缩放：按住 Ctrl + 鼠标右键，在右下角拖拽");
            UnityEngine.GUILayout.Space(8f);

            bool useBlacklistMode = UnityEngine.GUILayout.Toggle(
                _useBlacklistModeConfig.Value,
                "使用黑名单模式（命中黑名单才视为干扰；关闭则为白名单模式）");
            if (useBlacklistMode != _useBlacklistModeConfig.Value)
            {
                _useBlacklistModeConfig.Value = useBlacklistMode;
                _useBlacklistModeConfig.ConfigFile.Save();
                Logger.LogInfo($"过滤模式已更新：{(useBlacklistMode ? "黑名单" : "白名单")}");
            }

            UnityEngine.GUILayout.Label($"当前过滤模式：{(_useBlacklistModeConfig.Value ? "黑名单模式" : "白名单模式")}");
            UnityEngine.GUILayout.Space(6f);

            bool autoTriggerEnabled = UnityEngine.GUILayout.Toggle(_autoTriggerAiChatConfig.Value, "创作中且焦点位于非白名单时，自动触发 AIChat");
            if (autoTriggerEnabled != _autoTriggerAiChatConfig.Value)
            {
                _autoTriggerAiChatConfig.Value = autoTriggerEnabled;
                _autoTriggerAiChatConfig.ConfigFile.Save();
                Logger.LogInfo($"自动触发 AIChat 开关已更新: {autoTriggerEnabled}");
            }

            UnityEngine.GUILayout.Space(4f);
            UnityEngine.GUILayout.Label("自动触发提示词模板（支持 {title}、{exe} 占位符）:");
            _autoPromptEditBuffer = UnityEngine.GUILayout.TextArea(_autoPromptEditBuffer, UnityEngine.GUILayout.Height(72f));

            UnityEngine.GUILayout.Space(4f);
            UnityEngine.GUILayout.Label("冷却 CD（秒）: 默认 600");
            _cooldownEditBuffer = UnityEngine.GUILayout.TextField(_cooldownEditBuffer, UnityEngine.GUILayout.Height(24f));
            UnityEngine.GUILayout.Label($"当前生效冷却: {_autoTriggerCooldownSecondsConfig.Value:0.##} 秒");
            UnityEngine.GUILayout.Space(8f);

            UnityEngine.GUILayout.Label("白名单（白名单模式下：不视为干扰）");
            _whitelistScroll = UnityEngine.GUILayout.BeginScrollView(_whitelistScroll, UnityEngine.GUILayout.Height(170f));
            _whitelistEditBuffer = UnityEngine.GUILayout.TextArea(_whitelistEditBuffer, UnityEngine.GUILayout.ExpandHeight(true));
            UnityEngine.GUILayout.EndScrollView();

            UnityEngine.GUILayout.Space(6f);
            UnityEngine.GUILayout.Label("黑名单（黑名单模式下：视为干扰）");
            _blacklistScroll = UnityEngine.GUILayout.BeginScrollView(_blacklistScroll, UnityEngine.GUILayout.Height(170f));
            _blacklistEditBuffer = UnityEngine.GUILayout.TextArea(_blacklistEditBuffer, UnityEngine.GUILayout.ExpandHeight(true));
            UnityEngine.GUILayout.EndScrollView();

            UnityEngine.GUILayout.BeginHorizontal();
            if (UnityEngine.GUILayout.Button("保存并应用", UnityEngine.GUILayout.Height(32f)))
            {
                _exeWhitelistConfig.Value = ConvertEditorTextToConfig(_whitelistEditBuffer);
                _exeBlacklistConfig.Value = ConvertEditorTextToConfig(_blacklistEditBuffer);

                string promptTemplate = string.IsNullOrWhiteSpace(_autoPromptEditBuffer)
                    ? "我正在创作中，被窗口 {title}（{exe}）分心了，请提醒我回到创作。"
                    : _autoPromptEditBuffer.Trim();
                _autoTriggerPromptTemplateConfig.Value = promptTemplate;

                float cooldown = 600f;
                if (!float.TryParse(_cooldownEditBuffer, out cooldown) || cooldown < 0f)
                    cooldown = 600f;
                _autoTriggerCooldownSecondsConfig.Value = cooldown;
                _cooldownEditBuffer = cooldown.ToString("0.##");
                _autoTriggerCooldownTimer = 0f;

                _exeWhitelistConfig.ConfigFile.Save();
                Logger.LogInfo($"配置已保存并应用：模式 {(_useBlacklistModeConfig.Value ? "黑名单" : "白名单")}, 白名单 {_exeWhitelist.Count} 项, 黑名单 {_exeBlacklist.Count} 项, 冷却 {cooldown:0.##} 秒，当前冷却已重置");
            }

            if (UnityEngine.GUILayout.Button("恢复当前配置", UnityEngine.GUILayout.Height(32f)))
            {
                _whitelistEditBuffer = ConvertConfigToEditorText(_exeWhitelistConfig.Value);
                _blacklistEditBuffer = ConvertConfigToEditorText(_exeBlacklistConfig.Value);
                _autoPromptEditBuffer = _autoTriggerPromptTemplateConfig.Value ?? string.Empty;
                _cooldownEditBuffer = _autoTriggerCooldownSecondsConfig.Value.ToString("0.##");
                Logger.LogInfo("已恢复为当前配置内容");
            }

            if (UnityEngine.GUILayout.Button("选择 EXE 追加到白名单", UnityEngine.GUILayout.Height(32f)))
            {
                AppendExeByFileDialog(false);
            }

            if (UnityEngine.GUILayout.Button("选择 EXE 追加到黑名单", UnityEngine.GUILayout.Height(32f)))
            {
                AppendExeByFileDialog(true);
            }

            if (UnityEngine.GUILayout.Button("关闭", UnityEngine.GUILayout.Height(32f)))
            {
                _showWhitelistPanel = false;
            }
            UnityEngine.GUILayout.EndHorizontal();

            UnityEngine.GUILayout.EndScrollView();
            UnityEngine.GUILayout.EndArea();

            UnityEngine.GUI.Box(resizeHandleRect, "⇲");
        }

        private void AppendExeByFileDialog(bool appendToBlacklist)
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

                    var set = BuildExeWhitelist(appendToBlacklist ? _blacklistEditBuffer : _whitelistEditBuffer);
                    if (set.Add(exe))
                    {
                        if (appendToBlacklist)
                        {
                            _blacklistEditBuffer = string.Join(Environment.NewLine, set);
                            Logger.LogInfo($"已追加 EXE 到黑名单编辑区: {exe}");
                        }
                        else
                        {
                            _whitelistEditBuffer = string.Join(Environment.NewLine, set);
                            Logger.LogInfo($"已追加 EXE 到白名单编辑区: {exe}");
                        }
                    }
                    else
                    {
                        Logger.LogInfo($"EXE 已存在于{(appendToBlacklist ? "黑名单" : "白名单")}编辑区: {exe}");
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

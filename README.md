# WindowFocusLoggerMod

> **Chill with You Lo-Fi Story** 的 BepInEx 插件，实时监测鼠标焦点窗口，在"创作中"状态下自动调用 AIChat 发送语音提醒，帮助你保持专注。

---

## ✨ 核心功能

| 功能                      | 说明                                                                                |
| ------------------------- | ----------------------------------------------------------------------------------- |
| **焦点窗口检测**          | 鼠标命中窗口 + 前台窗口联合解析，精准识别当前活动程序                               |
| **白名单 / 黑名单双模式** | 白名单模式：非白名单视为干扰；黑名单模式（默认）：仅命中黑名单视为干扰              |
| **番茄钟状态探测**        | 通过 UI 文本自动检测"创作中"状态，无需手动切换                                      |
| **自动触发 AIChat**       | 创作中 + 干扰窗口 → 跨插件反射调用 AIChat 协程，生成语音提醒（不修改 `AIChat.dll`） |
| **冷却与去重**            | 默认 600 秒冷却 + 相同窗口去重 + AIChat 忙碌检测，避免刷屏                          |
| **游戏内设置面板**        | F11 打开，可编辑白名单/黑名单、提示词模板、冷却时间、模式切换                       |
| **面板可缩放**            | Ctrl + 鼠标右键拖拽右下角 ⇲ 调整面板大小，整体内容可滚动                            |

---

## 📁 目录结构

```
Windowfocus/
├── README.md                          # 本文档
└── WindowFocusLoggerMod/
    ├── WindowFocusLoggerPlugin.cs      # 主逻辑源码
    ├── WindowFocusLoggerMod.csproj     # 项目文件 (net472)
    ├── bin/Release/net472/             # 构建产物
    │   └── WindowFocusLoggerMod.dll
    ├── dist/                           # 打包产物 (zip)
    └── obj/                            # 中间文件
```

---

## 🔧 构建

### 环境要求

- **OS**：Windows 10 / 11
- **SDK**：.NET 8.x（构建目标为 `net472`）
- **游戏目录**：需要引用其中的 `BepInEx`、`UnityEngine`、`Ookii.Dialogs` 等 DLL

### 构建命令

```bat
cd E:\PyCode\GoToDo\Windowfocus

dotnet build .\WindowFocusLoggerMod\WindowFocusLoggerMod.csproj -c Release ^
  /p:GameRootDir="E:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story"
```

> 若游戏安装路径不同，请修改 `GameRootDir` 参数。

构建产物：`WindowFocusLoggerMod\bin\Release\net472\WindowFocusLoggerMod.dll`

---

## 🚀 部署

将 DLL 复制到游戏 BepInEx 插件目录：

```
<游戏目录>\BepInEx\plugins\WindowFocusLoggerMod.dll
```

> ⚠️ 如遇复制失败，通常是游戏正在运行占用 DLL，请先关闭游戏。

---

## ⚙️ 配置项

配置文件位置：`<游戏目录>\BepInEx\config\com.yourname.windowfocuslogger.cfg`

### Filter 分组

| 配置项             | 默认值                     | 说明                                      |
| ------------------ | -------------------------- | ----------------------------------------- |
| `UseBlacklistMode` | `true`                     | `false` = 白名单模式；`true` = 黑名单模式 |
| `ExeWhitelist`     | 系统进程 + 常见工作软件    | 白名单模式下不视为干扰的 EXE 列表         |
| `ExeBlacklist`     | 常见游戏 + 影视 + 音乐软件 | 黑名单模式下视为干扰的 EXE 列表           |

### AIChat 分组

| 配置项                        | 默认值                                      | 说明                                                  |
| ----------------------------- | ------------------------------------------- | ----------------------------------------------------- |
| `EnableAutoTriggerInPomodoro` | `true`                                      | 是否在创作中状态启用自动触发                          |
| `AutoTriggerCooldownSeconds`  | `600`                                       | 触发冷却时间（秒）                                    |
| `AutoTriggerPromptTemplate`   | `我正在创作中，但注意力被外部窗口打断了...` | 发送给 LLM 的提示词，支持 `{title}` 和 `{exe}` 占位符 |

> 所有配置项均可在游戏内 F11 面板中实时修改并保存。

---

## 🎮 游戏内操作

| 操作                         | 说明                                                      |
| ---------------------------- | --------------------------------------------------------- |
| **F11**                      | 打开/关闭设置面板                                         |
| **Ctrl + 右键拖拽**          | 在面板右下角 ⇲ 区域拖拽可调整面板大小                     |
| **保存并应用**               | 保存白名单/黑名单/提示词/冷却配置，同时重置当前冷却计时器 |
| **恢复当前配置**             | 将面板编辑区恢复为���前已保存的配置内容                   |
| **选择 EXE 追加到白/黑名单** | 弹出文件选择器，选中 EXE 后自动追加到对应列表             |

---

## 🏗️ 技术实现

- **跨插件调用**：通过 `BepInEx.Bootstrap.Chainloader.PluginInfos` 发现 AIChat 插件实例
- **反射调用**：调用 AIChat 内部 `AIProcessRoutine(string prompt)` 协程与 `_isProcessing` 字段
- **焦点检测**：Win32 API（`WindowFromPoint`、`GetForegroundWindow`、`GetAncestor`）
- **番茄钟检测**：遍历 `TMPro.TextMeshProUGUI` / `UnityEngine.UI.Text` 组件搜索"创作中"关键词

---

## ⚠️ 已知限制

1. 反射调用依赖 AIChat 内部成员名（`AIProcessRoutine`、`_isProcessing`）；若上游重命名需同步适配
2. "创作中"识别依赖 UI 文本包含该关键词；若游戏文本变更需更新匹配策略
3. EXE 名称匹配为精确匹配（不区分大小写），不支持通配符

---

## 📄 License

本项目仅供学习交流使用。

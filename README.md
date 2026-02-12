# WindowFocusLoggerMod（Windowfocus）

该目录是 `WindowFocusLoggerMod` 的**源码主目录**（已从游戏安装目录迁出），用于开发、构建和打包。  
目标：在不改动 `AIChat.dll` 的前提下，实现“创作中状态 + 非白名单窗口”自动触发 AI 语音提醒。

---

## 目录结构

- `WindowFocusLoggerMod/`
  - `WindowFocusLoggerPlugin.cs`：主逻辑（焦点检测、白名单、番茄钟检测、AIChat 反射触发）
  - `WindowFocusLoggerMod.csproj`：项目文件（`net472`）
  - `bin/`：构建产物
  - `dist/`：打包产物（zip）
  - `obj/`：中间文件
- `README.md`：本文档

---

## 核心功能

1. 焦点窗口检测（鼠标命中窗口 + 前台窗口联合解析）
2. EXE 白名单过滤（仅处理非白名单目标）
3. F11 游戏内面板管理白名单（逐行编辑 + 文件选择器追加）
4. UI 文本探测“创作中”状态
5. 自动触发 AIChat：
   - 条件：处于“创作中”且切换到非白名单窗口
   - 方式：跨插件反射调用 `AIChat` 内部协程（不修改 `AIChat.dll`）
   - 具备冷却与重复去重，避免刷屏

---

## 构建环境

- OS：Windows 10/11
- SDK：.NET 8.x（用于构建 `net472` 项目）
- 游戏目录（默认）：
  - `E:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story`

> 项目依赖游戏目录中的 `BepInEx` 与 `UnityEngine` DLL。  
> 若你的游戏路径不同，请在构建时传入 `GameRootDir`。

---

## 构建命令

在 `E:\PyCode\GoToDo\Windowfocus` 下执行：

```bat
dotnet build .\WindowFocusLoggerMod\WindowFocusLoggerMod.csproj -c Release /p:GameRootDir="E:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story"
```

构建输出：

- `WindowFocusLoggerMod\bin\Release\net472\WindowFocusLoggerMod.dll`

---

## 部署到游戏

将 DLL 复制到：

- `E:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story\BepInEx\plugins\WindowFocusLoggerMod.dll`

如遇复制失败，通常是游戏占用 DLL，先关闭游戏再复制。

---

## 关键配置（BepInEx 配置文件）

配置文件：

- `BepInEx\config\com.yourname.windowfocuslogger.cfg`

重点项：

- `Filter.ExeWhitelist`：白名单
- `AIChat.EnableAutoTriggerInPomodoro`：是否启用自动触发
- `AIChat.AutoTriggerCooldownSeconds`：触发冷却秒数
- `AIChat.AutoTriggerPromptTemplate`：自动触发提示词模板（支持 `{title}`、`{exe}`）

---

## 已知限制

1. 反射调用依赖 `AIChat` 内部成员名；若上游改名，自动触发需同步适配。
2. “创作中”识别依赖 UI 文本包含该关键词；若游戏文本变更需更新匹配策略。

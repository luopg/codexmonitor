# Codex Monitor

[![Build](https://github.com/luopg/codexmonitor/actions/workflows/build.yml/badge.svg)](https://github.com/luopg/codexmonitor/actions/workflows/build.yml)

Codex Monitor 是一个 Windows 托盘应用，用来查看本机正在运行的 Codex 任务、按工作目录统计项目数量，并在任务完成时发出提醒。它还能显示本机 Codex 已记录的官方 OpenAI 套餐限额；安装 [CCSwitch](https://github.com/farion1231/cc-switch) 后，也可以按配置查询第三方供应商的 API 余额。

界面使用中文，程序仅支持 Windows x64。

## 功能

- 约每 1.2 秒刷新任务标题、工作目录和运行时间。
- 从 Codex 状态库与 rollout JSONL 日志恢复任务状态，并以有界分块方式读取新增内容。
- 在任务完成，或运行中任务数降到设定阈值时播放提示音、发送桌面通知并弹出窗口。
- 支持系统托盘、开机自启、窗口置顶和窄窗模式。
- 每 30 秒刷新一次用量信息；官方 OpenAI 套餐显示剩余百分比、限额窗口和重置时间，第三方供应商显示其余额接口返回的数据。
- 提供命令行诊断入口，便于检查任务状态、本机限额快照、第三方余额连接和提示音。

## 下载与运行

1. 打开 [GitHub Actions](https://github.com/luopg/codexmonitor/actions/workflows/build.yml)，选择最近一次成功的 `Build`。
2. 在该次运行的 **Artifacts** 区域下载 `CodexMonitor-win-x64`。
3. 解压后运行 `CodexMonitor.exe`。

构建产物包含运行时，无需另行安装 .NET。关闭主窗口后，程序会继续留在系统托盘；右键托盘图标可以修改提醒设置或退出。

提醒阈值只在任务数从阈值以上降到阈值以内时触发。例如阈值设为 5 时，从 6 个任务降到 5 个会提醒；程序启动时已经不超过 5 个任务则不会立即提醒。

## 数据来源与隐私

Codex Monitor 在本机读取以下数据：

- `%CODEX_HOME%\state_5.sqlite`；没有设置 `CODEX_HOME` 时读取 `%USERPROFILE%\.codex\state_5.sqlite`。
- 状态库中记录的 Codex rollout JSONL 文件，包括任务事件和官方套餐限额快照。
- 可选的 `%USERPROFILE%\.cc-switch\cc-switch.db`，用于识别当前供应商及读取第三方余额配置。

程序以只读方式打开 Codex 和 CCSwitch 数据库。

检测到 `OpenAI Official` 套餐限额时，Codex Monitor 只读取本机 Codex rollout 日志中已经记录的限额快照，从中计算并显示剩余百分比、主/次限额窗口及其重置时间。这条路径不读取 OAuth Token，也不会请求 OpenAI 私有接口。若近期 rollout 中没有可用且尚未过期的限额快照，界面会显示“不可查询”；任务监控功能不受影响。

第三方供应商需要在 CCSwitch 中启用余额查询并提供相应的接口配置。查询时，Codex Monitor 会临时读取当前供应商的 API Key，并将其作为 Bearer Header 发往该供应商配置的 HTTPS 地址或本机回环地址。程序不显示 API Key，也不会把 Key 写入自己的设置或日志。

个人设置保存在 `%APPDATA%\CodexMonitor\settings.ini`。

## 诊断命令

```powershell
CodexMonitor.exe --diagnostic-snapshot snapshot.json
CodexMonitor.exe --diagnostic-balance balance.json
CodexMonitor.exe --test-sound
```

`--diagnostic-snapshot` 输出当前任务快照；`--diagnostic-balance` 输出当前供应商的用量诊断结果，官方 OpenAI 套餐检查本机 rollout 限额快照，第三方供应商检查 CCSwitch 余额配置与接口；`--test-sound` 播放一次完成提示音。

任务快照包含任务标题、Thread ID 和完整工作目录；用量诊断包含供应商、套餐、限额窗口或余额结果，但不包含 API Key 或 OAuth Token。提交 Issue 或把诊断文件发给他人前，请先删除不希望公开的信息。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK。

```powershell
dotnet restore CodexMonitor.sln --locked-mode
dotnet build CodexMonitor.sln --configuration Release --no-restore
dotnet test CodexMonitor.sln --configuration Release --no-build
```

创建自包含发布包：

```powershell
./scripts/publish.ps1
```

脚本会把文件写入 `artifacts/win-x64`，然后在隔离的空 `CODEX_HOME` 中运行一次诊断冒烟测试。GitHub Actions 使用同一套还原、格式检查、构建、测试和发布流程。

## 项目结构

```text
src/CodexMonitor/          WinForms 应用源码
tests/CodexMonitor.Tests/  SQLite 与 JSONL 集成测试
scripts/publish.ps1        Windows x64 发布与冒烟测试
.github/workflows/         GitHub Actions 构建流程
```

如果你遇到状态识别错误或余额接口兼容问题，请提交 Issue，并附上复现步骤和已脱敏的诊断结果。

## 许可

本仓库目前没有为项目源码授予许可证。公开可见不代表允许复制、修改或分发源码。自包含构建中使用的第三方组件及其许可信息见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

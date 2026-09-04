# Codex Monitor

[![Build](https://github.com/luopg/codexmonitor/actions/workflows/build.yml/badge.svg)](https://github.com/luopg/codexmonitor/actions/workflows/build.yml)

Codex Monitor 是一个 Windows 托盘应用，用来查看本机正在运行的 Codex 任务、按工作目录统计项目数量，并在任务完成时发出提醒。安装了 [CCSwitch](https://github.com/farion1231/cc-switch) 的用户还可以在同一个窗口查看当前 Codex 供应商的 API 余额。

界面使用中文，程序仅支持 Windows x64。

## 功能

- 约每 1.2 秒刷新任务标题、工作目录和运行时间。
- 从 Codex 状态库与 rollout JSONL 日志恢复任务状态，并以有界分块方式读取新增内容。
- 在任务完成，或运行中任务数降到设定阈值时播放提示音、发送桌面通知并弹出窗口。
- 支持系统托盘、开机自启、窗口置顶和窄窗模式。
- 每 30 秒读取一次 CCSwitch 余额；没有安装或配置 CCSwitch 时，其余功能仍可使用。
- 提供命令行诊断入口，便于检查状态读取、余额连接和提示音。

## 下载与运行

1. 打开 [GitHub Actions](https://github.com/luopg/codexmonitor/actions/workflows/build.yml)，选择最近一次成功的 `Build`。
2. 在该次运行的 **Artifacts** 区域下载 `CodexMonitor-win-x64`。
3. 解压后运行 `CodexMonitor.exe`。

构建产物包含运行时，无需另行安装 .NET。关闭主窗口后，程序会继续留在系统托盘；右键托盘图标可以修改提醒设置或退出。

提醒阈值只在任务数从阈值以上降到阈值以内时触发。例如阈值设为 5 时，从 6 个任务降到 5 个会提醒；程序启动时已经不超过 5 个任务则不会立即提醒。

## 数据来源与隐私

Codex Monitor 在本机读取以下数据：

- `%CODEX_HOME%\state_5.sqlite`；没有设置 `CODEX_HOME` 时读取 `%USERPROFILE%\.codex\state_5.sqlite`。
- 状态库中记录的 Codex rollout JSONL 文件。
- 可选的 `%USERPROFILE%\.cc-switch\cc-switch.db`。

程序以只读方式打开 Codex 和 CCSwitch 数据库。查询余额时，它会把 CCSwitch 中当前供应商的 API Key 作为 Bearer Header 发往该供应商配置的 HTTPS 地址或本机回环地址。Codex Monitor 不显示 API Key，也不会把 Key 写入自己的设置或日志。

个人设置保存在 `%APPDATA%\CodexMonitor\settings.ini`。

## 诊断命令

```powershell
CodexMonitor.exe --diagnostic-snapshot snapshot.json
CodexMonitor.exe --diagnostic-balance balance.json
CodexMonitor.exe --test-sound
```

`--diagnostic-snapshot` 输出当前任务快照，`--diagnostic-balance` 检查 CCSwitch 余额配置，`--test-sound` 播放一次完成提示音。

任务快照包含任务标题、Thread ID 和完整工作目录。提交 Issue 或把诊断文件发给他人前，请先删除不希望公开的信息。

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

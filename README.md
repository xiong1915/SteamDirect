# SteamDirect

Windows 游戏网络助手，提供直连切换、Steam / CS2 网络诊断和自动游戏网络历史报告。

## 下载

[**下载 Windows x64 版 SteamDirect.exe**](https://github.com/xiong1915/SteamDirect/releases/latest/download/SteamDirect.exe)

[查看所有发布版本](https://github.com/xiong1915/SteamDirect/releases)

支持 Windows 10 / 11 x64，需要 .NET Framework 4.8。下载后将程序放在固定目录运行。

## 功能

- 关闭 FlClash 及识别到的相关应用，检查本机代理状态后重启 Steam。
- 保存原代理设置，并在本地手动代理端口就绪后恢复。
- 分项检查本地网络、DNS、代理 / VPN、Steam 服务与游戏连接。
- 自动识别 Steam 游戏启动与退出，保存每局网络记录。
- 生成简明报告，说明异常线索、可能位置和处理建议。
- 每条记录提供详细统计、原始采样、异常时间线和 CSV 导出。
- 支持托盘、开机自启、夜间模式、自定义主题色和日志管理。
- 可清理符合条件的过期临时文件。

## 使用

1. 启动程序，在设置中选择外观、托盘和日志保留天数。
2. 需要切换直连时，先退出游戏并保存其他应用正在编辑的内容，再执行概览页的一键操作。
3. 网络卡顿时打开“网络诊断”。游戏结束后，在“游戏记录”查看报告，点击记录后的“详细数据”查看采样。
4. 需要恢复代理时先打开原代理软件，等本地端口就绪，再点击“恢复原代理设置”。

自动直连流程会关闭 FlClash 以及识别到的 ChatGPT、Antigravity、YouTube 独立应用。检测不通过时流程会停止，原因显示在界面和日志中。

本机检查无法保证排除运营商透明代理或所有未知网络中间层。网络历史中的延迟、超时比例和抖动来自主动探测，不能当作游戏服务器的实际延迟或丢包率。报告按已有证据描述问题，证据不足时会明确说明。

## 本地数据

- 设置、代理恢复备份和游戏历史保存在 `%LOCALAPPDATA%\SteamDirect\`。
- 当前版本日志目录固定为 `D:\SteamDirect\Logs`，需要可写的 D 盘。设置中可选择保留天数并清理过期日志。
- 游戏监控默认低频采样，高负载时降低频率；游戏运行时暂停报告计算和详细数据读取。

## 从源码构建

在 Windows x64 上，使用 PowerShell 在仓库目录执行：

```powershell
.\Build.ps1
```

程序输出到 `dist\SteamDirect.exe`。源码位于 `src`，使用 Windows 自带的 .NET Framework C# 编译器，无需安装第三方包。

界面参考 FlClash 的布局风格，程序和网络操作逻辑为独立实现。

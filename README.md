# ShortcutWheel 快捷转盘

CSGO 风格的 Windows 径向快捷启动器。按下热键，从转盘中选择项目，即可启动。

A CSGO-style radial shortcut launcher for Windows.

## 功能 / Features

- **CSGO 风格转盘** — 深色扇区、金色描边、数字编号、流畅动画
- **热键唤起** — 默认 `Ctrl+Alt+Space`；也支持鼠标侧键长按
- **拖拽添加** — 从资源管理器拖入 `.exe` / `.lnk` / URL 即可添加快捷方式
- **自动解析 `.lnk`** — 快捷方式文件自动解析为真实目标路径 + 参数
- **URI / 协议支持** — `steam://`、`https://`、`ms-settings:`、`spotify:`、`discord://` 等
- **文件夹 / 子菜单** — 支持任意深度嵌套；点击进入，Backspace 返回
- **分页** — 每页最多 6 个项目；滚轮或方向键翻页
- **配置界面** — 深色主题配置窗口，支持拖拽排序
- **系统托盘** — 静默驻留托盘；启动时显示 Toast 通知
- **单文件 exe** — 无需安装，仅依赖 .NET 9 运行时

## 环境要求 / Requirements

- Windows 10 / 11 (x64)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

## 快速开始 / Quick Start

1. 运行 `ShortcutWheel.exe`
2. 右下角出现金色 Toast 通知
3. 按 **Ctrl+Alt+Space** 呼出转盘
4. 将 `.exe`、`.lnk` 或 URL 拖入扇区即可添加快捷方式
5. 右键托盘图标 → **配置** 管理快捷方式与设置

## 默认快捷键 / Hotkeys

| 操作 | 按键 |
|---|---|
| 打开 / 关闭转盘 | `Ctrl+Alt+Space` |
| 打开转盘（长按） | 鼠标侧键 `XButton1` |
| 关闭转盘 | `Esc` |
| 返回上级 | `Backspace` / `Shift` / 右键 |
| 下一页 | 滚轮↓ / `→` / `↓` / `PageDown` |
| 上一页 | 滚轮↑ / `←` / `↑` / `PageUp` |

## 配置文件位置 / Config Location

- 便携模式：exe 同目录下 `Config\shortcuts.json`
- 用户模式：`%APPDATA%\ShortcutWheel\shortcuts.json`

## 构建 / Build

```bash
cd ShortcutWheel
dotnet build -c Release

# 发布单文件 / Publish single-file
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## License

MIT

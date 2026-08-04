# Paste Image to File Explorer

让截图或其他复制到 Windows 剪贴板中的图片，可以直接在资源管理器中按 `Ctrl+V` 粘贴为 PNG 文件。

## 它解决什么问题？

截图工具通常复制的是 `Bitmap`、`PNG` 等图片数据；这些数据可以粘贴到聊天框、文档或图片编辑器，但资源管理器只接受“文件列表”，所以无法把图片直接粘贴进文件夹。

本工具在后台监听剪贴板：当检测到图片时，自动写入临时缓存，并将剪贴板转换为资源管理器可识别的文件格式。之后只需在目标文件夹按 `Ctrl+V`。

## 使用方法

1. 下载或克隆本仓库。
2. 双击 `outputs/Start-PasteImageToExplorer.vbs` 启动后台助手。
3. 用 Snipaste、Windows 截图工具或任意截图工具复制图片。
4. 打开目标文件夹，按 `Ctrl+V`。

图片会作为 PNG 创建在目标文件夹中。中间文件保存在 `%TEMP%\ExplorerClipboardImages`，工具会自动清理一天前的缓存。

## 注意事项

- 工具运行期间，复制图片后约 0.25 秒会将剪贴板改为“文件”格式。若你要把原始位图直接粘贴到聊天框或绘图软件，请先粘贴，再重新复制截图。
- 程序显示在 Windows 通知区域。右键图标并选择 **Exit** 可停止它。
- 重启 Windows 后需要再次运行启动器；可将该 `.vbs` 文件的快捷方式放进“启动”文件夹，实现登录后自动启动。

## 文件说明

- `outputs/PasteImageToExplorer.ps1`：后台监听与图片转换逻辑。
- `outputs/Start-PasteImageToExplorer.vbs`：无控制台窗口的启动器。

## 适用环境

- Windows 10 / Windows 11
- Windows PowerShell 5.1（系统内置）

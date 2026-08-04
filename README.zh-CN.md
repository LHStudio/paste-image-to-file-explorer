# Paste Image to File Explorer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://www.microsoft.com/windows)
[![PowerShell](https://img.shields.io/badge/Language-PowerShell-5391FE.svg)](https://learn.microsoft.com/powershell)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-brightgreen.svg)](https://github.com/LHStudio/paste-image-to-file-explorer/pulls)
[![Maintained](https://img.shields.io/badge/Maintained-YES-2ea44f.svg)](https://github.com/LHStudio/paste-image-to-file-explorer)

**[English](README.md) | 简体中文**

一个轻量级 Windows 后台工具，让截图或其他复制到剪贴板的图片，可以直接在文件资源管理器中按 `Ctrl+V` 粘贴为 PNG 文件。

> ⚠️ **声明**：本工具仅供学习与个人使用，请合理使用。

---

## ✨ 功能特性

- 📋 **剪贴板图片 → 文件** — 自动将剪贴板中的图片转换为文件资源管理器可识别的文件格式
- 📁 **随处粘贴为 PNG** — 在任何文件夹中按 `Ctrl+V` 即可将截图保存为 `.png` 文件
- 🖼️ **兼容任意截图工具** — Snipaste、Windows 截图工具、PrintScreen 等均可
- 🔄 **后台监听** — 每 250ms 监听剪贴板，无窗口无控制台
- 🗑️ **自动清理** — 自动删除 1 天前的缓存图片
- 🖥️ **系统托盘** — 小巧的托盘图标，带 **Exit** 菜单项

## 🎯 它解决什么问题？

截图工具通常复制的是**图片数据**（位图/PNG）；这些数据可以粘贴到聊天框、文档或图片编辑器，但**文件资源管理器只接受文件列表**，所以无法把图片直接粘贴进文件夹。

本工具正好填补这个缺口：它在后台监听剪贴板，检测到图片时自动保存到临时缓存，并把剪贴板重写为资源管理器可识别的**文件拖放格式**。之后只需在目标文件夹按 `Ctrl+V`。

## 🚀 快速开始

1. 下载或克隆本仓库。
2. 双击 `outputs/Start-PasteImageToExplorer.vbs` 启动后台助手（无控制台窗口）。
3. 用 Snipaste、Windows 截图工具或任意截图工具复制图片。
4. 打开目标文件夹，按 `Ctrl+V`。

图片会作为 PNG 文件保存在目标文件夹中。中间文件存放在 `%TEMP%\ExplorerClipboardImages`，一天后自动清理。

## 🔧 使用说明

- 工具运行期间，复制图片后约 **0.25 秒**会将剪贴板改为「文件」格式。若你想把原始位图直接粘贴到聊天框或绘图软件，请**先粘贴**，再重新复制截图。
- 程序显示在**系统通知区域**。右键图标选择 **Exit** 可停止。
- 重启 Windows 后需再次运行启动器；可将该 `.vbs` 文件的快捷方式放入**启动**文件夹，实现登录后自动启动。

## 📁 文件说明

| 文件 | 说明 |
|------|------|
| `outputs/PasteImageToExplorer.ps1` | 后台剪贴板监听与图片转换逻辑 |
| `outputs/Start-PasteImageToExplorer.vbs` | 无控制台窗口的启动器 |

## 💻 适用环境

- Windows 10 / Windows 11
- Windows PowerShell 5.1（系统内置）

## 🔒 隐私说明

- 全部在**本地**运行 — 无网络访问，数据不会离开你的电脑
- 临时图片仅存放在本地临时文件夹，并会自动删除

## 📝 许可证

基于 [MIT License](LICENSE) 发布。

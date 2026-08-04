# Paste Image to File Explorer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://www.microsoft.com/windows)
[![PowerShell](https://img.shields.io/badge/Language-PowerShell-5391FE.svg)](https://learn.microsoft.com/powershell)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-brightgreen.svg)](https://github.com/LHStudio/paste-image-to-file-explorer/pulls)
[![Maintained](https://img.shields.io/badge/Maintained-YES-2ea44f.svg)](https://github.com/LHStudio/paste-image-to-file-explorer)

**English | [简体中文](README.zh-CN.md)**

A lightweight Windows background tool that lets you paste screenshots directly into **File Explorer** with `Ctrl+V` as PNG files.

> ⚠️ **Disclaimer**: This tool is provided for learning and personal use. Use it responsibly.

---

## ✨ Features

- 📋 **Clipboard image → file** — automatically converts clipboard images into files File Explorer understands
- 📁 **Paste as PNG anywhere** — press `Ctrl+V` in any folder to save the screenshot as a `.png` file
- 🖼️ **Works with any screenshot tool** — Snipaste, Windows Snipping Tool, PrintScreen, etc.
- 🔄 **Background monitoring** — watches the clipboard every 250 ms, no window or console
- 🗑️ **Auto cleanup** — removes cached images older than 1 day
- 🖥️ **System tray** — small tray icon with an **Exit** menu item

## 🎯 What Problem Does It Solve?

Screenshot tools usually copy **image data** (bitmap/PNG) to the clipboard. You can paste that into chat boxes, documents, or image editors — but **File Explorer only accepts file lists**, so pasting an image directly into a folder doesn't work.

This tool bridges the gap: it monitors the clipboard in the background, saves detected images to a temporary cache, and rewrites the clipboard into the **file drop format** that File Explorer recognizes. After that, just press `Ctrl+V` in the target folder.

## 🚀 Getting Started

1. Download or clone this repository.
2. Double-click `outputs/Start-PasteImageToExplorer.vbs` to launch the background helper (no console window).
3. Copy an image with Snipaste, Windows Snipping Tool, or any screenshot tool.
4. Open the target folder and press `Ctrl+V`.

The image is saved as a PNG file in the target folder. Intermediate files live in `%TEMP%\ExplorerClipboardImages` and are auto-cleaned after one day.

## 🔧 Usage Notes

- While the tool is running, about **0.25 s** after copying an image the clipboard changes to the *file* format. If you want to paste the raw bitmap into a chat box or drawing app, paste it **first**, then re-copy the screenshot.
- The program sits in the **system tray**. Right-click the icon and choose **Exit** to stop it.
- After restarting Windows, run the launcher again. You can place a shortcut to the `.vbs` file in the **Startup** folder for auto-start at login.

## 📁 Files

| File | Description |
|------|-------------|
| `outputs/PasteImageToExplorer.ps1` | Background clipboard monitoring & image conversion logic |
| `outputs/Start-PasteImageToExplorer.vbs` | Console-free launcher |

## 💻 Requirements

- Windows 10 / Windows 11
- Windows PowerShell 5.1 (built into Windows)

## 🔒 Privacy

- Everything runs **locally** — no network access, no data leaves your machine.
- Temporary images are stored only in your local temp folder and deleted automatically.

## 📝 License

Released under the [MIT License](LICENSE).

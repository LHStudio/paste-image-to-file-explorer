// PasteImageToExplorer —— 剪贴板图片转文件工具（托盘程序）
//
// 功能：监视剪贴板，当出现图像（截图 / 复制图片）时自动保存为 PNG 文件，
//       并把文件列表写回剪贴板——在资源管理器 / 桌面按 Ctrl+V 即可直接
//       粘贴为图片文件；同时（可选）在剪贴板保留原图像，聊天窗口、文档
//       等场合仍可正常粘贴图片。
//
// 目标框架：.NET Framework 4.x（Windows 10/11 自带，无需安装运行时）
// 编译方式：运行目录下的 build.cmd

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// 程序集特性：csc 会据此自动生成 exe 右键“属性 → 详细信息”中的版本信息
[assembly: AssemblyTitle("剪贴板贴图")]
[assembly: AssemblyDescription("剪贴板图片转文件工具：截图后自动保存为 PNG，在资源管理器中按 Ctrl+V 即可粘贴为文件。作者：LHStudio")]
[assembly: AssemblyProduct("剪贴板贴图 PasteImageToExplorer")]
[assembly: AssemblyCompany("LHStudio")]
[assembly: AssemblyCopyright("Copyright (C) LHStudio 2026")]
[assembly: AssemblyTrademark("LHStudio")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

namespace PasteImageToExplorer
{
    internal static class Program
    {
        internal const string AppName = "PasteImageToExplorer";
        internal const string AppTitle = "剪贴板贴图";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(
                true, @"Local\PasteImageToExplorer_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(AppTitle + " 已在运行，请查看任务栏通知区域（右下角）的图标。",
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log("UI", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    if (ex != null) Log("未处理", ex);
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (App app = new App())
                {
                    Application.Run();
                }
                GC.KeepAlive(mutex);
            }
        }

        internal static void Log(string context, Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + context + "] " + ex + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // 接收 WM_CLIPBOARDUPDATE 的隐藏窗口（事件驱动，无需轮询占用剪贴板）
    internal sealed class ClipboardListenerWindow : NativeWindow
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        internal event EventHandler ClipboardUpdate;
        internal event EventHandler HotkeyPressed;

        internal ClipboardListenerWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = Program.AppName + "_Listener";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                EventHandler handler = ClipboardUpdate;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            else if (m.Msg == 0x0312)
            {
                EventHandler handler = HotkeyPressed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }
    }

    internal sealed class App : IDisposable
    {
        private const string PngFormat = "PNG";
        private const string CacheFolderName = "ExplorerClipboardImages";
        private const int RetryIntervalMs = 500;
        private const int MaxRetries = 40;               // 剪贴板被占用时的重试次数（约 20 秒）
        private const double CacheRetentionDays = 1.0;   // 缓存文件保留天数
        private const int MaxCacheFiles = 200;           // 缓存文件数量上限（超出删最旧的）
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        private readonly string cacheDir;
        private readonly string settingsFile;

        private NotifyIcon trayIcon;
        private ContextMenuStrip menu;
        private ClipboardListenerWindow listener;
        private Timer retryTimer;
        private Timer cleanupTimer;
        private Timer pollTimer;      // 序号兜底，不读取未变化的剪贴板

        private ToolStripMenuItem itemMonitor;
        private ToolStripMenuItem itemKeepImage;
        private ToolStripMenuItem itemNotify;
        private ToolStripMenuItem itemAutoStart;

        private bool monitorEnabled = true;
        private bool keepImageOnClipboard = true;
        private bool showNotifications = true;
        private bool hotkeyEnabled = true;
        private string hotkeyText = "Alt+C";
        private int registeredHotkeyId;
        private string hotkeyStatus = "";
        private Form settingsForm;
        private string lastStatus = "等待剪贴板图像";

        private bool settingClipboard;   // 正在由本程序写入剪贴板
        private uint lastSetSeq;         // 本程序上次写入剪贴板后的序号
        private uint lastPolledSeq;
        private uint lastHandledSeq;
        private uint pendingSeq;
        private bool pendingManual;
        private int retryCount;
        private int convertedCount;
        private bool disposed;

        internal App()
        {
            cacheDir = Path.Combine(Path.GetTempPath(), CacheFolderName);
            Directory.CreateDirectory(cacheDir);

            string settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.AppName);
            Directory.CreateDirectory(settingsDir);
            settingsFile = Path.Combine(settingsDir, "config.ini");

            bool firstRun = !File.Exists(settingsFile);
            LoadSettings();

            CleanOldCache();
            BuildMenu();
            BuildTrayIcon(firstRun);

            retryTimer = new Timer();
            retryTimer.Interval = RetryIntervalMs;
            retryTimer.Tick += delegate { OnRetryTick(); };

            listener = new ClipboardListenerWindow();
            listener.ClipboardUpdate += delegate(object s, EventArgs e) { OnClipboardUpdateCore(); };
            listener.HotkeyPressed += delegate { ProcessClipboardNow(true); };
            if (!AddClipboardFormatListener(listener.Handle))
                Program.Log("初始化", new Exception("事件监听注册失败，使用序号兜底。"));
            StartWatchdog();
            if (!ApplyHotkey(hotkeyEnabled, hotkeyText))
                trayIcon.ShowBalloonTip(3000, Program.AppTitle, hotkeyStatus + " 请在设置中更换快捷键。", ToolTipIcon.Warning);

            cleanupTimer = new Timer();
            cleanupTimer.Interval = 60 * 60 * 1000;   // 每小时清理一次过期缓存
            cleanupTimer.Tick += delegate { CleanOldCache(); };
            cleanupTimer.Start();

            // 启动时若剪贴板中已有图像（启动前刚截的图），也转换一次
            if (monitorEnabled) ProcessClipboardNow(false);
            UpdateTooltip();
        }

        private void StartWatchdog()
        {
            if (pollTimer != null) pollTimer.Dispose();
            lastPolledSeq = GetClipboardSequenceNumber();
            pollTimer = new Timer();
            pollTimer.Interval = 750;
            pollTimer.Tick += delegate
            {
                uint seq = GetClipboardSequenceNumber();
                if (seq == 0 || seq == lastPolledSeq) return;
                lastPolledSeq = seq;
                OnClipboardUpdateCore();
            };
            pollTimer.Start();
        }

        // ---------------- 剪贴板处理 ----------------

        private void OnClipboardUpdateCore()
        {
            if (settingClipboard) return;                 // 忽略自己写入剪贴板触发的更新
            if (!monitorEnabled) return;
            uint seq = GetClipboardSequenceNumber();
            if (seq != 0 && (seq == lastSetSeq || seq == lastHandledSeq)) return;
            if (seq == pendingSeq && retryTimer.Enabled) return;
            if (seq != pendingSeq) { retryCount = 0; pendingManual = false; }
            pendingSeq = seq;
            ProcessClipboardNow(false);
        }

        private void OnRetryTick()
        {
            retryTimer.Stop();
            if ((!monitorEnabled && !pendingManual) || settingClipboard)
            {
                retryCount = 0;
                return;
            }
            ProcessClipboardNow(pendingManual);
        }

        private enum ProcessResult { Converted, NothingToDo, Busy, NotReady, Superseded, Failed }

        private void ProcessClipboardNow(bool manual)
        {
            uint attemptedSeq = GetClipboardSequenceNumber();
            bool newManualRequest = manual && !pendingManual;
            if (newManualRequest) { pendingManual = true; retryCount = 0; pendingSeq = GetClipboardSequenceNumber(); }
            ProcessResult result;
            try
            {
                result = TryProcessClipboard();
            }
            catch (ExternalException) { result = ProcessResult.Busy; }
            catch (Exception ex)
            {
                Program.Log("转换", ex);
                result = ProcessResult.Failed;
                lastStatus = "转换失败，请查看错误日志：" + ex.Message;
                if (manual) trayIcon.ShowBalloonTip(3000, Program.AppTitle, lastStatus, ToolTipIcon.Error);
            }

            if (result == ProcessResult.Busy || result == ProcessResult.NotReady || result == ProcessResult.Superseded)
            {
                retryCount++;
                lastStatus = "剪贴板暂不可读，正在重试（" + retryCount + "/" + MaxRetries + "）";
                if (newManualRequest)
                {
                    trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                        "剪贴板正被其他程序占用，稍后会自动重试。", ToolTipIcon.Warning);
                }
                if (retryCount <= MaxRetries) retryTimer.Start();
                else
                {
                    lastStatus = "重试超时，请重新截图或按快捷键重试";
                    Program.Log("重试超时", new IOException(lastStatus));
                    if (pendingManual) trayIcon.ShowBalloonTip(3000, Program.AppTitle, lastStatus, ToolTipIcon.Warning);
                    retryCount = 0; pendingManual = false;
                }
                return;
            }

            retryTimer.Stop();
            retryCount = 0;
            pendingManual = false;
            lastHandledSeq = result == ProcessResult.Converted ? lastSetSeq : attemptedSeq;
            if (manual && result == ProcessResult.NothingToDo)
            {
                trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                    "剪贴板中没有可转换的图像。", ToolTipIcon.Warning);
            }
        }

        // 返回 Converted 表示完成一次转换；Busy 表示剪贴板被其他程序占用，需要重试
        private ProcessResult TryProcessClipboard()
        {
            uint inputSeq = GetClipboardSequenceNumber();
            IDataObject data;
            try
            {
                data = Clipboard.GetDataObject();
            }
            catch (ExternalException) { return ProcessResult.Busy; }
            if (data == null) return ProcessResult.NotReady;

            if (!HasImageFormat(data)) return ProcessResult.NothingToDo;
            // 已是文件复制（例如在资源管理器里复制的图片文件）则无需转换
            if (data.GetDataPresent(DataFormats.FileDrop)) return ProcessResult.NothingToDo;

            // 优先读取 PNG 原始字节，可完整保留透明通道
            byte[] pngBytes = ReadPngBytes(data);

            Image sourceImage = null;
            Bitmap cacheImage = null;
            if (pngBytes == null)
            {
                try
                {
                    sourceImage = data.GetData(DataFormats.Bitmap) as Image;
                }
                catch (ExternalException) { return ProcessResult.Busy; }
                if (sourceImage == null) return ProcessResult.NotReady;
                cacheImage = new Bitmap(sourceImage);
            }

            string savedPath = null;
            Bitmap clipboardImage = null;
            MemoryStream pngStream = null;
            bool committed = false;
            try
            {
                savedPath = SaveImageToCache(pngBytes, cacheImage);

                DataObject outData = new DataObject();
                StringCollection dropList = new StringCollection();
                dropList.Add(savedPath);
                outData.SetFileDropList(dropList);

                if (keepImageOnClipboard)
                {
                    if (pngBytes != null)
                    {
                        pngStream = new MemoryStream(pngBytes);
                        outData.SetData(PngFormat, false, pngStream);
                        using (MemoryStream ms = new MemoryStream(pngBytes))
                        using (Image decoded = Image.FromStream(ms))
                        {
                            clipboardImage = new Bitmap(decoded);
                            outData.SetImage(clipboardImage);
                        }
                    }
                    else
                    {
                        clipboardImage = new Bitmap(cacheImage);
                        outData.SetImage(clipboardImage);
                    }
                }

                try
                {
                    // Don't overwrite a newer clipboard value while encoding/saving the old image.
                    uint nowSeq = GetClipboardSequenceNumber();
                    if (inputSeq != 0 && nowSeq != inputSeq) return ProcessResult.Superseded;
                    settingClipboard = true;
                    try
                    {
                        // Retry on our timer, not inside OLE (which could overwrite a newer value).
                        Clipboard.SetDataObject(outData, true, 0, 0);
                    }
                    finally
                    {
                        settingClipboard = false;
                    }
                    lastSetSeq = GetClipboardSequenceNumber();
                    committed = true;
                }
                catch (ExternalException)
                {
                    // OleSetClipboard can succeed before OleFlushClipboard fails. The
                    // candidate may already be referenced; the finally block checks it.
                    return ProcessResult.Busy;
                }

                convertedCount++;
                lastStatus = "已转换：" + Path.GetFileName(savedPath);
                UpdateTooltip();
                CleanOldCache();
                EnforceCacheLimits();

                if (showNotifications)
                {
                    trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                        "已保存 " + Path.GetFileName(savedPath) + "，在资源管理器中按 Ctrl+V 即可粘贴为文件。",
                        ToolTipIcon.Info);
                }
                return ProcessResult.Converted;
            }
            finally
            {
                if (sourceImage != null) sourceImage.Dispose();
                if (cacheImage != null) cacheImage.Dispose();
                if (clipboardImage != null) clipboardImage.Dispose();
                if (pngStream != null) pngStream.Dispose();
                if (!committed && savedPath != null) DeleteUncommittedCache(savedPath);
            }
        }

        private void DeleteUncommittedCache(string path)
        {
            HashSet<string> keep = GetProtectedCacheFiles();
            if (keep == null || keep.Contains(path)) return;
            try { File.Delete(path); } catch { }
        }

        private static bool HasImageFormat(IDataObject data)
        {
            return data.GetDataPresent(PngFormat)
                || data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent(DataFormats.Dib);
        }

        private static byte[] ReadPngBytes(IDataObject data)
        {
            try
            {
                if (!data.GetDataPresent(PngFormat)) return null;
                object obj = data.GetData(PngFormat);

                byte[] bytes = null;
                MemoryStream ms = obj as MemoryStream;
                if (ms != null)
                {
                    bytes = ms.ToArray();
                }
                else if (obj is byte[])
                {
                    bytes = (byte[])obj;
                }
                else
                {
                    Stream stream = obj as Stream;
                    if (stream != null)
                    {
                        using (stream)
                        using (MemoryStream copy = new MemoryStream())
                        {
                            stream.CopyTo(copy);
                            bytes = copy.ToArray();
                        }
                    }
                }

                if (bytes == null || bytes.Length < 8) return null;
                // 校验 PNG 魔数（89 50 4E 47）
                if (bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47) return null;
                return bytes;
            }
            catch (ExternalException) { throw; }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                Program.Log("读取 PNG", ex);
                return null;
            }
        }

        private string SaveImageToCache(byte[] pngBytes, Image image)
        {
            string baseName = "Screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            string path = Path.Combine(cacheDir, baseName + ".png");
            int n = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(cacheDir, baseName + "-" + n.ToString() + ".png");
                n++;
            }
            if (pngBytes != null) File.WriteAllBytes(path, pngBytes);
            else image.Save(path, ImageFormat.Png);
            return path;
        }

        // ---------------- 缓存管理 ----------------

        private void CleanOldCache()
        {
            try
            {
                HashSet<string> keep = GetProtectedCacheFiles();
                if (keep == null) return; // Can't safely determine current references: defer deletion.
                DateTime cutoff = DateTime.Now.AddDays(-CacheRetentionDays);
                foreach (string file in Directory.GetFiles(cacheDir, "*.png"))
                {
                    try
                    {
                        if (!keep.Contains(file) && File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void EnforceCacheLimits()
        {
            try
            {
                HashSet<string> keep = GetProtectedCacheFiles();
                if (keep == null) return;
                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.png");
                if (files.Length <= MaxCacheFiles) return;
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
                });
                int excess = files.Length - MaxCacheFiles;
                for (int i = 0; i < files.Length && excess > 0; i++)
                {
                    if (keep.Contains(files[i].FullName)) continue;
                    try { files[i].Delete(); excess--; } catch { }
                }
            }
            catch { }
        }

        // 手动清理：删除全部缓存文件，但保留剪贴板当前引用的文件
        private void CleanAllCache()
        {
            try
            {
                HashSet<string> keepList = GetProtectedCacheFiles();
                if (keepList == null)
                {
                    trayIcon.ShowBalloonTip(2000, Program.AppTitle, "剪贴板被占用，为保护当前图片，本次未清理。", ToolTipIcon.Warning);
                    return;
                }

                int deleted = 0;
                foreach (string file in Directory.GetFiles(cacheDir, "*.png"))
                {
                    if (keepList.Contains(file)) continue;
                    try { File.Delete(file); deleted++; }
                    catch { }
                }
                trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                    "已清理 " + deleted + " 个缓存文件。", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Program.Log("清理缓存", ex);
            }
        }

        private HashSet<string> GetProtectedCacheFiles()
        {
            try
            {
                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return null;
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                    if (paths == null) return null;
                    foreach (string path in paths) keep.Add(Path.GetFullPath(path));
                }
                return keep;
            }
            catch { return null; }
        }

        // ---------------- 界面 ----------------

        private void BuildMenu()
        {
            menu = new ContextMenuStrip();

            itemMonitor = new ToolStripMenuItem("启用剪贴板监控(&M)");
            itemMonitor.CheckOnClick = true;
            itemMonitor.Checked = monitorEnabled;
            itemMonitor.Click += delegate
            {
                monitorEnabled = itemMonitor.Checked;
                SaveSettings();
                UpdateTooltip();
                if (monitorEnabled) { lastHandledSeq = 0; ProcessClipboardNow(false); }
                else if (!pendingManual) { retryTimer.Stop(); retryCount = 0; }
            };

            itemKeepImage = new ToolStripMenuItem("转换后在剪贴板保留图像(&K)");
            itemKeepImage.CheckOnClick = true;
            itemKeepImage.Checked = keepImageOnClipboard;
            itemKeepImage.Click += delegate
            {
                keepImageOnClipboard = itemKeepImage.Checked;
                SaveSettings();
            };

            itemNotify = new ToolStripMenuItem("显示转换通知(&N)");
            itemNotify.CheckOnClick = true;
            itemNotify.Checked = showNotifications;
            itemNotify.Click += delegate
            {
                showNotifications = itemNotify.Checked;
                SaveSettings();
            };

            itemAutoStart = new ToolStripMenuItem("开机自动启动(&S)");
            itemAutoStart.CheckOnClick = true;
            itemAutoStart.Checked = IsAutoStartEnabled();
            itemAutoStart.Click += delegate
            {
                try
                {
                    SetAutoStart(itemAutoStart.Checked);
                }
                catch (Exception ex)
                {
                    Program.Log("自启动", ex);
                }
                itemAutoStart.Checked = IsAutoStartEnabled();
            };

            ToolStripMenuItem itemConvertNow = new ToolStripMenuItem("立即转换当前剪贴板图像(&C)");
            itemConvertNow.Click += delegate { ProcessClipboardNow(true); };

            ToolStripMenuItem itemOpenCache = new ToolStripMenuItem("打开缓存文件夹(&F)");
            itemOpenCache.Click += delegate { OpenCacheFolder(); };

            ToolStripMenuItem itemClean = new ToolStripMenuItem("清理缓存文件(&L)");
            itemClean.Click += delegate { CleanAllCache(); };

            ToolStripMenuItem itemAbout = new ToolStripMenuItem("关于(&A)");
            itemAbout.Click += delegate { ShowAbout(); };

            ToolStripMenuItem itemExit = new ToolStripMenuItem("退出(&X)");
            itemExit.Click += delegate { ExitApp(); };

            menu.Items.Add(itemMonitor);
            menu.Items.Add(itemKeepImage);
            menu.Items.Add(itemNotify);
            menu.Items.Add(itemAutoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemConvertNow);
            menu.Items.Add(itemOpenCache);
            menu.Items.Add(itemClean);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem itemSettings = new ToolStripMenuItem("设置：快捷键与转换选项(&O)");
            itemSettings.Click += delegate { ShowSettings(); };
            menu.Items.Add(itemSettings);
            menu.Items.Add(itemAbout);
            menu.Items.Add(itemExit);

            // 每次打开菜单时同步勾选状态（注册表项可能被外部修改）
            menu.Opened += delegate
            {
                itemMonitor.Checked = monitorEnabled;
                itemKeepImage.Checked = keepImageOnClipboard;
                itemNotify.Checked = showNotifications;
                itemAutoStart.Checked = IsAutoStartEnabled();
            };
        }

        private static bool TryParseHotkey(string text, out uint mods, out uint key, out string normalized)
        {
            mods = 0; key = 0; normalized = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim().ToUpperInvariant();
                uint bit = part == "ALT" ? 1u : part == "CTRL" || part == "CONTROL" ? 2u : part == "SHIFT" ? 4u : part == "WIN" ? 8u : 0u;
                if (bit != 0)
                {
                    if ((mods & bit) != 0) return false;
                    mods |= bit;
                }
                else
                {
                    if (key != 0 || i != parts.Length - 1) return false;
                    if (part.Length == 1 && ((part[0] >= 'A' && part[0] <= 'Z') || (part[0] >= '0' && part[0] <= '9')))
                        key = (uint)part[0];
                    else
                    {
                        int number;
                        if (!part.StartsWith("F") || !int.TryParse(part.Substring(1), out number) || number < 1 || number > 24) return false;
                        key = (uint)((int)Keys.F1 + number - 1);
                    }
                }
            }
            if (mods == 0 || key == 0) return false;
            normalized = ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 1) != 0 ? "Alt+" : "")
                + ((mods & 4) != 0 ? "Shift+" : "") + ((mods & 8) != 0 ? "Win+" : "")
                + (key >= (uint)Keys.F1 && key <= (uint)Keys.F24 ? "F" + (key - (uint)Keys.F1 + 1) : ((char)key).ToString());
            return true;
        }

        private bool ApplyHotkey(bool enabled, string text)
        {
            uint mods, key; string normalized;
            if (!enabled)
            {
                if (registeredHotkeyId != 0) UnregisterHotKey(listener.Handle, registeredHotkeyId);
                registeredHotkeyId = 0; hotkeyEnabled = false; hotkeyStatus = "全局快捷键已关闭";
                if (TryParseHotkey(text, out mods, out key, out normalized)) hotkeyText = normalized;
                return true;
            }
            if (!TryParseHotkey(text, out mods, out key, out normalized))
            {
                hotkeyStatus = "格式无效：请使用 Ctrl / Alt / Shift / Win 加字母、数字或 F1–F24";
                return false;
            }
            if (registeredHotkeyId != 0 && hotkeyText == normalized)
            { hotkeyEnabled = true; hotkeyStatus = "快捷键已生效：" + normalized; return true; }
            int nextId = registeredHotkeyId == 1 ? 2 : 1;
            if (!RegisterHotKey(listener.Handle, nextId, mods | 0x4000, key)) // MOD_NOREPEAT
            {
                hotkeyStatus = normalized + " 无法注册，可能已被占用或受系统限制（错误 " + Marshal.GetLastWin32Error() + "），原快捷键保持不变";
                return false;
            }
            if (registeredHotkeyId != 0) UnregisterHotKey(listener.Handle, registeredHotkeyId);
            registeredHotkeyId = nextId; hotkeyEnabled = true; hotkeyText = normalized;
            hotkeyStatus = "快捷键已生效：" + normalized;
            return true;
        }

        private void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = CreateSettingsForm();
            settingsForm.FormClosed += delegate { settingsForm = null; };
            settingsForm.Show();
        }

        private Form CreateSettingsForm()
        {
            Form form = new Form();
            form.Text = Program.AppTitle + " · 设置";
            form.Font = new Font("Microsoft YaHei UI", 10f);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.ClientSize = new Size(560, 460);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false; form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            Label title = new Label(); title.Text = "截图后，一键粘贴为文件";
            title.Font = new Font(form.Font.FontFamily, 15f, FontStyle.Bold);
            title.SetBounds(24, 18, 510, 35); form.Controls.Add(title);
            CheckBox monitor = new CheckBox(); monitor.Name = "monitor"; monitor.Text = "自动转换新的剪贴板图像";
            monitor.Checked = monitorEnabled; monitor.SetBounds(26, 65, 500, 28); form.Controls.Add(monitor);
            CheckBox keep = new CheckBox(); keep.Text = "转换后保留原图像（可继续粘贴到聊天 / 文档）";
            keep.Checked = keepImageOnClipboard; keep.SetBounds(26, 99, 500, 28); form.Controls.Add(keep);
            CheckBox notify = new CheckBox(); notify.Text = "转换成功时显示通知";
            notify.Checked = showNotifications; notify.SetBounds(26, 133, 500, 28); form.Controls.Add(notify);
            CheckBox startup = new CheckBox(); startup.Text = "登录 Windows 后自动启动";
            startup.Checked = IsAutoStartEnabled(); startup.SetBounds(26, 167, 500, 28); form.Controls.Add(startup);
            CheckBox shortcut = new CheckBox(); shortcut.Text = "启用全局快捷键：立即转换（暂停自动转换时也可用）";
            shortcut.Checked = hotkeyEnabled; shortcut.SetBounds(26, 210, 510, 28); form.Controls.Add(shortcut);
            TextBox hotkey = new TextBox(); hotkey.Name = "hotkey"; hotkey.Text = hotkeyText;
            hotkey.SetBounds(28, 247, 215, 30); hotkey.Enabled = shortcut.Checked; form.Controls.Add(hotkey);
            shortcut.CheckedChanged += delegate { hotkey.Enabled = shortcut.Checked; };
            hotkey.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && !e.Alt && !e.Shift && (e.KeyCode == Keys.A || e.KeyCode == Keys.C || e.KeyCode == Keys.V || e.KeyCode == Keys.X || e.KeyCode == Keys.Back)) return;
                if (e.Modifiers == Keys.None || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
                string keyName = e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9 ? ((char)('0' + e.KeyCode - Keys.D0)).ToString() : e.KeyCode.ToString();
                hotkey.Text = (e.Control ? "Ctrl+" : "") + (e.Alt ? "Alt+" : "") + (e.Shift ? "Shift+" : "") + keyName;
                e.SuppressKeyPress = true;
            };
            Label hint = new Label(); hint.Text = "直接按组合键，或输入 Alt+C / Ctrl+Shift+F8\nWin 组合键请手动输入";
            hint.SetBounds(257, 245, 280, 46); form.Controls.Add(hint);
            Label status = new Label(); status.Text = hotkeyStatus + "\n" + lastStatus;
            status.SetBounds(26, 300, 510, 60); form.Controls.Add(status);
            Button cache = new Button(); cache.Text = "打开缓存"; cache.SetBounds(26, 371, 108, 32);
            cache.Click += delegate { OpenCacheFolder(); }; form.Controls.Add(cache);
            Button log = new Button(); log.Text = "查看日志"; log.SetBounds(145, 371, 108, 32);
            log.Click += delegate
            {
                string path = Path.Combine(Path.GetDirectoryName(settingsFile), "error.log");
                if (File.Exists(path)) System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
                else status.Text = "尚无错误日志。\n" + lastStatus;
            }; form.Controls.Add(log);
            Button save = new Button(); save.Text = "保存"; save.SetBounds(308, 409, 108, 32); form.Controls.Add(save);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.SetBounds(427, 409, 108, 32); form.Controls.Add(cancel);
            cancel.Click += delegate { form.Close(); }; form.CancelButton = cancel; form.AcceptButton = save;
            save.Click += delegate
            {
                bool oldEnabled = hotkeyEnabled; string oldKey = hotkeyText;
                bool oldMonitor = monitorEnabled, oldKeep = keepImageOnClipboard, oldNotify = showNotifications;
                bool oldStartup = IsAutoStartEnabled();
                if (!ApplyHotkey(shortcut.Checked, hotkey.Text)) { status.ForeColor = Color.Firebrick; status.Text = hotkeyStatus; return; }
                try
                {
                    if (startup.Checked != oldStartup) SetAutoStart(startup.Checked);
                    monitorEnabled = monitor.Checked; keepImageOnClipboard = keep.Checked; showNotifications = notify.Checked;
                    if (!SaveSettings()) throw new IOException("设置文件写入失败");
                }
                catch (Exception ex)
                {
                    monitorEnabled = oldMonitor; keepImageOnClipboard = oldKeep; showNotifications = oldNotify;
                    ApplyHotkey(oldEnabled, oldKey);
                    try { if (IsAutoStartEnabled() != oldStartup) SetAutoStart(oldStartup); } catch (Exception restore) { Program.Log("恢复自启动", restore); }
                    Program.Log("保存设置", ex); status.ForeColor = Color.Firebrick; status.Text = "未保存：" + ex.Message; return;
                }
                itemMonitor.Checked = monitorEnabled; itemKeepImage.Checked = keepImageOnClipboard; itemNotify.Checked = showNotifications;
                UpdateTooltip(); form.Close();
                if (monitorEnabled) { lastHandledSeq = 0; ProcessClipboardNow(false); }
                else if (!pendingManual) { retryTimer.Stop(); retryCount = 0; }
            };
            return form;
        }

        private void BuildTrayIcon(bool firstRun)
        {
            trayIcon = new NotifyIcon();
            trayIcon.Text = Program.AppTitle;
            try
            {
                using (Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                {
                    trayIcon.Icon = (Icon)exeIcon.Clone();
                }
            }
            catch
            {
                trayIcon.Icon = SystemIcons.Information;
            }
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { OpenCacheFolder(); };

            if (firstRun)
            {
                trayIcon.ShowBalloonTip(3000, Program.AppTitle,
                    "已开始监控剪贴板：截图或复制图片后，在资源管理器中按 Ctrl+V 即可直接粘贴为文件。",
                    ToolTipIcon.Info);
            }
        }

        private void UpdateTooltip()
        {
            string text = Program.AppTitle;
            if (convertedCount > 0) text += " - 已转换 " + convertedCount + " 张";
            if (!monitorEnabled) text += "（已暂停）";
            if (text.Length > 63) text = text.Substring(0, 63);   // NotifyIcon.Text 上限 63 字符
            trayIcon.Text = text;
        }

        private void OpenCacheFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + cacheDir + "\"");
            }
            catch (Exception ex)
            {
                Program.Log("打开缓存", ex);
            }
        }

        private void ShowAbout()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Program.AppTitle + " v1.1");
            sb.AppendLine("作者：LHStudio");
            sb.AppendLine("全局快捷键：" + (hotkeyEnabled && registeredHotkeyId != 0 ? hotkeyText : "未启用"));
            sb.AppendLine();
            sb.AppendLine("截图或复制图片后，自动把图像保存为 PNG 文件并放回剪贴板：");
            sb.AppendLine("  · 在资源管理器 / 桌面按 Ctrl+V，直接粘贴为图片文件");
            sb.AppendLine("  · 图像同时保留在剪贴板，可继续粘贴到聊天窗口、文档等");
            sb.AppendLine();
            sb.AppendLine("缓存目录：" + cacheDir);
            sb.AppendLine("文件保留 24 小时后自动清理（上限 " + MaxCacheFiles + " 个）。");
            sb.AppendLine();
            sb.AppendLine("Copyright (C) LHStudio 2026");
            MessageBox.Show(sb.ToString(), "关于 " + Program.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApp()
        {
            try { SaveSettings(); } catch { }
            if (retryTimer != null) retryTimer.Stop();
            if (cleanupTimer != null) cleanupTimer.Stop();
            if (pollTimer != null) pollTimer.Stop();
            if (listener != null)
            {
                try { RemoveClipboardFormatListener(listener.Handle); } catch { }
            }
            if (trayIcon != null) trayIcon.Visible = false;
            Application.Exit();
        }

        // ---------------- 设置与自启动 ----------------

        private void LoadSettings()
        {
            try
            {
                foreach (string line in File.ReadAllLines(settingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "monitor") monitorEnabled = (val == "1");
                    else if (key == "keep_image") keepImageOnClipboard = (val == "1");
                    else if (key == "notify") showNotifications = (val == "1");
                    else if (key == "hotkey_enabled") hotkeyEnabled = (val == "1");
                    else if (key == "hotkey")
                    {
                        uint mods, code; string normalized;
                        if (TryParseHotkey(val, out mods, out code, out normalized)) hotkeyText = normalized;
                    }
                }
            }
            catch { }
        }

        private bool SaveSettings()
        {
            try
            {
                string temporary = settingsFile + ".tmp";
                File.WriteAllLines(temporary, new string[]
                {
                    "monitor=" + (monitorEnabled ? 1 : 0),
                    "keep_image=" + (keepImageOnClipboard ? 1 : 0),
                    "notify=" + (showNotifications ? 1 : 0),
                    "hotkey_enabled=" + (hotkeyEnabled ? 1 : 0),
                    "hotkey=" + hotkeyText
                }, Encoding.UTF8);
                if (File.Exists(settingsFile)) File.Replace(temporary, settingsFile, null);
                else File.Move(temporary, settingsFile);
                return true;
            }
            catch (Exception ex) { Program.Log("保存设置", ex); return false; }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    object value = key.GetValue(Program.AppName, null);
                    if (value == null) return false;
                    string path = value.ToString().Trim().Trim('"');
                    return string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SetAutoStart(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enable) key.SetValue(Program.AppName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(Program.AppName, false);
            }
        }

        // ---------------- 释放 ----------------

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (settingsForm != null) { settingsForm.Dispose(); settingsForm = null; }
            if (listener != null && registeredHotkeyId != 0) UnregisterHotKey(listener.Handle, registeredHotkeyId);
            if (retryTimer != null) { retryTimer.Dispose(); retryTimer = null; }
            if (cleanupTimer != null) { cleanupTimer.Dispose(); cleanupTimer = null; }
            if (pollTimer != null) { pollTimer.Dispose(); pollTimer = null; }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (menu != null) { menu.Dispose(); menu = null; }
            if (listener != null)
            {
                try { RemoveClipboardFormatListener(listener.Handle); } catch { }
                try { listener.DestroyHandle(); } catch { }
                listener = null;
            }
        }
    }
}

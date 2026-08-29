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
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0.0")]

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

        private readonly string cacheDir;
        private readonly string settingsFile;

        private NotifyIcon trayIcon;
        private ContextMenuStrip menu;
        private ClipboardListenerWindow listener;
        private Timer retryTimer;
        private Timer cleanupTimer;
        private Timer pollTimer;      // 仅当系统拒绝事件监听注册时启用

        private ToolStripMenuItem itemMonitor;
        private ToolStripMenuItem itemKeepImage;
        private ToolStripMenuItem itemNotify;
        private ToolStripMenuItem itemAutoStart;

        private bool monitorEnabled = true;
        private bool keepImageOnClipboard = true;
        private bool showNotifications = true;

        private bool settingClipboard;   // 正在由本程序写入剪贴板
        private uint lastSetSeq;         // 本程序上次写入剪贴板后的序号
        private uint lastPolledSeq;
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

            listener = new ClipboardListenerWindow();
            listener.ClipboardUpdate += delegate(object s, EventArgs e) { OnClipboardUpdateCore(); };
            if (!AddClipboardFormatListener(listener.Handle))
            {
                Program.Log("初始化", new Exception("AddClipboardFormatListener 失败，改用轮询模式。"));
                pollTimer = new Timer();
                pollTimer.Interval = 250;
                pollTimer.Tick += delegate
                {
                    uint seq = GetClipboardSequenceNumber();
                    if (seq == 0 || seq == lastPolledSeq) return;
                    lastPolledSeq = seq;
                    OnClipboardUpdateCore();
                };
                pollTimer.Start();
            }

            retryTimer = new Timer();
            retryTimer.Interval = RetryIntervalMs;
            retryTimer.Tick += delegate { OnRetryTick(); };

            cleanupTimer = new Timer();
            cleanupTimer.Interval = 60 * 60 * 1000;   // 每小时清理一次过期缓存
            cleanupTimer.Tick += delegate { CleanOldCache(); };
            cleanupTimer.Start();

            // 启动时若剪贴板中已有图像（启动前刚截的图），也转换一次
            if (monitorEnabled) ProcessClipboardNow(false);
        }

        // ---------------- 剪贴板处理 ----------------

        private void OnClipboardUpdateCore()
        {
            if (settingClipboard) return;                 // 忽略自己写入剪贴板触发的更新
            if (!monitorEnabled) return;
            uint seq = GetClipboardSequenceNumber();
            if (seq != 0 && seq == lastSetSeq) return;
            ProcessClipboardNow(false);
        }

        private void OnRetryTick()
        {
            retryTimer.Stop();
            if (!monitorEnabled || settingClipboard)
            {
                retryCount = 0;
                return;
            }
            ProcessClipboardNow(false);
        }

        private enum ProcessResult { Converted, NothingToDo, Busy }

        private void ProcessClipboardNow(bool manual)
        {
            ProcessResult result;
            try
            {
                result = TryProcessClipboard();
            }
            catch (Exception ex)
            {
                Program.Log("转换", ex);
                result = ProcessResult.NothingToDo;
            }

            if (result == ProcessResult.Busy)
            {
                retryCount++;
                if (manual)
                {
                    trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                        "剪贴板正被其他程序占用，稍后会自动重试。", ToolTipIcon.Warning);
                }
                if (retryCount <= MaxRetries) retryTimer.Start();
                else retryCount = 0;
                return;
            }

            retryTimer.Stop();
            retryCount = 0;
            if (manual && result == ProcessResult.NothingToDo)
            {
                trayIcon.ShowBalloonTip(2000, Program.AppTitle,
                    "剪贴板中没有可转换的图像。", ToolTipIcon.Warning);
            }
        }

        // 返回 Converted 表示完成一次转换；Busy 表示剪贴板被其他程序占用，需要重试
        private ProcessResult TryProcessClipboard()
        {
            IDataObject data;
            try
            {
                data = Clipboard.GetDataObject();
            }
            catch (ExternalException) { return ProcessResult.Busy; }
            if (data == null) return ProcessResult.NothingToDo;

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
                    sourceImage = Clipboard.GetImage();
                }
                catch (ExternalException) { return ProcessResult.Busy; }
                if (sourceImage == null) return ProcessResult.NothingToDo;
                cacheImage = new Bitmap(sourceImage);
            }

            string savedPath = null;
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
                        outData.SetData(PngFormat, false, new MemoryStream(pngBytes));
                        using (MemoryStream ms = new MemoryStream(pngBytes))
                        using (Image decoded = Image.FromStream(ms))
                        {
                            outData.SetImage(new Bitmap(decoded));
                        }
                    }
                    else
                    {
                        outData.SetImage(new Bitmap(cacheImage));
                    }
                }

                try
                {
                    settingClipboard = true;
                    try
                    {
                        Clipboard.SetDataObject(outData, true);
                    }
                    finally
                    {
                        settingClipboard = false;
                    }
                    lastSetSeq = GetClipboardSequenceNumber();
                }
                catch (ExternalException)
                {
                    try { File.Delete(savedPath); } catch { }
                    return ProcessResult.Busy;
                }

                convertedCount++;
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
                // cacheImage 可能仍被剪贴板数据对象引用，交给 GC 回收
            }
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
            catch
            {
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
                DateTime cutoff = DateTime.Now.AddDays(-CacheRetentionDays);
                foreach (string file in Directory.GetFiles(cacheDir, "*.png"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
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
                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.png");
                if (files.Length <= MaxCacheFiles) return;
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
                });
                int excess = files.Length - MaxCacheFiles;
                for (int i = 0; i < excess; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        // 手动清理：删除全部缓存文件，但保留剪贴板当前引用的文件
        private void CleanAllCache()
        {
            try
            {
                List<string> keepList = null;
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        StringCollection drop = Clipboard.GetFileDropList();
                        keepList = new List<string>();
                        foreach (string f in drop) keepList.Add(f.ToLowerInvariant());
                    }
                }
                catch { }

                int deleted = 0;
                foreach (string file in Directory.GetFiles(cacheDir, "*.png"))
                {
                    if (keepList != null && keepList.Contains(file.ToLowerInvariant())) continue;
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
            sb.AppendLine(Program.AppTitle + " v1.0");
            sb.AppendLine("作者：LHStudio");
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
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(settingsFile, new string[]
                {
                    "monitor=" + (monitorEnabled ? 1 : 0),
                    "keep_image=" + (keepImageOnClipboard ? 1 : 0),
                    "notify=" + (showNotifications ? 1 : 0)
                }, Encoding.UTF8);
            }
            catch { }
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
                try { listener.DestroyHandle(); } catch { }
                listener = null;
            }
        }
    }
}

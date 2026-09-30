using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

// Runs the real application against an isolated Windows clipboard, not the user's.
internal static class Regression
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder text, uint length, out uint needed);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb; public string reserved, desktop, title;
        public uint x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, cbReserved; public IntPtr reserved2, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public uint id, threadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, System.Text.StringBuilder command, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr environment, string cwd, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static object app;
    static Type type;
    static string root;
    static int passed, failed;
    static object Get(string name) { FieldInfo f = type.GetField(name, Flags); return f == null ? null : f.GetValue(app); }
    static void Set(string name, object value) { FieldInfo f = type.GetField(name, Flags); if (f != null) f.SetValue(app, value); }
    static object Call(string name, params object[] args)
    {
        MethodInfo m = type.GetMethod(name, Flags);
        if (m == null) throw new MissingMethodException(name);
        try { return m.Invoke(app, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Assert(string name, bool value)
    {
        Console.WriteLine((value ? "PASS " : "FAIL ") + name);
        if (value) passed++; else failed++;
    }
    static void Test(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Assert(name + " [" + ex.GetType().Name + ": " + ex.Message + "]", false); }
    }
    static void Pump(int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < end);
    }
    static void PutImage()
    {
        using (Bitmap image = new Bitmap(120, 90))
        {
            using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(255, 40, 160, 80));
            Clipboard.SetImage(image);
        }
    }
    static string CurrentFile()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var files = Clipboard.GetFileDropList();
        return files.Count == 0 ? null : files[0];
    }
    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "--hotkeys") return HotkeyProbe(args[0]);
        IntPtr station = CreateWindowStation(null, 0, 0x000F037F, IntPtr.Zero);
        Console.WriteLine("STATION_HANDLE=" + station + " CREATE_ERROR=" + Marshal.GetLastWin32Error());
        if (station == IntPtr.Zero || !SetProcessWindowStation(station))
        { Console.Error.WriteLine("Isolated window station failed: " + Marshal.GetLastWin32Error()); return 2; }
        int result = 2;
        Thread testThread = new Thread(delegate() {
            IntPtr desktop = CreateDesktop("Tests", IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
            { Console.Error.WriteLine("Isolated desktop failed: " + Marshal.GetLastWin32Error()); return; }
            try { result = RunTests(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 2; }
        });
        testThread.SetApartmentState(ApartmentState.STA);
        testThread.Start(); testThread.Join(); return result;
    }
    static int RunTests(string[] args)
    {        root = Path.Combine(Path.GetTempPath(), "PasteImageTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMP", root);
        var stationName = new System.Text.StringBuilder(256); uint needed;
        if (!GetUserObjectInformation(GetProcessWindowStation(), 2, stationName, 512, out needed)) throw new Exception("cannot query isolated station");
        Environment.SetEnvironmentVariable("EXPECTED_TEST_STATION", stationName.ToString());
        Console.WriteLine("INPUT: bitmap=120x90; PNG=64x64 alpha128; clipboard lock=3000ms; paused monitor; isolated window station");
        Console.WriteLine("ASSEMBLY " + Path.GetFullPath(args[0]));
        type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("PasteImageToExplorer.App", true);
        app = FormatterServices.GetUninitializedObject(type);
        Set("cacheDir", Path.Combine(root, "ExplorerClipboardImages"));
        Directory.CreateDirectory((string)Get("cacheDir"));
        Set("settingsFile", Path.Combine(root, "config.ini"));
        Set("monitorEnabled", false); Set("keepImageOnClipboard", true); Set("showNotifications", false);
        Set("hotkeyEnabled", true); Set("hotkeyText", "Alt+C");
        Call("BuildMenu"); Call("BuildTrayIcon", false);
        var timer = new System.Windows.Forms.Timer(); timer.Interval = 500;
        timer.Tick += delegate { Call("OnRetryTick"); }; Set("retryTimer", timer);
        Type listenerType = type.Assembly.GetType("PasteImageToExplorer.ClipboardListenerWindow");
        NativeWindow listener = (NativeWindow)Activator.CreateInstance(listenerType, true);
        Set("listener", listener);
        listenerType.GetEvent("ClipboardUpdate", Flags).GetAddMethod(true).Invoke(listener, new object[] { new EventHandler(delegate { Call("OnClipboardUpdateCore"); }) });
        if (listenerType.GetEvent("HotkeyPressed", Flags) != null)
            listenerType.GetEvent("HotkeyPressed", Flags).GetAddMethod(true).Invoke(listener, new object[] { new EventHandler(delegate { Call("ProcessClipboardNow", true); }) });
        AddClipboardFormatListener(listener.Handle);
        try { Run(args); }
        finally
        {
            RemoveClipboardFormatListener(listener.Handle);
            ((IDisposable)app).Dispose();
            // Only test-owned files under our freshly generated root.
            Directory.Delete(root, true);
        }
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
    static void Run(string[] args)
    {
        Test("bitmap conversion", delegate {
            PutImage(); Call("ProcessClipboardNow", false);
            string path = CurrentFile();
            Assert("bitmap file plus image", path != null && File.Exists(path) && Clipboard.ContainsImage());
            using (Image image = Image.FromFile(path)) Assert("bitmap dimensions", image.Width == 120 && image.Height == 90);
        });
        Test("PNG", delegate {
            byte[] bytes;
            using (Bitmap b = new Bitmap(64, 64))
            using (MemoryStream s = new MemoryStream())
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(128, 200, 30, 30));
                b.Save(s, ImageFormat.Png); bytes = s.ToArray();
            }
            DataObject data = new DataObject(); data.SetData("PNG", false, new MemoryStream(bytes));
            Clipboard.SetDataObject(data, true); Call("ProcessClipboardNow", false);
            string path = CurrentFile();
            Assert("PNG bytes and alpha preserved", path != null && SameBytes(bytes, File.ReadAllBytes(path)));
            Assert("PNG retained on clipboard", Clipboard.GetDataObject().GetDataPresent("PNG"));
            object retained = Clipboard.GetDataObject().GetData("PNG");
            byte[] retainedBytes = retained is MemoryStream ? ((MemoryStream)retained).ToArray() : retained as byte[];
            Assert("retained PNG readable after conversion disposal", retainedBytes != null && SameBytes(bytes, retainedBytes));
        });
        Test("file/text/no loop", delegate {
            string cache = (string)Get("cacheDir"); int count = Directory.GetFiles(cache).Length;
            var list = new System.Collections.Specialized.StringCollection(); list.Add(args[0]);
            Clipboard.SetFileDropList(list); Call("ProcessClipboardNow", false);
            Assert("file copy unchanged", CurrentFile() == args[0]);
            Clipboard.SetText("plain text"); Call("ProcessClipboardNow", false);
            Assert("text unchanged", Clipboard.GetText() == "plain text");
            Assert("non-image creates no file", Directory.GetFiles(cache).Length == count);
            Set("monitorEnabled", true); PutImage(); Pump(1300);
            int stable = Directory.GetFiles(cache).Length; Pump(1200);
            Assert("no self-trigger loop", Directory.GetFiles(cache).Length == stable && CurrentFile() != null);
            Set("monitorEnabled", false);
        });
        Test("manual retry", delegate {
            PutImage(); Pump(50);
            ManualResetEvent ready = new ManualResetEvent(false);
            bool locked = false;
            Thread blocker = new Thread(delegate() {
                locked = OpenClipboard(IntPtr.Zero); ready.Set(); Thread.Sleep(3000); if (locked) CloseClipboard();
            });
            blocker.Start(); ready.WaitOne();
            if (!locked) throw new Exception("could not acquire test clipboard lock");
            Call("ProcessClipboardNow", true); blocker.Join(); Pump(1800);
            Assert("paused manual conversion retries after clipboard unlock", CurrentFile() != null);
            timerStop();
        });
        Test("busy PNG reads", delegate {
            bool busy = false;
            try { Call("ReadPngBytes", new BusyPngData()); } catch (ExternalException) { busy = true; }
            Assert("transient PNG read failure is retryable", busy);
        });
        Test("cache protection", delegate {
            PutImage(); Call("ProcessClipboardNow", false);
            string current = CurrentFile(); File.SetLastWriteTime(current, DateTime.Now.AddDays(-2));
            Call("CleanOldCache"); Assert("expiry keeps clipboard referenced file", File.Exists(current));
            if (!File.Exists(current)) File.WriteAllBytes(current, new byte[] { 1 });
            for (int i = 0; i < 205; i++) File.WriteAllText(Path.Combine((string)Get("cacheDir"), "dummy-" + i + ".png"), "test");
            Call("EnforceCacheLimits"); Assert("count limit keeps clipboard referenced file", File.Exists(current));
            if (!File.Exists(current)) File.WriteAllBytes(current, new byte[] { 1 });
            Call("CleanAllCache"); Assert("manual cleanup keeps clipboard referenced file", File.Exists(current));
        });
        Test("busy cleanup", delegate {
            PutImage(); Call("ProcessClipboardNow", false); string current = CurrentFile();
            ManualResetEvent ready = new ManualResetEvent(false); bool locked = false;
            Thread blocker = new Thread(delegate() { locked = OpenClipboard(IntPtr.Zero); ready.Set(); Thread.Sleep(3000); if (locked) CloseClipboard(); });
            blocker.Start(); ready.WaitOne();
            if (!locked) throw new Exception("clipboard lock unavailable");
            Call("CleanAllCache"); blocker.Join();
            Assert("cleanup defers while clipboard references cannot be read", File.Exists(current));
        });
        Test("partial clipboard commit cleanup", delegate {
            string candidate = Path.Combine((string)Get("cacheDir"), "partial-commit.png"); File.WriteAllText(candidate, "fixture");
            var drop = new System.Collections.Specialized.StringCollection(); drop.Add(candidate); Clipboard.SetFileDropList(drop);
            Call("DeleteUncommittedCache", candidate);
            Assert("failed flush cleanup preserves already-published file", File.Exists(candidate));
            Clipboard.SetText("newer user value"); Call("DeleteUncommittedCache", candidate);
            Assert("abandoned unreferenced candidate is deleted", !File.Exists(candidate));
        });
        Test("watchdog", delegate {
            Set("monitorEnabled", true);
            RemoveClipboardFormatListener(((NativeWindow)Get("listener")).Handle);
            Call("StartWatchdog"); PutImage(); Pump(1800);
            Assert("watchdog converts when notification is missing", CurrentFile() != null);
            Set("monitorEnabled", false);
        });
        Test("hotkey dispatch and settings", delegate {
            Set("monitorEnabled", false); PutImage(); Pump(100);
            Assert("paused image waits for manual shortcut", CurrentFile() == null);
            NativeWindow w = (NativeWindow)Get("listener");
            PostMessage(w.Handle, 0x312, new IntPtr(1), IntPtr.Zero); Pump(700);
            Assert("WM_HOTKEY converts while monitor paused", CurrentFile() != null);
            Assert("unmodified key rejected", !(bool)Call("ApplyHotkey", true, "C"));
            Assert("invalid key rejected", !(bool)Call("ApplyHotkey", true, "Alt+NotAKey"));
            Set("hotkeyText", "Ctrl+Shift+F8");
            Call("SaveSettings"); Set("hotkeyText", "Alt+C"); Call("LoadSettings");
            Assert("custom hotkey persisted", (string)Get("hotkeyText") == "Ctrl+Shift+F8");
            Assert("hotkey can be disabled", (bool)Call("ApplyHotkey", false, "Ctrl+Shift+F8"));
            Call("SaveSettings"); Set("hotkeyEnabled", true); Call("LoadSettings");
            Assert("disabled hotkey persisted", !(bool)Get("hotkeyEnabled"));
            using (Form form = (Form)Call("CreateSettingsForm"))
            {
                form.Show(); Pump(100);
                Assert("settings page exposes shortcut and monitor controls", form.Controls.Find("hotkey", true).Length == 1 && form.Controls.Find("monitor", true).Length == 1);
                if (args.Length > 1) using (Bitmap image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(args[1], ImageFormat.Png); }
                form.Close();
            }
        });
        Test("automatic clipboard contention", delegate {
            Set("monitorEnabled", true); AddClipboardFormatListener(((NativeWindow)Get("listener")).Handle);
            PutImage();
            ManualResetEvent ready = new ManualResetEvent(false); bool locked = false;
            Thread blocker = new Thread(delegate() { locked = OpenClipboard(IntPtr.Zero); ready.Set(); Thread.Sleep(3000); if (locked) CloseClipboard(); });
            blocker.Start(); ready.WaitOne();
            if (!locked) throw new Exception("clipboard lock unavailable");
            Call("OnClipboardUpdateCore"); blocker.Join(); Pump(1800);
            Assert("automatic conversion retries after clipboard unlock", CurrentFile() != null);
            Set("monitorEnabled", false);
        });
        Test("settings save and cancel", delegate {
            Set("monitorEnabled", false); Set("keepImageOnClipboard", true); Set("hotkeyEnabled", false); Call("SaveSettings");
            byte[] before = File.ReadAllBytes((string)Get("settingsFile"));
            using (Form form = (Form)Call("CreateSettingsForm"))
            {
                form.Show(); Pump(30);
                foreach (Control c in form.Controls)
                {
                    CheckBox box = c as CheckBox;
                    if (box != null && box.Text.StartsWith("转换后保留")) box.Checked = false;
                    Button button = c as Button;
                    if (button != null && button.Text == "取消") button.PerformClick();
                }
            }
            Assert("cancel leaves settings unchanged", SameBytes(before, File.ReadAllBytes((string)Get("settingsFile"))) && (bool)Get("keepImageOnClipboard"));
            using (Form form = (Form)Call("CreateSettingsForm"))
            {
                form.Show(); Pump(30); Button save = null;
                foreach (Control c in form.Controls)
                {
                    CheckBox box = c as CheckBox;
                    if (box != null && box.Text.StartsWith("转换后保留")) box.Checked = false;
                    Button button = c as Button;
                    if (button != null && button.Text == "保存") save = button;
                }
                save.PerformClick();
                Assert("settings save applies and persists image preference", !(bool)Get("keepImageOnClipboard") && File.ReadAllText((string)Get("settingsFile")).Contains("keep_image=0"));
            }
            Set("keepImageOnClipboard", true);
        });
        Test("newest image", delegate {
            Set("monitorEnabled", true);
            for (int i = 0; i < 12; i++)
                using (Bitmap b = new Bitmap(120 + i, 90))
                { using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.FromArgb(255, i, 25, 50)); Clipboard.SetImage(b); }
            Pump(1400);
            string path = CurrentFile();
            bool newest = false;
            if (path != null) using (Bitmap b = new Bitmap(path)) newest = b.Width == 131 && b.GetPixel(0, 0).R == 11;
            Assert("burst converts newest image pixels and dimensions", newest);
            Set("monitorEnabled", false);
        });
        if (args.Length > 2) Test("original project end-to-end suite", delegate {
            Set("monitorEnabled", true);
            string stdout = Path.Combine(root, "original.stdout.txt"), stderr = Path.Combine(root, "original.stderr.txt");
            var command = new System.Text.StringBuilder("cmd.exe /d /s /c \"powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File \"" + Path.GetFullPath(args[2]) + "\" 1>\"" + stdout + "\" 2>\"" + stderr + "\"\"");
            StartupInfo startup = new StartupInfo(); startup.cb = Marshal.SizeOf(startup);
            startup.desktop = Environment.GetEnvironmentVariable("EXPECTED_TEST_STATION") + "\\Tests";
            ProcessInfo pi;
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref startup, out pi))
                throw new Exception("isolated child creation failed: " + Marshal.GetLastWin32Error());
            using (Process child = Process.GetProcessById((int)pi.id))
            {
                while (!child.HasExited) Pump(30);
                Console.Write(File.ReadAllText(stdout));
                string error = File.ReadAllText(stderr); if (error.Length > 0) Console.Error.Write(error);
                uint code;
                Assert("original suite exit zero", GetExitCodeProcess(pi.process, out code) && code == 0);
            }
            CloseHandle(pi.process); CloseHandle(pi.thread);
            Set("monitorEnabled", false);
        });
    }

    static int HotkeyProbe(string assembly)
    {
        Console.WriteLine("INPUT: real interactive desktop; Alt+C / Ctrl+Shift+F8; conflict Ctrl+F9; no clipboard access");
        root = Path.Combine(Path.GetTempPath(), "PasteHotkeyTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        type = Assembly.LoadFrom(Path.GetFullPath(assembly)).GetType("PasteImageToExplorer.App", true);
        app = FormatterServices.GetUninitializedObject(type);
        Set("settingsFile", Path.Combine(root, "config.ini")); Set("hotkeyText", "Alt+C");
        Type lt = type.Assembly.GetType("PasteImageToExplorer.ClipboardListenerWindow");
        NativeWindow w = (NativeWindow)Activator.CreateInstance(lt, true); Set("listener", w);
        int fired = 0;
        if (lt.GetEvent("HotkeyPressed", Flags) != null)
            lt.GetEvent("HotkeyPressed", Flags).GetAddMethod(true).Invoke(w, new object[] { new EventHandler(delegate { fired++; }) });
        try
        {
            Test("interactive hotkey", delegate {
                bool registered = (bool)Call("ApplyHotkey", true, "Alt+C");
                Assert("default Alt+C registers on interactive desktop", registered);
                Console.WriteLine("HOTKEY_STATUS " + Get("hotkeyStatus"));
                if (registered)
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x43, 0, 0, UIntPtr.Zero);
                    keybd_event(0x43, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); Pump(300);
                    Assert("real Alt+C dispatches WM_HOTKEY", fired == 1);
                }
                Assert("custom Ctrl+Shift+F8 registers", (bool)Call("ApplyHotkey", true, "Ctrl+Shift+F8"));
                bool reserved = RegisterHotKey(w.Handle, 99, 2, (uint)Keys.F9);
                Assert("conflict preserves working shortcut", reserved && !(bool)Call("ApplyHotkey", true, "Ctrl+F9") && (string)Get("hotkeyText") == "Ctrl+Shift+F8");
                if (reserved) UnregisterHotKey(w.Handle, 99);
                Call("SaveSettings"); Set("hotkeyText", "Alt+C"); Call("LoadSettings");
                Assert("custom shortcut round-trips config", (string)Get("hotkeyText") == "Ctrl+Shift+F8");
                Assert("shortcut disable unregisters", (bool)Call("ApplyHotkey", false, "Ctrl+Shift+F8") && (int)Get("registeredHotkeyId") == 0);
            });
        }
        finally { ((IDisposable)app).Dispose(); Directory.Delete(root, true); }
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
    }
    static void timerStop() { ((System.Windows.Forms.Timer)Get("retryTimer")).Stop(); }
    // A delayed-rendering clipboard producer may advertise PNG before it can supply bytes.
    sealed class BusyPngData : IDataObject
    {
        public object GetData(string f) { throw new ExternalException("temporary clipboard producer failure"); }
        public object GetData(string f, bool a) { return GetData(f); }
        public object GetData(Type t) { return GetData(t.Name); }
        public bool GetDataPresent(string f) { return f == "PNG"; }
        public bool GetDataPresent(string f, bool a) { return GetDataPresent(f); }
        public bool GetDataPresent(Type t) { return false; }
        public string[] GetFormats() { return new string[] { "PNG" }; }
        public string[] GetFormats(bool a) { return GetFormats(); }
        public void SetData(string f, object d) { throw new NotSupportedException(); }
        public void SetData(string f, bool a, object d) { throw new NotSupportedException(); }
        public void SetData(Type t, object d) { throw new NotSupportedException(); }
        public void SetData(object d) { throw new NotSupportedException(); }
    }
}

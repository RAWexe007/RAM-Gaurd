using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RamGuard
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            if (!TrayApp.IsAdmin())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                }
                catch
                {
                    MessageBox.Show("RamGuard needs administrator rights to close some processes and to start with Windows.\n\nPlease run it again and click Yes on the prompt.", "RamGuard");
                }
                return;
            }
            bool created;
            using (Mutex m = new Mutex(true, "RamGuard_SingleInstance", out created))
            {
                if (!created) return;
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                try
                {
                    Application.EnableVisualStyles();
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("RamGuard crashed:\n\n" + ex.ToString(), "RamGuard");
                }
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        const string AppName = "RamGuard";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly string dataDir = PickDir();
        string configPath { get { return Path.Combine(dataDir, "RamGuard-list.txt"); } }
        string logPath { get { return Path.Combine(dataDir, "RamGuard-log.txt"); } }

        static string PickDir()
        {
            string[] options = new string[] {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamGuard")
            };
            foreach (string d in options)
            {
                try
                {
                    Directory.CreateDirectory(d);
                    string test = Path.Combine(d, ".write-test");
                    File.WriteAllText(test, "x");
                    File.Delete(test);
                    return d;
                }
                catch { }
            }
            return Path.GetTempPath();
        }

        internal static bool IsAdmin()
        {
            try
            {
                return new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        readonly List<string> always = new List<string>();
        readonly List<string> idle = new List<string>();
        readonly Dictionary<string, DateTime> hiddenSince = new Dictionary<string, DateTime>();
        readonly HashSet<string> failedLogged = new HashSet<string>();
        int graceSeconds = 30;
        int checkSeconds = 10;
        bool paused = false;

        ToolStripMenuItem miPause, miStartup;

        const string DefaultConfig =
@"# RamGuard list. One rule per line. Names have NO .exe and are not case sensitive.
# Put a # at the start of a line to turn that rule off.
#
# always:Name  = kill it every time it is found running
# idle:Name    = kill it only when it has NO visible window (hidden in tray / background)
#                for the grace time below. If you open its window, it is left alone.
#
# Settings
grace_seconds=30
check_seconds=10

# ---- Only when not in use ----
idle:msedge
idle:WhatsApp
idle:WhatsApp.Root
#idle:Spotify
#idle:Telegram
#idle:OneDrive

# ---- Always kill (Windows 11 extras) ----
always:Copilot
always:Widgets
always:WidgetService
always:WidgetBoard
always:PhoneExperienceHost
always:YourPhone
always:ms-teams
always:Teams
always:GameBar
always:GameBarPresenceWriter
always:Cortana
always:MicrosoftEdgeUpdate
always:Skype
always:SkypeApp
always:SkypeBackgroundHost
always:Clipchamp
always:GrooveMusic
always:Microsoft.Media.Player
";

        public TrayApp()
        {
            bool firstRun = !File.Exists(configPath);
            if (firstRun)
            {
                try { File.WriteAllText(configPath, DefaultConfig); }
                catch (Exception ex) { MessageBox.Show("Could not create list file:\n" + configPath + "\n\n" + ex.Message, "RamGuard"); }
                SetStartup(true); // on by default, can be turned off from the tray menu
            }
            LoadConfig();
            Log("RamGuard started. Admin=" + IsAdmin() + ". Folder=" + dataDir + ". Always rules=" + always.Count + ", idle rules=" + idle.Count);

            miPause = new ToolStripMenuItem("Cleaning is ON (click to pause)", null, delegate { TogglePause(); });
            miStartup = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                bool want = !IsStartup();
                SetStartup(want);
                if (IsStartup() != want) MessageBox.Show("Could not change startup setting. Check the log for the exit code.", "RamGuard");
                RefreshMenu();
            });
            RefreshMenu();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(miPause);
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Edit list (Notepad)", null, delegate { Process.Start("notepad.exe", "\"" + configPath + "\""); });
            menu.Items.Add("Reload list", null, delegate { LoadConfig(); Balloon("List reloaded"); });
            menu.Items.Add("Open folder", null, delegate { Process.Start("explorer.exe", "\"" + dataDir + "\""); });
            menu.Items.Add("Show log", null, delegate { if (File.Exists(logPath)) Process.Start("notepad.exe", "\"" + logPath + "\""); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { tray.Visible = false; Application.Exit(); });

            menu.Opening += delegate { RefreshMenu(); };
            tray.Icon = MakeIcon(Color.LimeGreen);
            tray.Text = IsAdmin() ? "RamGuard - running (admin)" : "RamGuard - running (NOT admin)";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            timer.Interval = checkSeconds * 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            if (firstRun) Balloon("RamGuard is running. Right-click the tray icon for options.");
        }

        void LoadConfig()
        {
            always.Clear(); idle.Clear(); hiddenSince.Clear();
            graceSeconds = 30; checkSeconds = 10;
            try
            {
                foreach (string raw in File.ReadAllLines(configPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("always:", StringComparison.OrdinalIgnoreCase)) always.Add(Clean(line.Substring(7)));
                    else if (line.StartsWith("idle:", StringComparison.OrdinalIgnoreCase)) idle.Add(Clean(line.Substring(5)));
                    else if (line.StartsWith("grace_seconds=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(14), out graceSeconds);
                    else if (line.StartsWith("check_seconds=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(14), out checkSeconds);
                }
            }
            catch { }
            if (graceSeconds < 5) graceSeconds = 5;
            if (checkSeconds < 3) checkSeconds = 3;
            timer.Interval = checkSeconds * 1000;
        }

        static string Clean(string s)
        {
            s = s.Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }

        void Tick()
        {
            if (paused) return;
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return; }

            int myId = Process.GetCurrentProcess().Id;
            Dictionary<string, List<Process>> byName = new Dictionary<string, List<Process>>();
            foreach (Process p in all)
            {
                string n;
                try { n = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                List<Process> l;
                if (!byName.TryGetValue(n, out l)) { l = new List<Process>(); byName[n] = l; }
                l.Add(p);
            }

            // Always-kill list
            foreach (string name in always)
            {
                List<Process> l;
                if (byName.TryGetValue(name, out l)) KillAll(name, l, "always");
            }

            // Idle list: kill only when no visible window for the grace time
            foreach (string name in idle)
            {
                List<Process> l;
                if (!byName.TryGetValue(name, out l)) { hiddenSince.Remove(name); continue; }

                bool hasWindow = false;
                foreach (Process p in l)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) { hasWindow = true; break; } } catch { }
                }

                if (hasWindow) { hiddenSince.Remove(name); continue; }

                DateTime since;
                if (!hiddenSince.TryGetValue(name, out since)) { hiddenSince[name] = DateTime.Now; continue; }
                if ((DateTime.Now - since).TotalSeconds >= graceSeconds)
                {
                    KillAll(name, l, "no window for " + graceSeconds + "s");
                    hiddenSince.Remove(name);
                }
            }

            foreach (Process p in all) p.Dispose();
        }

        void KillAll(string name, List<Process> list, string why)
        {
            int killed = 0;
            foreach (Process p in list)
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    p.Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    string key = name + ":" + SafeId(p);
                    if (failedLogged.Add(key)) Log("FAILED to close " + name + " (pid " + SafeId(p) + "): " + ex.Message);
                }
            }
            if (killed > 0) Log("Closed " + name + " (" + killed + " process, " + why + ")");
        }

        static int SafeId(Process p) { try { return p.Id; } catch { return -1; } }

        void Log(string msg)
        {
            try
            {
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 200000) fi.Delete(); // keep the log small
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        void RefreshMenu()
        {
            bool on = IsStartup();
            miStartup.Checked = on;
            miStartup.Text = "Start with Windows: " + (on ? "ON" : "OFF") + " (click to switch)";
        }

        void TogglePause()
        {
            paused = !paused;
            miPause.Text = paused ? "Cleaning is PAUSED (click to resume)" : "Cleaning is ON (click to pause)";
            tray.Text = paused ? "RamGuard - paused" : "RamGuard - running";
            Icon old = tray.Icon;
            tray.Icon = MakeIcon(paused ? Color.Orange : Color.LimeGreen);
            if (old != null) old.Dispose();
            hiddenSince.Clear();
        }

        static int RunHidden(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { p.WaitForExit(15000); return p.ExitCode; }
            }
            catch { return -1; }
        }

        bool IsStartup()
        {
            return RunHidden("schtasks.exe", "/Query /TN " + AppName) == 0;
        }

        void SetStartup(bool on)
        {
            // remove old-style startup entry from v1 so it never starts twice
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(AppName, false); } } catch { }

            int code;
            if (on)
                code = RunHidden("schtasks.exe", "/Create /TN " + AppName + " /TR \"\\\"" + Application.ExecutablePath + "\\\"\" /SC ONLOGON /RL HIGHEST /F");
            else
                code = RunHidden("schtasks.exe", "/Delete /TN " + AppName + " /F");
            Log("Startup " + (on ? "enable" : "disable") + " -> exit code " + code);
        }

        void Balloon(string text)
        {
            tray.ShowBalloonTip(3000, "RamGuard", text, ToolTipIcon.Info);
        }

        static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(b, 1, 1, 13, 13);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}

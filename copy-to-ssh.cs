// copy-to-ssh — native tray app.
// Watches the clipboard; on a screenshot, uploads it to an SSH host and puts
// the remote path on the clipboard, ready to paste into Claude Code / Codex.
// Build: build.cmd (uses the csc.exe that ships with Windows, no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Config
{
    public static string Host = "";
    public static string Mode = "ask";        // ask | auto
    public static string RemoteDir = "/tmp";

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "copy-to-ssh"); } }
    static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

    public static void Load()
    {
        if (!File.Exists(FilePath)) return;
        foreach (string line in File.ReadAllLines(FilePath))
        {
            int i = line.IndexOf('=');
            if (i < 1) continue;
            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
            if (k == "host") Host = v;
            else if (k == "mode") Mode = v;
            else if (k == "remoteDir") RemoteDir = v;
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(FilePath, new[] { "host=" + Host, "mode=" + Mode, "remoteDir=" + RemoteDir });
    }
}

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mtx = new System.Threading.Mutex(true, @"Local\copy-to-ssh", out created))
        {
            if (!created) return; // already running
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config.Load();
            if (Config.Host.Length == 0)
            {
                var hosts = GetSshHosts();
                if (hosts.Count > 0) { Config.Host = hosts[0]; Config.Save(); }
            }
            var form = new MainForm();
            if (Array.IndexOf(args, "--smoke") >= 0)
            {
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate { form.Shutdown(); Application.Exit(); };
                t.Start();
            }
            Application.Run(form);
        }
    }

    public static List<string> GetSshHosts()
    {
        var result = new List<string>();
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(p)) return result;
        foreach (string line in File.ReadAllLines(p))
        {
            Match m = Regex.Match(line, @"^\s*Host\s+(.+)$");
            if (!m.Success) continue;
            foreach (string name in m.Groups[1].Value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.IndexOfAny(new[] { '*', '?', '!' }) < 0 && !result.Contains(name))
                    result.Add(name);
        }
        return result;
    }

    public static bool IsStartupEnabled()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("copy-to-ssh") != null;
    }

    public static void SetStartup(bool on)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("copy-to-ssh", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("copy-to-ssh", false);
        }
    }
}

// One long-lived ssh connection; each send writes "name size\n" + raw bytes and
// waits for the remote loop's ok/fail line. Kills the per-send handshake that
// dominated upload latency. ssh+cat-style streaming, not scp: no temp files and
// it works with any ssh-config alias.
class Uploader
{
    Process proc;
    string procHost = "", procDir = "";
    string lastStderr = "";
    readonly object gate = new object();

    void Start()
    {
        StopLocked();
        procHost = Config.Host;
        procDir = Config.RemoteDir.TrimEnd('/');
        // sweep old clips once per connection, in the background, off the critical path;
        // dd count_bytes reads exactly <size> bytes (GNU coreutils — fine on Linux servers)
        string cmd = "( find " + procDir + " -maxdepth 1 -name 'clip-*.png' -mtime +1 -delete >/dev/null 2>&1 & ); " +
                     "while read -r n s; do if dd of=$n bs=65536 iflag=fullblock,count_bytes count=$s >/dev/null 2>&1; " +
                     "then echo ok; else echo fail; fi; done";
        var psi = new ProcessStartInfo();
        psi.FileName = "ssh";
        psi.Arguments = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=2 \"" + procHost + "\" \"" + cmd + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        proc = Process.Start(psi);
        proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (!string.IsNullOrEmpty(e.Data)) lastStderr = e.Data; };
        proc.BeginErrorReadLine();
    }

    void StopLocked()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(); }
        catch { }
        proc = null;
    }

    public void Stop() { lock (gate) StopLocked(); }

    // open (or re-open) the connection now so the first send doesn't pay the handshake
    public void Warm()
    {
        lock (gate)
        {
            try
            {
                if (proc == null || proc.HasExited || procHost != Config.Host || procDir != Config.RemoteDir.TrimEnd('/'))
                    Start();
            }
            catch { }
        }
    }

    public bool Send(string remote, byte[] data, out string err)
    {
        err = "";
        lock (gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (proc == null || proc.HasExited || procHost != Config.Host || procDir != Config.RemoteDir.TrimEnd('/'))
                        Start();
                    var stdin = proc.StandardInput.BaseStream;
                    byte[] header = System.Text.Encoding.ASCII.GetBytes(remote + " " + data.Length + "\n");
                    stdin.Write(header, 0, header.Length);
                    stdin.Write(data, 0, data.Length);
                    stdin.Flush();
                    var reply = proc.StandardOutput.ReadLineAsync();
                    if (!reply.Wait(15000)) throw new Exception("timed out waiting for the server");
                    if (reply.Result == "ok") return true;
                    throw new Exception(reply.Result == null ? "connection closed" : "remote write failed");
                }
                catch (Exception ex)
                {
                    err = lastStderr.Length > 0 ? lastStderr : ex.Message;
                    lastStderr = "";
                    StopLocked(); // retry once on a fresh connection
                }
            }
            return false;
        }
    }
}

class MainForm : Form
{
    readonly Uploader uploader = new Uploader();
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int WM_HOTKEY = 0x0312;

    NotifyIcon tray;
    ToolStripMenuItem hostMenu, askItem;
    string lastHash = "";

    public MainForm()
    {
        IntPtr force = Handle; // create the (hidden) window so we get clipboard messages
        AddClipboardFormatListener(force);
        RegisterHotKey(force, 1, 0x0002 | 0x0001, (uint)Keys.V); // Ctrl+Alt+V — silently unavailable if another app owns it
        tray = new NotifyIcon();
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); // the icon embedded by build.cmd
        UpdateTooltip();
        tray.Visible = true;
        tray.BalloonTipClicked += delegate { SendNow(); };
        tray.DoubleClick += delegate { ShowSettings(); };
        BuildMenu();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { uploader.Warm(); });
        tray.ShowBalloonTip(2500, "copy-to-ssh running",
            "Target: " + Config.Host + "  |  mode: " + Config.Mode + "  (right-click icon for options)", ToolTipIcon.Info);
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); } // tray-only, never show

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
        else if (m.Msg == WM_HOTKEY) { lastHash = ""; SendNow(); } // explicit send: bypass the sent-dedupe
        base.WndProc(ref m);
    }

    public void Shutdown() { tray.Visible = false; uploader.Stop(); }

    void UpdateTooltip()
    {
        string t = "copy-to-ssh → " + Config.Host;
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; // NotifyIcon.Text hard limit
    }

    byte[] GetClipboardPng()
    {
        if (!Clipboard.ContainsImage()) return null;
        Image img = Clipboard.GetImage();
        if (img == null) return null;
        using (var ms = new MemoryStream())
        {
            img.Save(ms, ImageFormat.Png);
            img.Dispose();
            return ms.ToArray();
        }
    }

    static string HashOf(byte[] data)
    {
        using (MD5 md5 = MD5.Create()) return Convert.ToBase64String(md5.ComputeHash(data));
    }

    string lastSeenHash = "";
    string lastRemote = "";

    void OnClipboardChanged()
    {
        byte[] png = GetClipboardPng();
        if (png == null) return;
        string hash = HashOf(png);
        // snipping tool re-writes the image after we set the remote path, clobbering it — put the path back
        if (hash == lastHash && lastRemote.Length > 0) { try { Clipboard.SetText(lastRemote); } catch { } return; }
        if (hash == lastSeenHash) return; // snipping tool / clipboard history fire duplicate updates for one image
        lastSeenHash = hash;
        if (Config.Mode == "auto") SendPng(png, hash);
        else tray.ShowBalloonTip(6000, "Screenshot captured", "Click here to send to " + Config.Host, ToolTipIcon.Info);
    }

    void SendNow()
    {
        byte[] png = GetClipboardPng();
        if (png == null) return;
        SendPng(png, HashOf(png));
    }

    void SendPng(byte[] png, string hash)
    {
        if (hash == lastHash) return; // already sent (or in flight)
        lastHash = hash;
        lastRemote = "";

        string name = "clip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        string remote = Config.RemoteDir.TrimEnd('/') + "/" + name;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string err;
            bool ok = uploader.Send(remote, png, out err);
            BeginInvoke((MethodInvoker)delegate
            {
                if (ok)
                {
                    lastRemote = remote;
                    Clipboard.SetText(remote);
                    tray.ShowBalloonTip(3000, "Sent to " + Config.Host, remote + "  — paste it in your SSH window", ToolTipIcon.Info);
                }
                else
                {
                    lastHash = "";     // allow retry with the same image
                    lastSeenHash = ""; // so duplicate clipboard events can auto-retry too
                    if (err.Length > 200) err = err.Substring(0, 200);
                    tray.ShowBalloonTip(4000, "Upload failed", err.Length > 0 ? err : "check host / key auth", ToolTipIcon.Error);
                }
            });
        });
    }

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        hostMenu = new ToolStripMenuItem("Send to");
        foreach (string h in Program.GetSshHosts())
        {
            var mi = new ToolStripMenuItem(h);
            mi.Checked = h == Config.Host;
            mi.Click += HostClicked;
            hostMenu.DropDownItems.Add(mi);
        }
        askItem = new ToolStripMenuItem("Ask before sending");
        askItem.CheckOnClick = true;
        askItem.Checked = Config.Mode != "auto";
        askItem.Click += delegate { Config.Mode = askItem.Checked ? "ask" : "auto"; Config.Save(); };

        var send = new ToolStripMenuItem("Send clipboard image now  (Ctrl+Alt+V)");
        send.Click += delegate { lastHash = ""; SendNow(); };
        var settings = new ToolStripMenuItem("Settings...");
        settings.Click += delegate { ShowSettings(); };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += delegate { tray.Visible = false; Application.Exit(); };

        menu.Items.Add(hostMenu);
        menu.Items.Add(askItem);
        menu.Items.Add(send);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        tray.ContextMenuStrip = menu;
    }

    void HostClicked(object sender, EventArgs e)
    {
        var mi = (ToolStripMenuItem)sender;
        Config.Host = mi.Text;
        Config.Save();
        UpdateTooltip();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { uploader.Warm(); }); // reconnect to the new host now
        foreach (ToolStripMenuItem x in hostMenu.DropDownItems) x.Checked = x.Text == Config.Host;
    }

    void ShowSettings()
    {
        using (var f = new SettingsForm())
        {
            if (f.ShowDialog() == DialogResult.OK)
            {
                BuildMenu(); // resync checkmarks
                UpdateTooltip();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { uploader.Warm(); });
            }
        }
    }
}

class SettingsForm : Form
{
    public SettingsForm()
    {
        Text = "copy-to-ssh settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(370, 175);

        var hostLbl = new Label { Text = "SSH host", Left = 12, Top = 16, Width = 90 };
        var hostBox = new ComboBox { Left = 110, Top = 12, Width = 245 };
        foreach (string h in Program.GetSshHosts()) hostBox.Items.Add(h);
        hostBox.Text = Config.Host;

        var dirLbl = new Label { Text = "Remote dir", Left = 12, Top = 47, Width = 90 };
        var dirBox = new TextBox { Left = 110, Top = 44, Width = 245, Text = Config.RemoteDir };

        var askBox = new CheckBox { Text = "Ask before sending (uncheck = always send)", Left = 110, Top = 74, Width = 250, Checked = Config.Mode != "auto" };
        var startBox = new CheckBox { Text = "Start at login", Left = 110, Top = 98, Width = 250, Checked = Program.IsStartupEnabled() };

        var ok = new Button { Text = "OK", Left = 199, Top = 136, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 280, Top = 136, Width = 75, DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += delegate
        {
            Config.Host = hostBox.Text.Trim();
            Config.RemoteDir = dirBox.Text.Trim();
            Config.Mode = askBox.Checked ? "ask" : "auto";
            Config.Save();
            Program.SetStartup(startBox.Checked);
        };
        Controls.AddRange(new Control[] { hostLbl, hostBox, dirLbl, dirBox, askBox, startBox, ok, cancel });
    }
}

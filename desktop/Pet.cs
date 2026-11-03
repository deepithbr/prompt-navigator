// The desk pet: a small fighter in a corner of the screen that shows what
// the task board knows without opening it.
//
// He stands with his arms crossed and his eyes shut while nothing runs. While
// any Claude or ChatGPT task works, a blue aura burns around him. When one
// finishes it flares gold, and when one needs you it pulses red, with a bubble
// naming the task. The bubble stays until you click him, which brings that
// task's app forward. A tray balloon was the only signal before, and Windows
// hides those under Do not disturb and drops them after a few seconds.
//
// The window is a layered window drawn with per-pixel alpha, so only the
// fighter and his bubble catch the mouse and everything around them clicks
// through.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class PetForm : Form
{
    enum Mood { Resting, Working, Done, Needs }

    // The canvas, in pixels at 100% scaling. The fighter stands bottom right
    // and the bubble sits above him, so the window can hug the screen corner.
    const int W = 300, H = 200, Stand = 84;

    static readonly Color Skin = Color.FromArgb(232, 190, 154);
    static readonly Color Hair = Color.FromArgb(30, 26, 34);
    static readonly Color Coat = Color.FromArgb(52, 56, 70);
    static readonly Color Sleeve = Color.FromArgb(64, 69, 86);
    static readonly Color Trousers = Color.FromArgb(38, 40, 50);
    static readonly Color Boots = Color.FromArgb(70, 46, 36);
    static readonly Color Band = Color.FromArgb(182, 38, 42);
    static readonly Color Belt = Color.FromArgb(196, 152, 62);
    static readonly Color Ink = Color.FromArgb(18, 16, 22);
    static readonly Color Blue = Color.FromArgb(91, 157, 187);
    static readonly Color Gold = Color.FromArgb(255, 200, 70);
    static readonly Color Red = Color.FromArgb(226, 70, 58);
    static readonly Color Green = Color.FromArgb(127, 163, 127);
    static readonly Color Orange = Color.FromArgb(217, 119, 87);
    static readonly Color Paper = Color.FromArgb(230, 228, 223);
    static readonly Color Dim = Color.FromArgb(150, 147, 141);

    readonly TaskBoard board;
    readonly Timer tick = new Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<TaskItem> alerts = new List<TaskItem>();
    readonly string settings;
    float scale = 1f;
    int working;
    double alertAt = -100;
    // A finish in the app you are already looking at gets a short flare and
    // bubble, and is not kept, so a Code session in front of you does not nag.
    TaskItem flash;
    double flashUntil;
    bool sound = true, placed;
    bool pressed, dragging;
    Point downAt, startAt;

    public bool Wanted = true;

    public PetForm(TaskBoard board)
    {
        this.board = board;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptAdvisor");
        settings = Path.Combine(dir, "pet.txt");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        Text = "Prompt Advisor · Pet";

        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add("Tasks", delegate { board.Show(); });
        menu.MenuItems.Add("Dismiss", delegate { alerts.Clear(); flash = null; });
        MenuItem chime = new MenuItem("Sound");
        chime.Click += delegate { sound = !sound; Save(); };
        menu.MenuItems.Add(chime);
        menu.MenuItems.Add("Hide pet", delegate { SetWanted(false); });
        menu.Popup += delegate { chime.Checked = sound; };
        ContextMenu = menu;

        MouseDown += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true; dragging = false;
            downAt = Cursor.Position; startAt = Location;
        };
        MouseMove += delegate
        {
            if (!pressed) return;
            Point p = Cursor.Position;
            if (!dragging && Math.Abs(p.X - downAt.X) + Math.Abs(p.Y - downAt.Y) > 4) dragging = true;
            if (dragging) Location = new Point(startAt.X + p.X - downAt.X, startAt.Y + p.Y - downAt.Y);
        };
        MouseUp += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (dragging) { dragging = false; Rescale(); Save(); }
            else Clicked();
        };

        board.Changed += delegate { if (IsHandleCreated) BeginInvoke((Action)Sync); };
        board.Alerted += delegate (TaskItem it) { if (IsHandleCreated) BeginInvoke((Action)delegate { Alert(it); }); };

        tick.Interval = 50;
        tick.Tick += delegate { Render(); };
        Load_();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Layered, kept off Alt+Tab, never takes focus from what you type in.
            cp.ExStyle |= 0x80000 | 0x80 | 0x8000000;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Rescale();
        // First run: bottom right, just above the taskbar, at this screen's scale.
        if (!placed)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 4, wa.Bottom - Height);
        }
        Sync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        tick.Enabled = Visible;
    }

    public void SetWanted(bool on)
    {
        Wanted = on;
        if (on) { Show(); Render(); } else Hide();
        Save();
    }

    /* -------------------------------------------------------------- *
     * What the board says
     * -------------------------------------------------------------- */

    void Sync()
    {
        List<TaskItem> all = board.Snapshot();
        Dictionary<string, TaskItem> byKey = new Dictionary<string, TaskItem>();
        working = 0;
        foreach (TaskItem it in all)
        {
            byKey[it.Key] = it;
            // A Claude Code turn you interrupted never sends its stop, so a
            // task silent for three hours no longer counts as running.
            if (it.State == "working" && (DateTime.Now - it.Updated).TotalHours < 3) working++;
        }
        // Gone from the board, or started again, means you have been back to it.
        alerts.RemoveAll(delegate (TaskItem a)
        {
            TaskItem now;
            return !byKey.TryGetValue(a.Key, out now) || now.State == "working";
        });
    }

    void Alert(TaskItem it)
    {
        Sync();
        double now = clock.Elapsed.TotalSeconds;
        alertAt = now;
        if (InFront(it))
        {
            flash = it;
            flashUntil = now + 4;
            return;
        }
        alerts.RemoveAll(delegate (TaskItem a) { return a.Key == it.Key; });
        alerts.Add(it);
        if (sound)
        {
            if (it.State == "needs") SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
    }

    static bool InFront(TaskItem it)
    {
        string proc = TaskBoard.ProcessFor(it.App);
        if (proc == null) return false;
        try
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return string.Equals(Process.GetProcessById((int)pid).ProcessName, proc, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    void Clicked()
    {
        if (alerts.Count == 0) { board.Show(); return; }
        TaskItem last = alerts[alerts.Count - 1];
        alerts.Clear();
        flash = null;
        board.Open(last);
    }

    /* -------------------------------------------------------------- *
     * Drawing
     * -------------------------------------------------------------- */

    void Rescale()
    {
        try { scale = Math.Max(1f, Native.GetDpiForWindow(Handle) / 96f); } catch { scale = 1f; }
        Size = new Size((int)(W * scale), (int)(H * scale));
    }

    void Render()
    {
        try
        {
            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.ScaleTransform(scale, scale);
                    Draw(g, clock.Elapsed.TotalSeconds);
                }
                Layered.Push(this, bmp);
            }
        }
        catch (Exception e)
        {
            // Never let a drawing fault take the board down with it.
            tick.Enabled = false;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  pet stopped drawing: " + e.Message + Environment.NewLine); } catch { }
        }
    }

    void Draw(Graphics g, double t)
    {
        if (flash != null && t >= flashUntil) flash = null;
        TaskItem shown = alerts.Count > 0 ? alerts[alerts.Count - 1] : flash;
        bool needs = alerts.Count > 0
            ? alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; })
            : flash != null && flash.State == "needs";
        Mood mood = needs ? Mood.Needs : shown != null ? Mood.Done
            : working > 0 ? Mood.Working : Mood.Resting;
        double since = t - alertAt;

        float lift = 0, shake = 0, power = 0;
        Color aura = Color.Empty;
        if (mood == Mood.Resting) lift = (float)(Math.Sin(t * 1.4) * 0.6);
        else if (mood == Mood.Working)
        {
            aura = Blue;
            power = 0.85f + 0.06f * (float)Math.Sin(t * 3);
            shake = (float)Math.Sin(t * 41) * 0.4f;
        }
        else if (mood == Mood.Done)
        {
            // A big flare for three seconds, hovering, then a steady glow.
            aura = Gold;
            double rise = Math.Min(1, since / 0.3), fall = since < 3 ? 1 : Math.Max(0, 1 - (since - 3) / 0.6);
            lift = (float)(7 * rise * fall);
            power = since < 3 ? 1.05f + 0.1f * (float)Math.Sin(t * 9) : 0.72f + 0.06f * (float)Math.Sin(t * 2.2);
        }
        else
        {
            aura = Red;
            power = 0.75f + 0.2f * (float)Math.Abs(Math.Sin(t * 4));
            shake = (float)Math.Sin(t * 53) * 0.8f;
        }

        float cx = W - 8 - Stand / 2f + shake, ground = H - 8;

        using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            float sw = 40 * (1 - lift / 30f);
            g.FillEllipse(b, cx - sw / 2, ground - 4, sw, 7);
        }

        GraphicsState saved = g.Save();
        g.TranslateTransform(cx, ground - 2 - lift);
        if (aura != Color.Empty) DrawAura(g, t, aura, power);
        DrawFighter(g, t, mood);
        g.Restore(saved);

        float top = ground - 2 - lift - 112;
        if (mood == Mood.Working && working > 1) DrawCount(g, cx, top);
        if (shown != null) DrawBubble(g, mood, shown, cx, top);
    }

    // Flames around him: an oval whose upper edge licks upward in tongues
    // that shift every frame.
    void DrawAura(Graphics g, double t, Color c, float power)
    {
        for (int layer = 0; layer < 2; layer++)
        {
            float s = layer == 0 ? 1f : 0.74f;
            float rx = 36 * power * s, ry = 56 * power * s, cy = -50;
            PointF[] pts = new PointF[56];
            for (int i = 0; i < pts.Length; i++)
            {
                double a = 2 * Math.PI * i / pts.Length;
                double up = Math.Max(0, -Math.Sin(a));
                double n = 0.06 * Math.Sin(a * 7 + t * 11 + layer * 2);
                double tongue = up * (i % 2 == 1 ? 0.28 + 0.22 * Math.Sin(a * 9 + t * 16 + layer) : 0.05);
                float x = (float)(Math.Cos(a) * rx * (1 + n) * (1 - up * 0.35));
                float y = (float)(cy + Math.Sin(a) * ry * (1 + n) - tongue * ry);
                pts[i] = new PointF(x, Math.Min(y, 2));
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(layer == 0 ? 70 : 110, c)))
                g.FillClosedCurve(b, pts, FillMode.Winding, 0.35f);
        }
    }

    // Origin at his feet, y going up as it goes negative.
    void DrawFighter(Graphics g, double t, Mood mood)
    {
        bool charged = mood != Mood.Resting;
        using (Pen line = new Pen(Ink, 1.6f))
        using (SolidBrush ink = new SolidBrush(Ink))
        using (SolidBrush skin = new SolidBrush(Skin))
        {
            line.LineJoin = LineJoin.Round;

            // Legs, planted apart, and boots.
            using (SolidBrush b = new SolidBrush(Trousers))
            {
                g.FillRectangle(b, -12, -36, 10, 28); g.DrawRectangle(line, -12, -36, 10, 28);
                g.FillRectangle(b, 2, -36, 10, 28); g.DrawRectangle(line, 2, -36, 10, 28);
            }
            using (SolidBrush b = new SolidBrush(Boots))
            {
                RectangleF l = new RectangleF(-15, -10, 14, 10), r = new RectangleF(1, -10, 14, 10);
                using (GraphicsPath p = Rounded(l, 3)) { g.FillPath(b, p); g.DrawPath(line, p); }
                using (GraphicsPath p = Rounded(r, 3)) { g.FillPath(b, p); g.DrawPath(line, p); }
            }

            // Coat, shoulders wider than the waist, with a belt.
            PointF[] coat = { new PointF(-19, -66), new PointF(19, -66), new PointF(14, -32), new PointF(-14, -32) };
            using (SolidBrush b = new SolidBrush(Coat)) g.FillPolygon(b, coat);
            g.DrawPolygon(line, coat);
            using (SolidBrush b = new SolidBrush(Belt)) g.FillRectangle(b, -14.5f, -38, 29, 5);
            g.DrawRectangle(line, -14.5f, -38, 29, 5);

            // Arms crossed over the chest.
            using (SolidBrush b = new SolidBrush(Sleeve))
            {
                g.FillEllipse(b, -25, -67, 11, 24); g.DrawEllipse(line, -25, -67, 11, 24);
                g.FillEllipse(b, 14, -67, 11, 24); g.DrawEllipse(line, 14, -67, 11, 24);
                RectangleF bar = new RectangleF(-22, -56, 44, 11);
                using (GraphicsPath p = Rounded(bar, 5)) { g.FillPath(b, p); g.DrawPath(line, p); }
            }
            g.FillEllipse(skin, -21, -58, 9, 8); g.DrawEllipse(line, -21, -58, 9, 8);
            g.FillEllipse(skin, 12, -54, 9, 8); g.DrawEllipse(line, 12, -54, 9, 8);

            // Neck and head.
            g.FillRectangle(skin, -4, -71, 8, 7);
            g.DrawLine(line, -4, -71, -4, -65); g.DrawLine(line, 4, -71, 4, -65);
            g.FillEllipse(skin, -12, -94, 24, 26);
            g.DrawEllipse(line, -12, -94, 24, 26);

            // Hair swept up in spikes, which stir when his power is up.
            float stir = charged ? (float)Math.Sin(t * 13) * 0.8f : 0;
            PointF[] hair =
            {
                new PointF(-13, -80), new PointF(-21, -93 + stir), new PointF(-13, -94),
                new PointF(-15, -108 + stir), new PointF(-6, -98), new PointF(-3, -117 - stir),
                new PointF(3, -100), new PointF(11, -114 + stir), new PointF(10, -98),
                new PointF(20, -106 - stir), new PointF(13, -91), new PointF(21, -88 + stir),
                new PointF(13, -80), new PointF(10, -87), new PointF(4, -85),
                new PointF(-1, -89), new PointF(-6, -85), new PointF(-11, -87)
            };
            using (SolidBrush b = new SolidBrush(Hair)) g.FillPolygon(b, hair);
            g.DrawPolygon(line, hair);

            // Headband, its tails trailing to one side.
            using (SolidBrush b = new SolidBrush(Band)) g.FillRectangle(b, -12, -87, 24, 4);
            float wave = (float)Math.Sin(t * (charged ? 6 : 1.5)) * (charged ? 3 : 1);
            using (Pen tail = new Pen(Band, 2.4f))
            {
                tail.StartCap = tail.EndCap = LineCap.Round;
                g.DrawBezier(tail, 12, -85, 18, -86, 22, -82 + wave, 28, -80 + wave);
                g.DrawBezier(tail, 12, -84, 17, -81, 20, -76 - wave, 25, -73 - wave);
            }

            // The face. Brows always set hard; the eyes and mouth carry the mood.
            using (Pen brow = new Pen(Ink, 1.8f))
            {
                brow.StartCap = brow.EndCap = LineCap.Round;
                float steep = mood == Mood.Needs ? 2.6f : 1.6f;
                g.DrawLine(brow, -9, -81 - steep, -3, -80);
                g.DrawLine(brow, 3, -80, 9, -81 - steep);

                if (mood == Mood.Resting)
                {
                    g.DrawLine(brow, -8, -77.5f, -4, -77);
                    g.DrawLine(brow, 4, -77, 8, -77.5f);
                }
                else
                {
                    float open = mood == Mood.Needs ? 3.4f : 2.4f;
                    // A blink now and then while he works.
                    if (mood == Mood.Working && (t % 4.3) < 0.1) open = 0.6f;
                    g.FillEllipse(Brushes.White, -8.5f, -78.5f, 5.5f, open);
                    g.FillEllipse(Brushes.White, 3, -78.5f, 5.5f, open);
                    float look = mood == Mood.Working ? (float)Math.Sin(t * 0.8) * 1.2f : 0;
                    g.FillEllipse(ink, -6.6f + look, -78.6f, 2.2f, open + 0.2f);
                    g.FillEllipse(ink, 4.6f + look, -78.6f, 2.2f, open + 0.2f);
                }

                if (mood == Mood.Done)
                {
                    // The smirk: one corner up.
                    g.DrawBezier(brow, -3, -72, 0, -72, 2, -72.2f, 4, -74);
                }
                else if (mood == Mood.Needs)
                {
                    g.FillEllipse(ink, -2.5f, -73.5f, 5, 3);
                }
                else
                {
                    g.DrawLine(brow, -2.5f, -72, 2.5f, -72.4f);
                }
            }
        }
    }

    void DrawCount(Graphics g, float cx, float top)
    {
        // More than one task running: say how many.
        using (SolidBrush b = new SolidBrush(Blue))
        using (Font f = new Font("Segoe UI", 8, FontStyle.Bold))
        {
            g.FillEllipse(b, cx + 18, top + 8, 17, 17);
            StringFormat mid = new StringFormat();
            mid.Alignment = mid.LineAlignment = StringAlignment.Center;
            g.DrawString(working.ToString(), f, Brushes.White, new RectangleF(cx + 18, top + 8, 17, 17), mid);
        }
    }

    void DrawBubble(Graphics g, Mood mood, TaskItem it, float cx, float top)
    {
        bool kept = alerts.Count > 0;
        string head = (mood == Mood.Needs ? "Needs you · " : "Done · ") + it.Source;
        string body = it.Title ?? "";
        string hint = !kept ? "" : alerts.Count > 1 ? "+" + (alerts.Count - 1) + " more · click to open" : "Click to open";

        using (Font fh = new Font("Segoe UI", 9, FontStyle.Bold))
        using (Font fb = new Font("Segoe UI", 9))
        using (Font fs = new Font("Segoe UI", 8))
        {
            float tw = Math.Max(g.MeasureString(head, fh).Width, Math.Max(g.MeasureString(body, fb).Width, g.MeasureString(hint, fs).Width));
            float w = Math.Min(W - 16, Math.Max(130, tw + 20));
            float h = hint.Length > 0 ? 62 : 44;
            float right = W - 8, bottomY = Math.Min(top - 8, 8 + h);
            RectangleF r = new RectangleF(right - w, bottomY - h, w, h);

            Color edge = mood == Mood.Needs ? Orange : Green;
            using (GraphicsPath path = Rounded(r, 8))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(242, 32, 31, 30)))
            using (Pen pen = new Pen(edge, 1.5f))
            {
                PointF[] tail = { new PointF(cx - 7, r.Bottom - 1), new PointF(cx + 7, r.Bottom - 1), new PointF(cx + 2, r.Bottom + 8) };
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                g.FillPolygon(fill, tail);
                g.DrawLine(pen, tail[1], tail[2]);
                g.DrawLine(pen, tail[2], tail[0]);
            }

            StringFormat cut = new StringFormat(StringFormatFlags.NoWrap);
            cut.Trimming = StringTrimming.EllipsisCharacter;
            using (SolidBrush b = new SolidBrush(edge)) g.DrawString(head, fh, b, new RectangleF(r.X + 10, r.Y + 6, r.Width - 20, 16), cut);
            using (SolidBrush b = new SolidBrush(Paper)) g.DrawString(body, fb, b, new RectangleF(r.X + 10, r.Y + 22, r.Width - 20, 16), cut);
            if (hint.Length > 0)
                using (SolidBrush b = new SolidBrush(Dim)) g.DrawString(hint, fs, b, new RectangleF(r.X + 10, r.Y + 40, r.Width - 20, 17), cut);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /* -------------------------------------------------------------- *
     * Where it sits, kept in %LOCALAPPDATA%\PromptAdvisor\pet.txt
     * -------------------------------------------------------------- */

    void Load_()
    {
        try
        {
            if (File.Exists(settings))
            {
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(settings))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1);
                }
                int x, y;
                if (kv.ContainsKey("x") && kv.ContainsKey("y") && int.TryParse(kv["x"], out x) && int.TryParse(kv["y"], out y))
                {
                    // Only if that spot is still on a screen.
                    foreach (Screen s in Screen.AllScreens)
                        if (s.WorkingArea.Contains(x + 200, y + 120)) { Location = new Point(x, y); placed = true; break; }
                }
                if (kv.ContainsKey("sound")) sound = kv["sound"] != "0";
                if (kv.ContainsKey("shown")) Wanted = kv["shown"] != "0";
            }
        }
        catch { }
    }

    void Save()
    {
        try
        {
            File.WriteAllText(settings, "x=" + Left + "\r\ny=" + Top + "\r\nsound=" + (sound ? 1 : 0) + "\r\nshown=" + (Wanted ? 1 : 0) + "\r\n");
        }
        catch { }
    }

    static class Layered
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public static void Push(Form f, Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                SIZE size = new SIZE(); size.cx = bmp.Width; size.cy = bmp.Height;
                POINT from = new POINT(), at = new POINT(); at.x = f.Left; at.y = f.Top;
                BLEND blend = new BLEND(); blend.Alpha = 255; blend.Format = 1; // AC_SRC_ALPHA
                UpdateLayeredWindow(f.Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}

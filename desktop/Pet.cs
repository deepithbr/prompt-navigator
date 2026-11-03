// The desk pet: a fighter's face in a corner of the screen that shows what
// the task board knows without opening it.
//
// His eyes stay shut while nothing runs. While any Claude or ChatGPT task
// works, a blue aura burns around him and his war marks glow. When one
// finishes the aura flares gold and his eyes blaze, and when one needs you it
// all burns red, with a bubble naming the task. The bubble stays until you click him, which brings that
// task's app forward. A tray balloon was the only signal before, and Windows
// hides those under Do not disturb and drops them after a few seconds.
//
// The window is a layered window drawn with per-pixel alpha, so only the
// face and his bubble catch the mouse and everything around them clicks
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

    // The canvas, in pixels at 100% scaling. His face sits bottom right
    // and the bubble sits above him, so the window can hug the screen corner.
    const int W = 300, H = 200, Span = 84;

    static readonly Color Skin = Color.FromArgb(226, 184, 148);
    static readonly Color Hair = Color.FromArgb(28, 24, 32);
    static readonly Color Scarf = Color.FromArgb(178, 36, 40);
    static readonly Color ScarfDark = Color.FromArgb(142, 26, 32);
    static readonly Color Mark = Color.FromArgb(150, 40, 44);
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

        float cx = W - 8 - Span / 2f + shake, ground = H - 8;

        GraphicsState saved = g.Save();
        g.TranslateTransform(cx, ground - 2 - lift);
        if (aura != Color.Empty) DrawAura(g, t, aura, power);
        if (mood == Mood.Needs || (mood == Mood.Done && since < 3)) DrawSparks(g, t, aura, power);
        DrawFace(g, t, mood, aura);
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
            float rx = 40 * power * s, ry = 52 * power * s, cy = -62;
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

    // A close-up of his head, origin below the scarf, y going up as it goes
    // negative. At rest his eyes are shut. With his power up the war marks on
    // his cheeks and brow light in the aura's colour, and when a task finishes
    // or needs you his eyes blaze with it too.
    void DrawFace(Graphics g, double t, Mood mood, Color aura)
    {
        bool blazing = mood == Mood.Done || mood == Mood.Needs;
        float wind = mood == Mood.Resting ? 0.3f : 1f;
        using (Pen line = new Pen(Ink, 1.5f))
        using (SolidBrush ink = new SolidBrush(Ink))
        using (SolidBrush skin = new SolidBrush(Skin))
        using (SolidBrush shade = new SolidBrush(Color.FromArgb(55, 40, 10, 20)))
        {
            line.LineJoin = LineJoin.Round;

            // The topknot's tail and the scarf's ends stream out behind him.
            float sway = (float)Math.Sin(t * (1.5 + 5 * wind)) * 3 * wind;
            using (Pen tail = new Pen(Hair, 3.2f))
            {
                tail.StartCap = tail.EndCap = LineCap.Round;
                g.DrawBezier(tail, 6, -106, 16, -110 + sway, 24, -104 - sway, 32, -100 + sway);
                g.DrawBezier(tail, 6, -104, 14, -104 - sway, 20, -96 + sway, 27, -92 - sway);
            }
            PointF[] ribbon = Ribbon(t, wind, 14, -20);
            using (SolidBrush b = new SolidBrush(ScarfDark)) g.FillPolygon(b, ribbon);
            g.DrawPolygon(line, ribbon);

            // Neck.
            g.FillRectangle(skin, -9, -40, 18, 22);
            g.FillRectangle(shade, -9, -40, 6, 22);
            g.DrawLine(line, -9, -40, -9, -20);
            g.DrawLine(line, 9, -40, 9, -20);

            // Ears, then the face: a long, angular jaw.
            g.FillEllipse(skin, -26, -67, 7, 13); g.DrawEllipse(line, -26, -67, 7, 13);
            g.FillEllipse(skin, 19, -67, 7, 13); g.DrawEllipse(line, 19, -67, 7, 13);
            PointF[] face =
            {
                new PointF(-22, -84), new PointF(22, -84), new PointF(21.5f, -63), new PointF(16, -46),
                new PointF(7, -36.5f), new PointF(0, -35), new PointF(-7, -36.5f), new PointF(-16, -46), new PointF(-21.5f, -63)
            };
            g.FillClosedCurve(skin, face, FillMode.Alternate, 0.25f);
            PointF[] cheek = { new PointF(-22, -82), new PointF(-12, -82), new PointF(-8, -50), new PointF(-7, -36.5f), new PointF(-16, -46), new PointF(-21.5f, -63) };
            g.FillClosedCurve(shade, cheek, FillMode.Alternate, 0.25f);
            g.DrawClosedCurve(line, face, 0.25f, FillMode.Alternate);

            // The scarf wound round his neck.
            RectangleF wrap = new RectangleF(-17, -26, 34, 13);
            using (GraphicsPath p = Rounded(wrap, 6))
            using (SolidBrush b = new SolidBrush(Scarf)) { g.FillPath(b, p); g.DrawPath(line, p); }
            g.DrawLine(line, -10, -24, -6, -15);
            g.DrawLine(line, 2, -25, 6, -15);

            // Hair swept straight back into a topknot, a few locks falling forward.
            PointF[] hair =
            {
                new PointF(-24, -66), new PointF(-25, -84), new PointF(-18, -98), new PointF(-4, -104),
                new PointF(10, -103), new PointF(22, -96), new PointF(25, -84), new PointF(24, -66),
                new PointF(20, -76), new PointF(15, -80), new PointF(10, -77), new PointF(5, -82),
                new PointF(-2, -73), new PointF(-4, -81), new PointF(-11, -79), new PointF(-17, -82), new PointF(-20, -74)
            };
            using (SolidBrush b = new SolidBrush(Hair)) g.FillPolygon(b, hair);
            g.DrawPolygon(line, hair);
            using (SolidBrush b = new SolidBrush(Hair)) g.FillEllipse(b, -3, -116, 13, 12);
            g.DrawEllipse(line, -3, -116, 13, 12);
            using (SolidBrush b = new SolidBrush(Scarf)) g.FillRectangle(b, -1, -106, 9, 3);

            // Light from the aura catches the far edge of his hair, face and neck.
            if (aura != Color.Empty)
                using (Pen rim = new Pen(Color.FromArgb(blazing ? 230 : 160, aura), 1.6f))
                {
                    rim.StartCap = rim.EndCap = LineCap.Round;
                    g.DrawCurve(rim, new PointF[] { new PointF(10, -103), new PointF(22, -96), new PointF(25, -84) }, 0.5f);
                    g.DrawCurve(rim, new PointF[] { new PointF(21.5f, -63), new PointF(16, -46), new PointF(7, -36.5f) }, 0.5f);
                    g.DrawLine(rim, 9.5f, -39, 9.5f, -27);
                }

            // War marks: two strokes down each cheek and one on the brow.
            // Dark red at rest, lit in the aura's colour once his power is up.
            Color markColour = aura == Color.Empty ? Mark : Blend(aura, Color.White, blazing ? 0.45f : 0.15f);
            if (aura != Color.Empty)
            {
                float glow = blazing ? 1f : 0.6f + 0.25f * (float)Math.Sin(t * 4);
                using (Pen halo = new Pen(Color.FromArgb((int)(110 * glow), aura), 5f))
                {
                    halo.StartCap = halo.EndCap = LineCap.Round;
                    Marks(g, halo);
                }
            }
            using (Pen mark = new Pen(markColour, 1.8f))
            {
                mark.StartCap = mark.EndCap = LineCap.Round;
                Marks(g, mark);
            }

            // Brows: heavy, set hard, steeper when something needs you.
            float steep = mood == Mood.Needs ? 5f : 3f;
            PointF[] browL = { new PointF(-17, -69 - steep), new PointF(-4, -66), new PointF(-4, -63.6f), new PointF(-17, -66.5f - steep) };
            PointF[] browR = { new PointF(17, -69 - steep), new PointF(4, -66), new PointF(4, -63.6f), new PointF(17, -66.5f - steep) };
            g.FillPolygon(ink, browL);
            g.FillPolygon(ink, browR);

            // Eyes.
            if (mood == Mood.Resting)
            {
                using (Pen lid = new Pen(Ink, 1.8f))
                {
                    lid.StartCap = lid.EndCap = LineCap.Round;
                    g.DrawLine(lid, -15, -59.5f, -6, -58.5f);
                    g.DrawLine(lid, 6, -58.5f, 15, -59.5f);
                }
            }
            else
            {
                float open = mood == Mood.Needs ? 5.5f : 4.2f;
                if (mood == Mood.Working && (t % 4.3) < 0.1) open = 0.8f;
                foreach (int side in new int[] { -1, 1 })
                {
                    PointF[] eye =
                    {
                        new PointF(side * 15.5f, -59.5f), new PointF(side * 11, -59.5f - open * 0.6f),
                        new PointF(side * 5.5f, -59f), new PointF(side * 10, -59.5f + open * 0.4f)
                    };
                    if (blazing)
                    {
                        // No pupils, only light.
                        using (SolidBrush halo = new SolidBrush(Color.FromArgb(120, aura)))
                            g.FillEllipse(halo, side * 10.5f - 8, -64, 16, 10);
                        using (SolidBrush b = new SolidBrush(Blend(aura, Color.White, 0.55f)))
                            g.FillClosedCurve(b, eye, FillMode.Alternate, 0.3f);
                    }
                    else
                    {
                        g.FillClosedCurve(Brushes.White, eye, FillMode.Alternate, 0.3f);
                        float look = (float)Math.Sin(t * 0.8) * 1.6f;
                        g.FillEllipse(ink, side * 10 - 1.8f + look, -59.5f - open * 0.5f, 3.6f, open);
                    }
                    g.DrawClosedCurve(line, eye, 0.3f, FillMode.Alternate);
                }
            }

            // Nose and mouth.
            g.DrawLine(line, 0.5f, -55, 2.5f, -48.5f);
            g.DrawLine(line, 2.5f, -48.5f, -0.5f, -48);
            using (Pen lip = new Pen(Ink, 1.6f))
            {
                lip.StartCap = lip.EndCap = LineCap.Round;
                if (mood == Mood.Done)
                    g.DrawBezier(lip, -5, -42.5f, -1, -42.5f, 3, -42.8f, 6, -45.5f);
                else if (mood == Mood.Needs)
                {
                    g.FillRectangle(Brushes.White, -5, -44, 10, 3);
                    g.DrawRectangle(lip, -5, -44, 10, 3);
                    g.DrawLine(line, -5, -42.5f, 5, -42.5f);
                }
                else
                    g.DrawLine(lip, -4.5f, -42.5f, 4.5f, -43);
            }
        }
    }

    static void Marks(Graphics g, Pen p)
    {
        g.DrawLine(p, -12, -54, -14, -47);
        g.DrawLine(p, -8.5f, -54, -10.5f, -47);
        g.DrawLine(p, 12, -54, 14, -47);
        g.DrawLine(p, 8.5f, -54, 10.5f, -47);
        g.DrawLine(p, 0, -80, 0, -75);
    }

    // A strip of cloth trailing from (x, y), rippling harder in a stronger wind.
    static PointF[] Ribbon(double t, float wind, float x0, float y0)
    {
        const int n = 9;
        PointF[] pts = new PointF[n * 2];
        for (int i = 0; i < n; i++)
        {
            float x = x0 + i * 4.4f;
            float y = y0 + i * (1.8f - wind * 1.1f) + (float)Math.Sin(t * (2 + 6 * wind) - i * 0.9) * i * 0.5f * (0.4f + wind);
            float half = 3.2f - i * 0.22f;
            pts[i] = new PointF(x, y - half);
            pts[n * 2 - 1 - i] = new PointF(x, y + half);
        }
        return pts;
    }

    // Short bolts that jump around him, a new set every eighth of a second.
    static void DrawSparks(Graphics g, double t, Color c, float power)
    {
        Random r = new Random((int)(t * 8));
        using (Pen p = new Pen(Color.FromArgb(235, Blend(c, Color.White, 0.6f)), 1.4f))
        {
            for (int k = 0; k < 3; k++)
            {
                double a = r.NextDouble() * Math.PI * 2;
                float x = (float)Math.Cos(a) * 36 * power, y = -62 + (float)Math.Sin(a) * 46 * power;
                PointF[] bolt = new PointF[4];
                for (int i = 0; i < bolt.Length; i++)
                {
                    bolt[i] = new PointF(x, y);
                    x += (float)(Math.Cos(a) * 5 + r.NextDouble() * 8 - 4);
                    y += (float)(Math.Sin(a) * 5 + r.NextDouble() * 8 - 4);
                }
                g.DrawLines(p, bolt);
            }
        }
    }

    static Color Blend(Color a, Color b, float k)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
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

// The desk pet: an energy core in a corner of the screen that shows what
// the task board knows without opening it.
//
// It glows dim grey and barely turns while nothing runs. While any Claude or
// ChatGPT task works it turns blue and spins. When one finishes it bursts
// gold, and when one needs you it pulses red, with a bubble naming the task.
// The bubble stays until you click the core, which brings that task's app
// forward. A tray balloon was the only signal before, and Windows hides those
// under Do not disturb and drops them after a few seconds.
//
// By default it only appears when there is something to show: it fades in
// when a task starts or finishes, and fades out once nothing has run and
// nothing has waited on you for 30 seconds. "Always" keeps it on screen and
// "Never" keeps it away; the tray menu's Pet item picks between the three.
//
// The window is a layered window drawn with per-pixel alpha, so only the core
// and its bubble catch the mouse and everything around them clicks through.

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
    enum Mood { Resting, Hidden, Thinking, Working, Done, Needs }

    // The canvas, in pixels at 100% scaling. The core sits bottom right and
    // the bubble above it, so the window can hug the screen corner.
    const int W = 300, H = 200, Span = 84;

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
    // A finish in the app you are already looking at gets a short burst and
    // bubble, and is not kept, so a Code session in front of you does not nag.
    TaskItem flash;
    double flashUntil;
    bool sound = true, placed;
    bool pressed, dragging;
    Point downAt, startAt;

    // "auto" shows it only while something is running or waiting on you,
    // "always" keeps it on screen, "off" never shows it.
    public string Mode = "auto";
    const double IdleHide = 30;
    double lastBusy = -1000;
    float fade = 1f;
    bool leaving, snoozed;
    Canvas canvas;

    // The bubble's close cross, in canvas pixels; empty with no bubble.
    RectangleF closeBox = RectangleF.Empty;
    // The bubble itself, in canvas pixels; empty with no bubble.
    RectangleF bubbleBox = RectangleF.Empty;
    bool overClose;
    int lastWorking, hiddenRuns;
    double workingSince = -100;
    // Battery saver on: half the motes and ten frames a second.
    bool battery;
    // Windows' "Animation effects" off: slower, gentler motion, never fewer frames.
    bool still;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Battery, Percent, SaverFlag; public int Life, Full; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, out bool value, int ini);

    // Read now and then, not every frame: both change rarely.
    void ReadSettings()
    {
        PowerStatus ps;
        battery = GetSystemPowerStatus(out ps) && (ps.SaverFlag & 1) == 1;
        bool animate;
        still = SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out animate, 0) && !animate;
    }
    // Alerts put away by hand, as key@time, so a request that is still open
    // on the board does not come straight back; a newer one does.
    readonly HashSet<string> dismissed = new HashSet<string>();
    int ticks, faults;

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
        menu.MenuItems.Add("Dismiss all", delegate { DismissAll(); });
        MenuItem chime = new MenuItem("Sound");
        chime.Click += delegate { sound = !sound; Save(); };
        menu.MenuItems.Add(chime);
        // Away until the next task starts or finishes, whatever the mode.
        menu.MenuItems.Add("Hide until the next task", delegate { DismissAll(); snoozed = true; Hide(); });
        menu.Popup += delegate { chime.Checked = sound; };
        ContextMenu = menu;

        MouseDown += delegate (object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true; dragging = false;
            downAt = Cursor.Position; startAt = Location;
        };
        MouseMove += delegate (object s, MouseEventArgs e)
        {
            overClose = closeBox.Contains(e.X / scale, e.Y / scale);
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
            else if (closeBox.Contains(e.X / scale, e.Y / scale)) { DismissShown(); overClose = false; }
            else Clicked();
        };
        MouseLeave += delegate { overClose = false; };

        board.Changed += delegate { if (IsHandleCreated) BeginInvoke((Action)Sync); };
        board.Alerted += delegate (TaskItem it) { if (IsHandleCreated) BeginInvoke((Action)delegate { Alert(it); }); };

        // Held back over a full-screen app: look every 2 seconds for the
        // screen to be yours again, then show what waited.
        holdCheck.Interval = 2000;
        holdCheck.Tick += delegate
        {
            if (ScreenBusy()) return;
            holdCheck.Enabled = false;
            held = false;
            Sync();
        };

        // The timer runs the pet's logic: fading, reading the board,
        // passing clicks. Frames are drawn by FrameLoop, in step with the screen.
        tick.Interval = 50;
        tick.Tick += delegate
        {
            // The board's state also changes with time alone: a run silent
            // for hours stops counting. Read it again about once a second.
            if (++ticks % 10 == 0) Sync();
            if (ticks % 50 == 1) ReadSettings();
            Fade();
            if (Visible) PassClicks();
        };
        Load_();
    }

    // Called once at start-up: the window exists from then on, hidden or
    // not, so the board's events always reach it.
    public void Start()
    {
        IntPtr unused = Handle;
        if (Mode == "always") Present();
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

    protected override void Dispose(bool disposing)
    {
        framesOn = false;
        if (disposing)
        {
            if (canvas != null) canvas.Dispose();
            foreach (Bitmap b in glows.Values) b.Dispose();
            glows.Clear();
        }
        base.Dispose(disposing);
    }

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
        if (Visible && frames == null)
        {
            framesOn = true;
            frames = new System.Threading.Thread(FrameLoop);
            frames.IsBackground = true;
            frames.Start();
        }
        else if (!Visible) { framesOn = false; frames = null; }
    }

    /* -------------------------------------------------------------- *
     * Frames, one per screen refresh. A WinForms timer ticks in 15.6 ms
     * steps and drifts, so motion drawn on it judders however fast it is
     * set. DwmFlush returns as Windows finishes composing each refresh, so
     * a frame drawn after it lands once per refresh, evenly: 60 a second on
     * a 60 Hz screen, every other refresh on a faster one. Battery saver
     * halves that. Windows' animations setting never lowers the frame rate,
     * which only makes motion look broken; it calms the motion instead.
     * -------------------------------------------------------------- */

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    volatile bool framesOn;
    System.Threading.Thread frames;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    // Refreshes to skip between frames, so a fast screen is not drawn for
    // at its full rate: 60 Hz draws every refresh, 120 and 144 Hz every
    // other one, an even 60 or 72 a second.
    static int RefreshStep()
    {
        int hz = 60;
        try { IntPtr dc = GetDC(IntPtr.Zero); hz = GetDeviceCaps(dc, 116 /* VREFRESH */); ReleaseDC(IntPtr.Zero, dc); } catch { }
        if (hz < 30 || hz > 500) hz = 60;
        return Math.Max(1, (int)Math.Round(hz / 60.0));
    }

    void FrameLoop()
    {
        int n = 0, drawn = 0, step = RefreshStep();
        System.Diagnostics.Stopwatch since = System.Diagnostics.Stopwatch.StartNew();
        bool reported = false;
        while (framesOn)
        {
            // Once, ten seconds after it appears: the rate it actually drew at.
            if (!reported && since.Elapsed.TotalSeconds >= 10)
            {
                reported = true;
                LogNote("pet drew " + drawn + " frames in 10 s (" + (drawn / 10) + " a second)");
            }
            if (DwmFlush() != 0) System.Threading.Thread.Sleep(16);
            // Battery saver: half as many frames, still evenly spaced.
            if (++n % (battery ? step * 2 : step) != 0) continue;
            try
            {
                if (!framesOn || IsDisposed) break;
                // Drawn on the window's own thread, and waited for, so frames
                // never queue up behind each other.
                Invoke((Action)delegate { if (Visible) Render(); });
                drawn++;
            }
            catch { break; }
        }
    }

    static void LogNote(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  " + line + Environment.NewLine); } catch { }
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        Save();
        if (mode == "off") Hide();
        else if (mode == "always" || Busy()) Present();
        else if (Visible) leaving = true;
    }

    bool Busy() { return working > 0 || alerts.Count > 0 || flash != null; }

    // Motes and glow drift out past the creature, and a layered window
    // catches the mouse wherever it has drawn anything, so a faint mote over
    // another app would take your click. Only the creature and its bubble
    // take clicks; elsewhere the window lets them through to what is below.
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    const int ExStyle = -20, ClickThrough = 0x20;
    bool passing;

    void PassClicks()
    {
        Point c = PointToClient(Cursor.Position);
        float x = c.X / scale, y = c.Y / scale;
        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        bool onIt = pressed || bubbleBox.Contains(x, y) || (x - cx) * (x - cx) + (y - cy) * (y - cy) < 38 * 38;
        if (onIt == !passing) return;
        passing = !onIt;
        int ex = GetWindowLong(Handle, ExStyle);
        SetWindowLong(Handle, ExStyle, passing ? ex | ClickThrough : ex & ~ClickThrough);
    }

    bool Lively()
    {
        return leaving || fade < 1 || flash != null
            || clock.Elapsed.TotalSeconds - alertAt < 3.5
            || alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; });
    }

    // Fade in, from wherever it was.
    void Present()
    {
        if (Mode == "off") return;
        // Over a full-screen app or a presentation, hold everything back;
        // it shows once the screen is yours again.
        if (ScreenBusy()) { held = true; holdCheck.Enabled = true; if (Visible) Hide(); return; }
        leaving = false;
        if (!Visible) { fade = 0; Show(); }
        if (!tick.Enabled) { faults = 0; tick.Enabled = true; }
        Render();
    }

    // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE: a
    // full-screen app, a game or video, or presentation settings switched on.
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    readonly Timer holdCheck = new Timer();
    bool held;

    static bool ScreenBusy()
    {
        int st;
        try { if (SHQueryUserNotificationState(out st) != 0) return false; } catch { return false; }
        return st == 2 || st == 3 || st == 4;
    }

    // Each tick: in "auto", start leaving once idle for 30 seconds; then
    // move the fade a step toward where it is heading.
    void Fade()
    {
        double now = clock.Elapsed.TotalSeconds;
        if (Busy()) { lastBusy = now; leaving = false; }
        else if (Mode == "auto" && now - lastBusy > IdleHide) leaving = true;
        if (leaving)
        {
            fade -= 0.06f;
            if (fade <= 0) { fade = 0; leaving = false; Hide(); }
        }
        else if (fade < 1) fade = Math.Min(1f, fade + 0.12f);
    }

    /* -------------------------------------------------------------- *
     * What the board says
     * -------------------------------------------------------------- */

    void Sync()
    {
        List<TaskItem> all = board.Snapshot();
        Dictionary<string, TaskItem> byKey = new Dictionary<string, TaskItem>();
        int was = working, hidden = 0;
        working = 0;
        foreach (TaskItem it in all)
        {
            byKey[it.Key] = it;
            // A Claude Code turn you interrupted never sends its stop, so a
            // task silent for three hours no longer counts as running.
            if (it.State == "working" && (DateTime.Now - it.Updated).TotalHours < 3) working++;
            if (it.State == "hidden" && (DateTime.Now - it.Updated).TotalHours < 3) hidden++;
        }
        // Gone from the board, or started again, means you have been back to it.
        alerts.RemoveAll(delegate (TaskItem a)
        {
            TaskItem now;
            return !byKey.TryGetValue(a.Key, out now) || now.State == "working";
        });
        // A request that still waits on you is shown, even if its alert was
        // lost to a restart, unless you put it away yourself.
        foreach (TaskItem it in all)
            if (it.State == "needs" && (DateTime.Now - it.Updated).TotalHours < 3
                && !dismissed.Contains(Stamp(it)) && !alerts.Exists(delegate (TaskItem a) { return a.Key == it.Key; }))
                alerts.Add(it);
        hiddenRuns = hidden;
        if (working > 0 && was == 0) workingSince = clock.Elapsed.TotalSeconds;
        // Hidden by hand, it stays away until a task starts that was not
        // already running then.
        if (working > lastWorking) snoozed = false;
        lastWorking = working;
        if (Busy() && !snoozed) { lastBusy = clock.Elapsed.TotalSeconds; Present(); }
    }

    void Alert(TaskItem it)
    {
        Sync();
        double now = clock.Elapsed.TotalSeconds;
        alertAt = now;
        // The board already decided this is news. It cannot tell for Claude
        // Code, whose hooks do not say whether you are looking, so a finish
        // there while the Claude app is in front only flashes.
        if ((it.Source ?? "").StartsWith("Claude Code") && InFront(it))
        {
            flash = it;
            flashUntil = now + 4;
            lastBusy = now;
            Present();
            return;
        }
        alerts.RemoveAll(delegate (TaskItem a) { return a.Key == it.Key; });
        alerts.Add(it);
        lastBusy = now;
        snoozed = false;
        Present();
        if (sound && Mode != "off" && !held)
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

    // A click opens the task in the bubble and puts away only that one;
    // others still waiting take its place.
    void Clicked()
    {
        if (alerts.Count == 0) { board.Show(); return; }
        TaskItem last = alerts[alerts.Count - 1];
        DismissShown();
        board.Open(last);
    }

    static string Stamp(TaskItem it) { return it.Key + "@" + it.Updated.Ticks; }

    void DismissShown()
    {
        flash = null;
        if (alerts.Count == 0) return;
        TaskItem last = alerts[alerts.Count - 1];
        dismissed.Add(Stamp(last));
        alerts.RemoveAt(alerts.Count - 1);
    }

    void DismissAll()
    {
        foreach (TaskItem a in alerts) dismissed.Add(Stamp(a));
        alerts.Clear();
        flash = null;
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
            if (canvas == null || canvas.Width != Width || canvas.Height != Height)
            {
                if (canvas != null) canvas.Dispose();
                canvas = new Canvas(Width, Height);
            }
            using (Graphics g = Graphics.FromImage(canvas.Image))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(scale, scale);
                Point m = PointToClient(Cursor.Position);
                UpdateGaze(m.X / scale, m.Y / scale, clock.Elapsed.TotalSeconds);
                Draw(g, clock.Elapsed.TotalSeconds);
            }
            canvas.Push(this, (byte)(255 * fade));
            faults = 0;
        }
        catch (Exception e)
        {
            // Never let a drawing fault take the board down with it. One bad
            // frame is skipped. Only a long run of them stops the drawing, and
            // the next time the pet is shown it tries again.
            if (++faults == 1 || faults % 100 == 0) LogFault(e);
            if (faults >= 200) tick.Enabled = false;
        }
    }

    void LogFault(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "PromptAdvisor.log"), DateTime.Now.ToString("s") + "  pet could not draw (" + faults + " in a row): " + e.Message + Environment.NewLine); } catch { }
    }

    void Draw(Graphics g, double t)
    {
        closeBox = RectangleF.Empty;
        bubbleBox = RectangleF.Empty;
        if (flash != null && t >= flashUntil) flash = null;
        TaskItem shown = alerts.Count > 0 ? alerts[alerts.Count - 1] : flash;
        bool needs = alerts.Count > 0
            ? alerts.Exists(delegate (TaskItem a) { return a.State == "needs"; })
            : flash != null && flash.State == "needs";
        Mood mood = needs ? Mood.Needs : shown != null ? Mood.Done
            : working > 0 ? (t - workingSince < 4 ? Mood.Thinking : Mood.Working)
            : hiddenRuns > 0 ? Mood.Hidden : Mood.Resting;

        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        GraphicsState saved = g.Save();
        g.TranslateTransform(cx, cy);
        DrawVoidling(g, t, mood, t - alertAt);
        g.Restore(saved);

        float top = cy - 34;
        if ((mood == Mood.Working || mood == Mood.Thinking) && working > 1) DrawCount(g, cx, top);
        if (shown != null) DrawBubble(g, mood, shown, cx, top);
    }

    // Voidling, centred on the origin: a tiny black hole with a tilted
    // accretion ring, a few orbiting motes and two slits of light for eyes.
    // The far half of the ring and of the motes is drawn before the core and
    // the near half after it, so the void sits inside its own ring.
    //
    //   resting    slow drift, a dim indigo ring, a gentle bob
    //   hidden     dimmer and slower still, eyes shut: a run you cannot see
    //   thinking   the first seconds of a run: ring spins fast, core tightens
    //   working    cyan ring, livelier motes, small energy arcs
    //   done       a starlight ripple and sparkles, then a calm cyan glow
    //   needs      a magenta ring that flickers and warps, eyes narrowed
    static readonly Color Void = Color.FromArgb(5, 4, 12);
    static readonly Color Cyan = Color.FromArgb(96, 226, 255);
    static readonly Color Violet = Color.FromArgb(150, 112, 255);
    static readonly Color Indigo = Color.FromArgb(80, 70, 205);
    static readonly Color Ember = Color.FromArgb(240, 84, 58);
    static readonly Color Starlight = Color.FromArgb(242, 247, 255);

    // Each mote: orbit size (times the ring), phase, speed, size, brightness.
    static readonly float[][] Motes = MakeMotes(22);

    static float[][] MakeMotes(int n)
    {
        Random r = new Random(7);
        float[][] m = new float[n][];
        for (int i = 0; i < n; i++)
            m[i] = new float[] { 0.78f + (float)r.NextDouble() * 0.6f, (float)(r.NextDouble() * Math.PI * 2),
                0.6f + (float)r.NextDouble() * 0.9f, 1f + (float)r.NextDouble() * 1.4f, 0.4f + (float)r.NextDouble() * 0.6f };
        return m;
    }

    // Whether the pointer is near: Voidling turns its ring toward you and
    // brightens a little, eased so it never snaps.
    bool noticed;
    float attend, attendX;

    void UpdateGaze(float px, float py, double t)
    {
        float cx = W - 8 - Span / 2f, cy = H - 8 - Span / 2f;
        float dx = px - cx, dy = py - cy;
        noticed = dx * dx + dy * dy < 90 * 90;
        attend += ((noticed ? 1f : 0f) - attend) * 0.06f;
        float tx = noticed ? Math.Max(-1f, Math.Min(1f, dx / 70f)) : 0f;
        attendX += (tx - attendX) * 0.06f;
    }

    // Voidling, centred on the origin: a tiny black hole with a tilted
    // accretion ring and a few orbiting motes. No face: its mood is in how
    // the ring turns, tightens and glows, and how the void breathes.
    //
    //   resting    slow indigo drift; after a long quiet, slower and dimmer
    //   hidden     dimmer still, nearly still: a run you cannot see
    //   thinking   the first seconds of a run: ring races, void tightens
    //   working    cyan ring, livelier motes, a current of energy
    //   done       a ripple of starlight, a rise, and the ring flips once
    //   needs      a deep red ring that warps slowly and breathes
    void DrawVoidling(Graphics g, double t, Mood mood, double since)
    {
        bool burst = mood == Mood.Done && since < 3;
        bool deep = mood == Mood.Resting && t - lastBusy > 25 && !noticed;
        float spin, core, ringR, energy, glow, wobble = 0, flicker = 1, lift = 0, squash = 0;
        Color ring, halo;
        switch (mood)
        {
            case Mood.Hidden:
                spin = 0.22f; core = 16; ringR = 33; energy = 0.12f; ring = Blend(Indigo, Void, 0.4f); halo = Indigo; glow = 0.1f; break;
            case Mood.Thinking:
                spin = 2.6f; core = 14.5f; ringR = 31; energy = 0.8f; ring = Violet; halo = Violet; glow = 0.45f; break;
            case Mood.Working:
                spin = 1.6f; core = 15.5f; ringR = 35; energy = 1f; ring = Cyan; halo = Blend(Cyan, Indigo, 0.4f);
                glow = 0.46f + 0.06f * (float)Math.Sin(t * 1.8); break;
            case Mood.Done:
                spin = burst ? 2f : 0.7f; core = 16; ringR = 35; energy = 0.7f; ring = Blend(Cyan, Starlight, burst ? 0.5f : 0.15f);
                halo = Cyan; glow = burst ? 0.55f : 0.4f + 0.04f * (float)Math.Sin(t * 1.5);
                // A rise and settle, eased at both ends.
                if (since < 1.4) { float u = (float)(since / 1.4); lift = 7 * (float)Math.Sin(u * Math.PI); squash = -0.05f * (float)Math.Sin(u * Math.PI); }
                break;
            case Mood.Needs:
                spin = 1.1f; core = 16; ringR = 34; energy = 0.75f; ring = Ember; halo = Ember;
                // Uneasy, never glitchy: a slow warp, a slow breathing of light.
                wobble = 2f * (float)Math.Sin(t * 2.2) * (float)Math.Sin(t * 0.9);
                flicker = 0.82f + 0.18f * (float)Math.Sin(t * 3.4);
                glow = 0.42f + 0.22f * (float)(0.5 + 0.5 * Math.Sin(t * 2.4)); break;
            default:
                spin = deep ? 0.28f : 0.45f; core = 16; ringR = 34; energy = deep ? 0.18f : 0.32f;
                ring = Blend(Indigo, Violet, 0.3f); halo = Indigo; glow = (deep ? 0.2f : 0.26f) + 0.04f * (float)Math.Sin(t * 1.1);
                break;
        }
        // Noticing you: a little brighter, the ring turned your way.
        glow += 0.08f * attend; energy += 0.15f * attend;

        // A snack: now and then a mote spirals in and the void swells as it
        // swallows it.
        double snack = mood == Mood.Resting || mood == Mood.Working ? (t % 13) / 13 : 0;
        float gulp = snack > 0.965 ? (float)Math.Sin((snack - 0.965) / 0.035 * Math.PI) : 0;

        if (still) { spin *= 0.4f; wobble *= 0.4f; lift *= 0.4f; }
        float bob = (float)Math.Sin(t * 1.05) * (mood == Mood.Resting || mood == Mood.Hidden ? 2f : 1.1f) * (still ? 0.3f : 1f);
        float breath = (float)Math.Sin(t * (deep || mood == Mood.Hidden ? 0.8 : 1.4)) * 0.022f;
        float turn = -16 + wobble + attendX * 9;
        g.TranslateTransform(0, bob - lift);

        Bitmap h = Glow(halo, glow);
        g.DrawImage(h, -GlowR, -GlowR, 2 * GlowR, 2 * GlowR);

        float a0 = (float)(t * spin);
        float flip = burst && since < 1.4 ? (float)Math.Cos(since / 1.4 * Math.PI * 2) : 1;
        float rx = ringR, ry = (ringR * 0.34f + wobble * 0.3f) * flip;
        GraphicsState level = g.Save();
        g.RotateTransform(turn);
        DrawRing(g, a0, rx, ry, ring, flicker, true);
        DrawMotes(g, t, a0, rx, ry, ring, energy, flicker, true);
        g.Restore(level);

        // The void, breathing and swelling as it swallows.
        float sx = core * (1 + squash + breath + 0.07f * gulp), sy = core * (1 - squash - breath * 0.6f + 0.05f * gulp);
        using (SolidBrush b = new SolidBrush(Void)) g.FillEllipse(b, -sx, -sy, 2 * sx, 2 * sy);
        using (Pen rim = new Pen(Color.FromArgb((int)(225 * flicker), Blend(ring, Starlight, 0.35f)), 1.6f))
            g.DrawEllipse(rim, -sx - 0.6f, -sy - 0.6f, 2 * sx + 1.2f, 2 * sy + 1.2f);
        using (Pen lens = new Pen(Color.FromArgb(38, Starlight), 1f))
            g.DrawEllipse(lens, -sx - 4, -sy - 4, 2 * sx + 8, 2 * sy + 8);
        // The far side of the ring, its light bent up and over the void.
        using (Pen bent = new Pen(Color.FromArgb((int)(150 * flicker), Blend(ring, Starlight, 0.25f)), 2.2f))
        {
            bent.StartCap = bent.EndCap = LineCap.Round;
            g.DrawArc(bent, -sx - 2.6f, -sy - 2.6f, 2 * sx + 5.2f, 2 * sy + 5.2f, 196, 148);
        }

        level = g.Save();
        g.RotateTransform(turn);
        DrawRing(g, a0, rx, ry, ring, flicker, false);
        DrawMotes(g, t, a0, rx, ry, ring, energy, flicker, false);
        if (mood == Mood.Working || mood == Mood.Thinking) DrawArcs(g, t, rx, ry, core, ring);
        g.Restore(level);

        if (snack > 0.84 && snack < 0.965)
        {
            float k = (float)((snack - 0.84) / 0.125), r = ringR * (1 - k) + core * 0.4f * k;
            double a = k * Math.PI * 3.2;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(230 * (1 - k * 0.6f)), Starlight)))
                g.FillEllipse(b, (float)Math.Cos(a) * r - 1.5f, (float)Math.Sin(a) * r * 0.45f - 1.5f, 3f, 3f);
        }

        if (burst) DrawStarlight(g, t, since, core);
    }

    // The accretion ring as short strokes round a tilted ellipse, brighter
    // on the side turning toward you. back: the far half, behind the void.
    static void DrawRing(Graphics g, float a0, float rx, float ry, Color c, float flicker, bool back)
    {
        const int n = 72;
        using (Pen soft = new Pen(Color.FromArgb((int)(60 * flicker), c), 5f))
        {
            soft.StartCap = soft.EndCap = LineCap.Round;
            for (int i = 0; i < n; i += 2)
            {
                double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 2) / n;
                if ((Math.Sin(a) < 0) != back) continue;
                g.DrawLine(soft, (float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry), (float)(Math.Cos(b) * rx), (float)(Math.Sin(b) * ry));
            }
        }
        using (Pen p = new Pen(c, 2f))
        {
            p.StartCap = p.EndCap = LineCap.Round;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n, b = 2 * Math.PI * (i + 1) / n;
                if ((Math.Sin(a) < 0) != back) continue;
                double lit = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(a + a0 * 0.15));
                // Light trails: a few brighter streaks that run round the ring.
                double streak = Math.Max(0, Math.Cos(3 * a - a0 * 2));
                // Far side dimmer, eased across the ends so the halves never show a seam.
                double near = 0.55 + 0.45 * Math.Max(0, Math.Min(1, (Math.Sin(a) + 0.3) / 0.6));
                int alpha = (int)(255 * flicker * Math.Min(1, near * (lit + 0.35 * streak * streak)));
                p.Color = Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c);
                g.DrawLine(p, (float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry), (float)(Math.Cos(b) * rx), (float)(Math.Sin(b) * ry));
            }
        }
    }

    // Motes in orbit, pulled round with the ring; livelier as energy rises.
    void DrawMotes(Graphics g, double t, float a0, float rx, float ry, Color c, float energy, float flicker, bool back)
    {
        int n = battery ? Motes.Length / 2 : Motes.Length;
        for (int i = 0; i < n; i++)
        {
            float[] m = Motes[i];
            double a = m[1] + a0 * m[2] * (0.6 + energy);
            if ((Math.Sin(a) < 0) != back) continue;
            float x = (float)(Math.Cos(a) * rx * m[0]), y = (float)(Math.Sin(a) * ry * m[0]);
            float s = m[3] * (0.8f + 0.4f * energy);
            double near = 0.5 + 0.5 * Math.Max(0, Math.Min(1, (Math.Sin(a) + 0.3) / 0.6));
            int alpha = (int)(255 * flicker * m[4] * (0.25 + 0.75 * energy) * near);
            Color tint = i % 4 == 0 ? Starlight : c;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), tint)))
                g.FillEllipse(b, x - s / 2, y - s / 2, s, s);
        }
    }

    // Two small energy arcs from the ring toward the void while it works.
    // They ride round with the ring and ripple smoothly, so they read as a
    // current, not as sparks jumping from place to place.
    static void DrawArcs(Graphics g, double t, float rx, float ry, float core, Color c)
    {
        for (int k = 0; k < 2; k++)
        {
            double a = t * 0.9 + k * Math.PI;
            float life = (float)(0.5 + 0.5 * Math.Sin(t * 2.4 + k * 1.7));
            PointF from = new PointF((float)(Math.Cos(a) * rx), (float)(Math.Sin(a) * ry));
            PointF to = new PointF(from.X * (core + 3) / rx, from.Y * 0.4f);
            PointF[] pts = new PointF[5];
            for (int i = 0; i < 5; i++)
            {
                float u = i / 4f;
                float bend = (float)Math.Sin(u * Math.PI) * (float)Math.Sin(t * 5 + k * 2 + i) * 2.2f;
                pts[i] = new PointF(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u + bend);
            }
            using (Pen p = new Pen(Color.FromArgb((int)(60 + 150 * life), Blend(c, Starlight, 0.4f)), 1.1f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawCurve(p, pts, 0.5f);
            }
        }
    }

    // The finish: two rippling rings of starlight and a few four-point stars.
    static void DrawStarlight(Graphics g, double t, double since, float core)
    {
        for (int w = 0; w < 2; w++)
        {
            double ph = (since * 0.9 + w * 0.5) % 1;
            float r = core + 5 + (float)ph * 26;
            using (Pen p = new Pen(Color.FromArgb((int)(130 * (1 - ph) * (1 - ph)), Blend(Cyan, Starlight, 0.6f)), 1.2f))
                g.DrawEllipse(p, -r, -r, 2 * r, 2 * r);
        }
        Random rnd = new Random(11);
        for (int k = 0; k < 6; k++)
        {
            double a = rnd.NextDouble() * Math.PI * 2, d = 20 + rnd.NextDouble() * 18;
            float twinkle = (float)Math.Max(0, Math.Sin(t * 6 + k * 1.3));
            float x = (float)(Math.Cos(a) * d), y = (float)(Math.Sin(a) * d * 0.8), s = 2 + 3.5f * twinkle;
            using (Pen p = new Pen(Color.FromArgb((int)(230 * twinkle), Starlight), 1f))
            {
                g.DrawLine(p, x - s, y, x + s, y);
                g.DrawLine(p, x, y - s, x, y + s);
            }
        }
    }

    const float GlowR = 24 + 6 * 3.4f;
    readonly Dictionary<long, Bitmap> glows = new Dictionary<long, Bitmap>();

    // The glow for one colour at one of 64 strengths, at this screen's scale.
    Bitmap Glow(Color c, float glow)
    {
        int level = Math.Max(0, Math.Min(64, (int)Math.Round(glow * 64)));
        long key = (long)(c.ToArgb() & 0xFFFFFF) * 128 + level;
        Bitmap b;
        if (glows.TryGetValue(key, out b)) return b;
        if (glows.Count > 200) { foreach (Bitmap old in glows.Values) old.Dispose(); glows.Clear(); }

        int px = (int)Math.Ceiling(2 * GlowR * scale);
        b = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        b.SetResolution(96, 96);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(px / (2 * GlowR), px / (2 * GlowR));
            g.TranslateTransform(GlowR, GlowR);
            float strength = level / 64f;
            for (int i = 6; i >= 0; i--)
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(strength * (14 + (6 - i) * 6)), c)))
                {
                    float r = 24 + i * 3.4f;
                    g.FillEllipse(brush, -r, -r, 2 * r, 2 * r);
                }
        }
        glows[key] = b;
        return b;
    }

    static Color Blend(Color a, Color b, float k)
    {
        return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    void DrawCount(Graphics g, float cx, float top)
    {
        // More than one task running: say how many.
        using (SolidBrush b = new SolidBrush(Blue))
        using (Font f = new Font("Segoe UI", 8, FontStyle.Bold))
        {
            g.FillEllipse(b, cx + 22, top - 4, 17, 17);
            StringFormat mid = new StringFormat();
            mid.Alignment = mid.LineAlignment = StringAlignment.Center;
            g.DrawString(working.ToString(), f, Brushes.White, new RectangleF(cx + 22, top - 4, 17, 17), mid);
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
            float w = Math.Min(W - 16, Math.Max(150, tw + 40));
            float h = hint.Length > 0 ? 66 : 48;
            float right = W - 8, bottomY = Math.Max(4 + h, top - 8);
            RectangleF r = new RectangleF(right - w, bottomY - h, w, h);
            bubbleBox = r;

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

            // A cross in the corner: dismiss without opening anything.
            RectangleF x = new RectangleF(r.Right - 22, r.Y + 5, 16, 16);
            closeBox = RectangleF.Inflate(x, 3, 3);
            if (overClose)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, Paper)))
                    g.FillEllipse(b, x);
            using (Pen p = new Pen(overClose ? Paper : Dim, 1.4f))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                float c = x.X + x.Width / 2, m = x.Y + x.Height / 2;
                g.DrawLine(p, c - 3.5f, m - 3.5f, c + 3.5f, m + 3.5f);
                g.DrawLine(p, c + 3.5f, m - 3.5f, c - 3.5f, m + 3.5f);
            }

            StringFormat cut = new StringFormat(StringFormatFlags.NoWrap);
            cut.Trimming = StringTrimming.EllipsisCharacter;
            using (SolidBrush b = new SolidBrush(edge)) g.DrawString(head, fh, b, new RectangleF(r.X + 10, r.Y + 6, r.Width - 36, 19), cut);
            using (SolidBrush b = new SolidBrush(Paper)) g.DrawString(body, fb, b, new RectangleF(r.X + 10, r.Y + 25, r.Width - 20, 19), cut);
            if (hint.Length > 0)
                using (SolidBrush b = new SolidBrush(Dim)) g.DrawString(hint, fs, b, new RectangleF(r.X + 10, r.Y + 45, r.Width - 20, 17), cut);
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
                if (kv.ContainsKey("mode")) Mode = kv["mode"] == "always" || kv["mode"] == "off" ? kv["mode"] : "auto";
                // Before the modes, the pet was simply shown or hidden.
                else if (kv.ContainsKey("shown") && kv["shown"] == "0") Mode = "off";
            }
        }
        catch { }
    }

    void Save()
    {
        try
        {
            File.WriteAllText(settings, "x=" + Left + "\r\ny=" + Top + "\r\nsound=" + (sound ? 1 : 0) + "\r\nmode=" + Mode + "\r\n");
        }
        catch { }
    }

    // The window's pixels: a DIB section that GDI+ draws into directly and
    // UpdateLayeredWindow reads from, so a frame needs no new bitmap and no
    // conversion. Made again only when the window's size changes.
    sealed class Canvas : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        public readonly int Width, Height;
        public readonly Bitmap Image;
        readonly IntPtr dc, dib, old;

        public Canvas(int w, int h)
        {
            Width = w; Height = h;
            dc = CreateCompatibleDC(IntPtr.Zero);
            BITMAPINFOHEADER info = new BITMAPINFOHEADER();
            info.Size = 40; info.Width = w; info.Height = -h; // top-down, as GDI+ lays rows out
            info.Planes = 1; info.BitCount = 32;
            IntPtr bits;
            dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            old = SelectObject(dc, dib);
            // Premultiplied alpha is what UpdateLayeredWindow expects.
            Image = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            // Font sizes are read against the image's own resolution, which
            // follows the screen's scaling. Pinned at 96, the text is scaled
            // once, by the drawing transform, not twice.
            Image.SetResolution(96, 96);
        }

        public void Push(Form f, byte alpha)
        {
            GdiFlush();
            SIZE size = new SIZE(); size.cx = Width; size.cy = Height;
            POINT from = new POINT(), at = new POINT(); at.x = f.Left; at.y = f.Top;
            BLEND blend = new BLEND(); blend.Alpha = alpha; blend.Format = 1; // AC_SRC_ALPHA
            UpdateLayeredWindow(f.Handle, IntPtr.Zero, ref at, ref size, dc, ref from, 0, ref blend, 2); // ULW_ALPHA
        }

        public void Dispose()
        {
            Image.Dispose();
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }
}

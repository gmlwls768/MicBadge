// 헤드셋 자체 음소거(Windows 에 안 알려짐)를 마이크 신호로 판별해 화면에 항상 띄우는 배지.
// 효과(Blue VO!CE 등)를 거치지 않은 원본(raw) 입력은 음소거 중 완전한 0, 켜져 있으면 조용해도 잡음이 있다.
// 빌드: build.bat   실행: MicBadge.exe [--log 파일]   (끌어서 이동, 오른쪽·아래 가장자리를 끌어 크기 조절, 우클릭 → 설정/종료)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    int GetCount(out uint count);
    int Item(uint index, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IPropertyStore store);
}
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    int GetCount(out uint count);
    int GetAt(uint index, out PROPERTYKEY key);
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
}
[StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
[StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
[StructLayout(LayoutKind.Sequential)] struct AudioClientProperties { public uint cbSize; public int bIsOffload, eCategory, Options; }

[ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    int GetBufferSize(out uint frames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    int GetMixFormat(out IntPtr format);
    int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr handle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(ref AudioClientProperties props);
}
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    int ReleaseBuffer(uint frames);
    int GetNextPacketSize(out uint frames);
}

class MicBadge : Form
{
    static readonly bool korean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
    static string T(string ko, string en) { return korean ? ko : en; }

    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref Size size, IntPtr hdcSrc, ref Point pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    enum State { Unknown, On, Off }
    State state = State.Unknown;
    IAudioClient2 client;
    IAudioCaptureClient cap;
    int channels;
    bool isFloat;
    float[] fbuf = new float[0];
    short[] sbuf = new short[0];
    bool gotData;
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastSound = long.MinValue / 2;     // 마지막으로 소리가 잡힌 시각(ms)
    string device = "";                     // 녹음 장치 이름에 이 글자가 있으면 그 장치, 비었거나 없으면 기본 장치
    double delaySec = 0.3;                  // 완전한 0 이 이만큼 이어지면 꺼짐 (0.1초 ~ 10분)
    bool lockSize;
    bool transparent;                       // 배경 없이 글자만, 상태는 글자 색으로
    readonly float scale;
    readonly int grip;                      // 오른쪽·아래 가장자리에서 이 픽셀 안쪽을 끌면 크기 조절
    int dragMode;                           // 0 이동, 1 너비, 2 높이, 3 둘 다
    Point dragOffset, dragStart;
    Size dragSize;
    readonly string iniFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MicBadge.ini");
    readonly string logFile;
    float logMin = float.MaxValue, logMax; int logCount;

    MicBadge(string logFile)
    {
        this.logFile = logFile;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
        grip = (int)(6 * scale);
        MinimumSize = new Size((int)(30 * scale), (int)(14 * scale));
        Size = new Size((int)(78 * scale), (int)(24 * scale));
        LoadSettings();

        ContextMenuStrip = new ContextMenuStrip();
        ContextMenuStrip.Items.Add(T("설정...", "Settings..."), null, delegate { ShowSettings(); });
        ContextMenuStrip.Items.Add(T("종료", "Exit"), null, delegate { Close(); });

        // 장치가 없거나 데이터가 끊기면 다시 연다
        System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
        watchdog.Interval = 2000;
        watchdog.Tick += delegate
        {
            if (cap == null || !gotData) { CloseMic(); OpenMic(); }
            gotData = false;
        };
        watchdog.Start();

        System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        poll.Interval = 100;
        poll.Tick += delegate { Poll(); };
        poll.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80000 | 0x80;  // WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Render(); OpenMic(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Render(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { CloseMic(); base.OnFormClosed(e); }

    // 켜져 있는 녹음 장치의 이름과 장치
    static List<KeyValuePair<string, IMMDevice>> Devices(IMMDeviceEnumerator en)
    {
        List<KeyValuePair<string, IMMDevice>> list = new List<KeyValuePair<string, IMMDevice>>();
        IMMDeviceCollection col; uint count;
        en.EnumAudioEndpoints(1, 1, out col);   // eCapture, ACTIVE
        col.GetCount(out count);
        PROPERTYKEY nameKey = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        for (uint i = 0; i < count; i++)
        {
            IMMDevice d; IPropertyStore ps; PROPVARIANT v;
            col.Item(i, out d);
            d.OpenPropertyStore(0, out ps);
            ps.GetValue(ref nameKey, out v);
            list.Add(new KeyValuePair<string, IMMDevice>(Marshal.PtrToStringUni(v.p) ?? "", d));
        }
        return list;
    }

    void OpenMic()
    {
        try
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice dev = null;
            if (device != "")
                foreach (KeyValuePair<string, IMMDevice> kv in Devices(en))
                    if (dev == null && kv.Key.Contains(device)) dev = kv.Value;
            if (dev == null) en.GetDefaultAudioEndpoint(1, 0, out dev);

            Guid iidClient = typeof(IAudioClient2).GUID; object o;
            dev.Activate(ref iidClient, 23, IntPtr.Zero, out o);
            IAudioClient2 c = (IAudioClient2)o;
            AudioClientProperties props = new AudioClientProperties { cbSize = 16, Options = 1 };   // AUDCLNT_STREAMOPTIONS_RAW
            c.SetClientProperties(ref props);
            IntPtr fmt;
            c.GetMixFormat(out fmt);
            int tag = (ushort)Marshal.ReadInt16(fmt, 0), bits = Marshal.ReadInt16(fmt, 14);
            if (tag == 0xFFFE) tag = Marshal.ReadInt32(fmt, 24);    // SubFormat 의 첫 값: 1=PCM, 3=float
            channels = Marshal.ReadInt16(fmt, 2);
            isFloat = tag == 3 && bits == 32;
            if ((!isFloat && bits != 16) || c.Initialize(0, 0, 10000000, 0, fmt, IntPtr.Zero) != 0) { SetState(State.Unknown); return; }
            Guid iidCap = typeof(IAudioCaptureClient).GUID;
            c.GetService(ref iidCap, out o);
            c.Start();
            client = c; cap = (IAudioCaptureClient)o;
        }
        catch (Exception) { SetState(State.Unknown); }
    }

    void CloseMic()
    {
        if (cap == null) return;
        try { client.Stop(); } catch (Exception) { }
        Marshal.ReleaseComObject(cap); Marshal.ReleaseComObject(client);
        cap = null; client = null;
    }

    void Poll()
    {
        if (cap == null) return;
        try
        {
            bool got = false; float peak = 0; uint next;
            for (cap.GetNextPacketSize(out next); next > 0; cap.GetNextPacketSize(out next))
            {
                IntPtr data; uint frames, flags; ulong p1, p2;
                cap.GetBuffer(out data, out frames, out flags, out p1, out p2);
                if ((flags & 2) == 0) peak = Math.Max(peak, Peak(data, (int)frames * channels));   // 2 = SILENT
                cap.ReleaseBuffer(frames);
                got = true;
            }
            if (!got) return;
            gotData = true;
            if (peak > 0) { lastSound = clock.ElapsedMilliseconds; SetState(State.On); }
            else if (clock.ElapsedMilliseconds - lastSound >= delaySec * 1000) SetState(State.Off);
            if (logFile != null) Log(peak);
        }
        catch (COMException) { CloseMic(); SetState(State.Unknown); }   // 장치가 사라짐 → 워치독이 다시 연다
    }

    // 16비트 눈금으로 환산한 최대 절댓값
    float Peak(IntPtr data, int n)
    {
        float peak = 0;
        if (isFloat)
        {
            if (fbuf.Length < n) fbuf = new float[n];
            Marshal.Copy(data, fbuf, 0, n);
            for (int i = 0; i < n; i++) { float v = Math.Abs(fbuf[i]) * 32768; if (v > peak) peak = v; }
        }
        else
        {
            if (sbuf.Length < n) sbuf = new short[n];
            Marshal.Copy(data, sbuf, 0, n);
            for (int i = 0; i < n; i++) { float v = Math.Abs((int)sbuf[i]); if (v > peak) peak = v; }
        }
        return peak;
    }

    // 1초마다 그 사이 피크의 최소·최대를 남긴다 (판별 기준 확인용)
    void Log(float peak)
    {
        if (peak < logMin) logMin = peak;
        if (peak > logMax) logMax = peak;
        if (++logCount < 10) return;
        File.AppendAllText(logFile, string.Format("{0:HH:mm:ss} min={1:G5} max={2:G5} {3}\r\n", DateTime.Now, logMin, logMax, state));
        logMin = float.MaxValue; logMax = 0; logCount = 0;
    }

    void SetState(State s)
    {
        if (s == state) return;
        state = s;
        Render();
    }

    Bitmap Draw()
    {
        Color c = transparent
            ? (state == State.Off ? Color.FromArgb(240, 50, 50) : state == State.On ? Color.FromArgb(40, 205, 90) : Color.FromArgb(160, 160, 160))
            : (state == State.Off ? Color.FromArgb(200, 30, 30) : state == State.On ? Color.FromArgb(30, 130, 60) : Color.Gray);
        string text = state == State.Off ? "MIC OFF" : state == State.On ? "MIC ON" : "MIC ?";
        Bitmap b = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(transparent ? Color.FromArgb(1, 0, 0, 0) : Color.FromArgb(230, c));   // 완전 투명(0)이면 클릭이 통과해 끌 수 없다
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            float px = Math.Max(6f, Math.Min(Height * 0.6f, Width / 4.8f));     // 배지 크기에 맞춘 글자 크기
            using (Font f = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Brush br = new SolidBrush(transparent ? c : Color.White))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip))
            {
                sf.Alignment = sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, f, br, new RectangleF(0, 0, Width, Height), sf);
            }
        }
        return b;
    }

    // 픽셀마다 투명도를 갖는 창이라 WM_PAINT 대신 그린 그림을 통째로 넘긴다
    void Render()
    {
        if (!IsHandleCreated) return;
        using (Bitmap b = Draw())
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = b.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
            Size size = b.Size; Point src = Point.Empty;
            BLENDFUNCTION bf = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };    // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref bf, 2);  // ULW_ALPHA
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    int Zone(Point p)
    {
        if (lockSize) return 0;
        return (p.X >= Width - grip ? 1 : 0) | (p.Y >= Height - grip ? 2 : 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { dragOffset = e.Location; dragStart = Cursor.Position; dragSize = Size; dragMode = Zone(e.Location); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Point c = Cursor.Position;
            if (dragMode == 0) Location = new Point(c.X - dragOffset.X, c.Y - dragOffset.Y);
            else Size = new Size(dragSize.Width + ((dragMode & 1) != 0 ? c.X - dragStart.X : 0), dragSize.Height + ((dragMode & 2) != 0 ? c.Y - dragStart.Y : 0));
        }
        else
        {
            int z = Zone(e.Location);
            Cursor = z == 3 ? Cursors.SizeNWSE : z == 1 ? Cursors.SizeWE : z == 2 ? Cursors.SizeNS : Cursors.Default;
        }
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) SaveSettings();
        base.OnMouseUp(e);
    }

    void LoadSettings()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Top + 16);
        try
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(iniFile))
            {
                string[] kv = line.Split(new char[] { '=' }, 2);
                if (kv.Length == 2) d[kv[0]] = kv[1];
            }
            if (d.ContainsKey("device")) device = d["device"];
            transparent = d.ContainsKey("transparent") && d["transparent"] == "1";
            Point pt = new Point(int.Parse(d["x"]), int.Parse(d["y"]));
            foreach (Screen sc in Screen.AllScreens) if (sc.Bounds.Contains(pt)) Location = pt;
            Size = new Size(int.Parse(d["w"]), int.Parse(d["h"]));
            delaySec = Math.Min(600, Math.Max(0.1, double.Parse(d["delay"], CultureInfo.InvariantCulture)));
            lockSize = d["lock"] == "1";
        }
        catch { }   // 파일이 없거나 깨졌으면 기본값
    }

    void SaveSettings()
    {
        File.WriteAllText(iniFile, string.Format(CultureInfo.InvariantCulture, "x={0}\r\ny={1}\r\nw={2}\r\nh={3}\r\ndelay={4}\r\nlock={5}\r\ndevice={6}\r\ntransparent={7}\r\n",
            Left, Top, Width, Height, delaySec, lockSize ? 1 : 0, device, transparent ? 1 : 0));
    }

    void ShowSettings()
    {
        using (Form f = new Form())
        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RUN_KEY))
        {
            f.Text = T("MicBadge 설정", "MicBadge Settings");
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding((int)(10 * scale));

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true;
            t.ColumnCount = 2;
            f.Controls.Add(t);

            ComboBox mic = new ComboBox();
            mic.DropDownStyle = ComboBoxStyle.DropDownList;
            mic.Width = (int)(260 * scale);
            mic.Items.Add(T("기본 녹음 장치", "Default recording device"));
            mic.SelectedIndex = 0;
            try
            {
                foreach (KeyValuePair<string, IMMDevice> kv in Devices((IMMDeviceEnumerator)new MMDeviceEnumerator()))
                {
                    mic.Items.Add(kv.Key);
                    if (device != "" && mic.SelectedIndex == 0 && kv.Key.Contains(device)) mic.SelectedIndex = mic.Items.Count - 1;
                }
            }
            catch (Exception) { }   // 장치 목록을 못 읽으면 기본 장치만
            if (device != "" && mic.SelectedIndex == 0) { mic.Items.Add(device); mic.SelectedIndex = mic.Items.Count - 1; }   // 지금은 꺼져 있는 장치

            NumericUpDown delay = Number(0.1m, 600m, (decimal)delaySec, 1);
            NumericUpDown w = Number(MinimumSize.Width, 4000, Width, 0), h = Number(MinimumSize.Height, 4000, Height, 0);
            CheckBox locked = Check(T("크기 잠금 (끌어서 크기 조절 안 함)", "Lock size (no resizing by dragging)"), lockSize);
            CheckBox clear = Check(T("배경 투명 (상태는 글자 색으로 표시)", "Transparent background (state shown by text color)"), transparent);
            CheckBox autostart = Check(T("Windows 시작 시 자동 실행", "Start with Windows"), run.GetValue("MicBadge") != null);

            t.Controls.Add(Caption(T("마이크", "Microphone")), 0, 0); t.Controls.Add(mic, 1, 0);
            t.Controls.Add(Caption(T("꺼짐 판정 시간(초)", "Mute delay (seconds)")), 0, 1); t.Controls.Add(delay, 1, 1);
            Label hint = Caption(T("이 시간 동안 소리가 전혀 없으면 MIC OFF (0.1초 ~ 600초)", "MIC OFF after this long with no sound at all (0.1 to 600 s)"));
            hint.ForeColor = SystemColors.GrayText;
            t.Controls.Add(hint, 0, 2); t.SetColumnSpan(hint, 2);
            t.Controls.Add(Caption(T("너비", "Width")), 0, 3); t.Controls.Add(w, 1, 3);
            t.Controls.Add(Caption(T("높이", "Height")), 0, 4); t.Controls.Add(h, 1, 4);
            t.Controls.Add(locked, 0, 5); t.SetColumnSpan(locked, 2);
            t.Controls.Add(clear, 0, 6); t.SetColumnSpan(clear, 2);
            t.Controls.Add(autostart, 0, 7); t.SetColumnSpan(autostart, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Anchor = AnchorStyles.Right;
            Button ok = new Button(), cancel = new Button();
            ok.Text = T("확인", "OK"); ok.DialogResult = DialogResult.OK; ok.AutoSize = true;
            cancel.Text = T("취소", "Cancel"); cancel.DialogResult = DialogResult.Cancel; cancel.AutoSize = true;
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            t.Controls.Add(buttons, 0, 8); t.SetColumnSpan(buttons, 2);
            f.AcceptButton = ok; f.CancelButton = cancel;

            if (f.ShowDialog() != DialogResult.OK) return;
            string picked = mic.SelectedIndex > 0 ? (string)mic.SelectedItem : "";
            if (picked != device) { device = picked; CloseMic(); OpenMic(); }
            delaySec = (double)delay.Value;
            lockSize = locked.Checked;
            transparent = clear.Checked;
            Size = new Size((int)w.Value, (int)h.Value);
            Render();
            if (autostart.Checked) run.SetValue("MicBadge", "\"" + Application.ExecutablePath + "\"");
            else run.DeleteValue("MicBadge", false);
            SaveSettings();
        }
    }

    static Label Caption(string text)
    {
        Label l = new Label();
        l.Text = text; l.AutoSize = true; l.Anchor = AnchorStyles.Left;
        return l;
    }
    static CheckBox Check(string text, bool value)
    {
        CheckBox c = new CheckBox();
        c.Text = text; c.Checked = value; c.AutoSize = true;
        return c;
    }
    NumericUpDown Number(decimal min, decimal max, decimal value, int decimals)
    {
        NumericUpDown n = new NumericUpDown();
        n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals;
        n.Increment = decimals > 0 ? 0.1m : 1m;
        n.Value = Math.Min(max, Math.Max(min, value));
        n.Width = (int)(80 * scale);
        return n;
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool first;
        using (Mutex m = new Mutex(true, "MicBadge_single", out first))
        {
            if (!first) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new MicBadge(args.Length == 2 && args[0] == "--log" ? Path.GetFullPath(args[1]) : null));
        }
    }
}

// ClipVault — менеджер буфера обмена. Ctrl+Shift+V — открыть историю.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

// Метаданные файла: имя, описание, версия, автор — чтобы это выглядело как обычное приложение.
[assembly: AssemblyTitle("ClipVault")]
[assembly: AssemblyDescription("Менеджер буфера обмена: история, закрепление и быстрые преобразования")]
[assembly: AssemblyCompany("ClipVault")]
[assembly: AssemblyProduct("ClipVault")]
[assembly: AssemblyCopyright("© 2026 ClipVault")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: ComVisible(false)]

class Entry
{
    public long Id;
    public bool IsImage;
    public bool IsFile;
    public bool Pinned;
    public DateTime Time;
    public string Text = "";        // для текста и файлов (пути через перенос строки)
    public string ImageFile = "";
    public Image Thumb;

    string kind; bool kindDone;
    // Тип содержимого текста: ССЫЛКА / ПОЧТА / ЦВЕТ / ТЕЛЕФОН / JSON или null
    public string Kind
    {
        get
        {
            if (!kindDone) { kind = (IsImage || IsFile) ? null : Detect.Of(Text); kindDone = true; }
            return kind;
        }
    }
}

static class Detect
{
    static readonly Regex Url = new Regex(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Mail = new Regex(@"^[^\s@]+@[^\s@]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);
    static readonly Regex Hex = new Regex(@"^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Rgb = new Regex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Phone = new Regex(@"^\+?[\d\s\-()]{7,20}$", RegexOptions.Compiled);

    public static string Of(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        if ((t[0] == '{' && t[t.Length - 1] == '}' && t.Contains(":")) || (t[0] == '[' && t[t.Length - 1] == ']' && t.Length > 2)) return "JSON";
        if (t.Length > 2000) return null;
        if (Url.IsMatch(t)) return "ССЫЛКА";
        if (Mail.IsMatch(t)) return "ПОЧТА";
        Color c; if (TryColor(t, out c)) return "ЦВЕТ";
        if (Phone.IsMatch(t)) { int d = t.Count(char.IsDigit); if (d >= 7 && d <= 15) return "ТЕЛЕФОН"; }
        return null;
    }

    public static bool TryColor(string t, out Color c)
    {
        c = Color.Empty;
        var m = Hex.Match(t);
        if (m.Success)
        {
            string h = m.Groups[1].Value;
            if (h.Length == 3) h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
            int r = Convert.ToInt32(h.Substring(0, 2), 16), g = Convert.ToInt32(h.Substring(2, 2), 16), b = Convert.ToInt32(h.Substring(4, 2), 16);
            int a = h.Length == 8 ? Convert.ToInt32(h.Substring(6, 2), 16) : 255;
            c = Color.FromArgb(a, r, g, b);
            return true;
        }
        m = Rgb.Match(t);
        if (m.Success)
        {
            int r = Math.Min(255, int.Parse(m.Groups[1].Value)), g = Math.Min(255, int.Parse(m.Groups[2].Value)), b = Math.Min(255, int.Parse(m.Groups[3].Value));
            c = Color.FromArgb(r, g, b);
            return true;
        }
        return false;
    }

    public static Color BadgeFor(string kind)
    {
        switch (kind)
        {
            case "ССЫЛКА": return Color.FromArgb(38, 190, 170);
            case "ПОЧТА": return Color.FromArgb(235, 95, 140);
            case "ЦВЕТ": return Color.FromArgb(150, 195, 60);
            case "ТЕЛЕФОН": return Color.FromArgb(70, 195, 115);
            case "JSON": return Color.FromArgb(120, 130, 250);
            default: return Color.Gray;
        }
    }
}

// Лог ошибок: data\log.txt (ротация при 256 КБ). Пишем тихо — логгер сам никогда не падает.
static class Logger
{
    static readonly object gate = new object();

    public static void Write(string msg)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Store.Dir);
                string f = Path.Combine(Store.Dir, "log.txt");
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 256 * 1024) { try { File.Copy(f, f + ".old", true); File.Delete(f); } catch { } }
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void Error(string where, Exception ex) { Write(where + ": " + ex); }
}

// Настройки: data\settings.txt, формат «ключ=значение»
static class Settings
{
    public static bool AutoCopy = true, Sound = true, Animations = true, PlainHotkeyOn = true;
    public static int Volume = 60, MaxHistory = 500;
    public static uint HotMods = Native.MOD_CONTROL | Native.MOD_SHIFT, HotKey = (uint)Keys.V;
    public static uint PlainMods = Native.MOD_CONTROL | Native.MOD_ALT, PlainKey = (uint)Keys.V;
    public static string Exclude = "explorer,windowsterminal,wt,cmd,powershell,pwsh,conhost,openconsole";

    static string FileName { get { return Path.Combine(Store.Dir, "settings.txt"); } }

    public static void Load()
    {
        try
        {
            string f = FileName;
            if (!File.Exists(f))
            {
                if (File.Exists(Path.Combine(Store.Dir, "autocopy_off"))) AutoCopy = false; // перенос старого флага
                return;
            }
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                try
                {
                    switch (k)
                    {
                        case "AutoCopy": AutoCopy = v == "1"; break;
                        case "Sound": Sound = v == "1"; break;
                        case "Animations": Animations = v == "1"; break;
                        case "PlainHotkeyOn": PlainHotkeyOn = v == "1"; break;
                        case "Volume": Volume = Math.Max(0, Math.Min(100, int.Parse(v))); break;
                        case "MaxHistory": MaxHistory = Math.Max(20, Math.Min(5000, int.Parse(v))); break;
                        case "HotMods": HotMods = uint.Parse(v); break;
                        case "HotKey": HotKey = uint.Parse(v); break;
                        case "PlainMods": PlainMods = uint.Parse(v); break;
                        case "PlainKey": PlainKey = uint.Parse(v); break;
                        case "Exclude": Exclude = v; break;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Logger.Error("Settings.Load", ex); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var l = new List<string>();
            l.Add("AutoCopy=" + (AutoCopy ? 1 : 0));
            l.Add("Sound=" + (Sound ? 1 : 0));
            l.Add("Animations=" + (Animations ? 1 : 0));
            l.Add("PlainHotkeyOn=" + (PlainHotkeyOn ? 1 : 0));
            l.Add("Volume=" + Volume);
            l.Add("MaxHistory=" + MaxHistory);
            l.Add("HotMods=" + HotMods);
            l.Add("HotKey=" + HotKey);
            l.Add("PlainMods=" + PlainMods);
            l.Add("PlainKey=" + PlainKey);
            l.Add("Exclude=" + string.Join(",", ExcludeList()));
            File.WriteAllLines(FileName, l.ToArray(), Encoding.UTF8);
        }
        catch (Exception ex) { Logger.Error("Settings.Save", ex); }
    }

    public static string[] ExcludeList()
    {
        return Exclude.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => { x = x.Trim().ToLowerInvariant(); return x.EndsWith(".exe") ? x.Substring(0, x.Length - 4) : x; })
            .Where(x => x.Length > 0).ToArray();
    }

    public static bool IsExcluded(string proc)
    {
        proc = proc.ToLowerInvariant();
        if (proc.EndsWith(".exe")) proc = proc.Substring(0, proc.Length - 4);
        return ExcludeList().Contains(proc);
    }

    public static string HotkeyText(uint mods, uint key)
    {
        if (key == 0) return "не задано";
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        Keys k = (Keys)key;
        parts.Add(k >= Keys.D0 && k <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString());
        return string.Join("+", parts.ToArray());
    }
}

static class Store
{
    // Данные лежат рядом с exe (папка data): так их одинаково видят и программа, и проводник,
    // даже если ClipVault запущен из «упакованного» приложения, которое подменяет AppData.
    public static string Dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data");
    public static string ImgDir = Path.Combine(Dir, "images");
    static string DbFile = Path.Combine(Dir, "history.txt");
    public static List<Entry> Items = new List<Entry>();
    public static int MaxUnpinned { get { return Settings.MaxHistory; } }

    public static void Load()
    {
        Directory.CreateDirectory(ImgDir);
        string[] lines = ReadLines(DbFile);
        if (lines == null) lines = ReadLines(DbFile + ".bak");   // основной файл повреждён — берём резервную копию
        if (lines == null) return;
        foreach (var line in lines)
        {
            var p = line.Split('|');
            if (p.Length < 5) continue;
            try
            {
                var e = new Entry();
                e.Id = long.Parse(p[0]);
                e.IsImage = p[1] == "I";
                e.IsFile = p[1] == "F";
                e.Pinned = p[2] == "1";
                e.Time = new DateTime(long.Parse(p[3]));
                if (e.IsImage)
                {
                    e.ImageFile = p[4];
                    if (!File.Exists(Path.Combine(ImgDir, e.ImageFile))) continue;
                    e.Thumb = MakeThumb(Path.Combine(ImgDir, e.ImageFile));
                }
                else e.Text = Encoding.UTF8.GetString(Convert.FromBase64String(p[4]));
                Items.Add(e);
            }
            catch { }
        }
    }

    public static void Save()
    {
        var sb = new StringBuilder();
        foreach (var e in Items)
        {
            string type = e.IsImage ? "I" : (e.IsFile ? "F" : "T");
            sb.Append(e.Id).Append('|').Append(type).Append('|')
              .Append(e.Pinned ? "1" : "0").Append('|').Append(e.Time.Ticks).Append('|')
              .Append(e.IsImage ? e.ImageFile : Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Text)))
              .Append('\n');
        }
        // Безопасная запись: сначала во временный файл, потом атомарная замена с резервной копией,
        // чтобы сбой посреди записи не стёр историю.
        try
        {
            string tmp = DbFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(DbFile)) File.Replace(tmp, DbFile, DbFile + ".bak");
            else File.Move(tmp, DbFile);
        }
        catch (Exception ex) { Logger.Error("Store.Save", ex); }
    }

    static string[] ReadLines(string f)
    {
        try { return File.Exists(f) ? File.ReadAllLines(f, Encoding.UTF8) : null; }
        catch (Exception ex) { Logger.Error("Store.Load " + f, ex); return null; }
    }

    public static void ApplyLimit() { Trim(); Save(); }

    public static Image MakeThumb(string path)
    {
        using (var src = Image.FromFile(path))
        {
            int h = 40, w = Math.Max(1, src.Width * h / Math.Max(1, src.Height));
            if (w > 120) { w = 120; h = Math.Max(1, src.Height * w / Math.Max(1, src.Width)); }
            return new Bitmap(src, w, h);
        }
    }

    static void Promote(Entry e)
    {
        Items.Remove(e); e.Time = DateTime.Now; Items.Insert(0, e);
    }

    // Приводим текст к сравнимому виду: одинаковые переносы строк, без хвостовых пробелов.
    // Чтобы «то же самое» не добавлялось повторно, даже если переносы строк отличались.
    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
    }

    static bool AlreadyTop(Entry e)
    {
        return Items.Count > 0 && ReferenceEquals(Items[0], e);
    }

    public static void AddText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;   // пустое не копируем
        string norm = Norm(text);
        var old = Items.FirstOrDefault(x => !x.IsImage && !x.IsFile && Norm(x.Text) == norm);
        if (old != null)
        {
            if (!AlreadyTop(old)) { Promote(old); Trim(); Save(); }   // то же самое — не дублируем
        }
        else { Items.Insert(0, new Entry { Id = DateTime.Now.Ticks, Time = DateTime.Now, Text = text }); Trim(); Save(); }
    }

    public static void AddFiles(string[] files)
    {
        if (files == null) return;
        files = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        if (files.Length == 0) return;   // пустой список файлов не добавляем
        string text = string.Join("\n", files);
        var old = Items.FirstOrDefault(x => x.IsFile && Norm(x.Text) == Norm(text));
        if (old != null)
        {
            if (!AlreadyTop(old)) { Promote(old); Trim(); Save(); }
        }
        else { Items.Insert(0, new Entry { Id = DateTime.Now.Ticks, Time = DateTime.Now, IsFile = true, Text = text }); Trim(); Save(); }
    }

    public static void AddImage(Image img)
    {
        if (img == null) return;
        var e = new Entry { Id = DateTime.Now.Ticks, Time = DateTime.Now, IsImage = true };
        e.ImageFile = e.Id + ".png";
        string path = Path.Combine(ImgDir, e.ImageFile);
        img.Save(path, ImageFormat.Png);

        // не добавляем дважды одинаковую картинку: быстрый отсев по размеру, затем по хешу
        long size = new FileInfo(path).Length;
        string newHash = null;
        foreach (var ex in Items.Where(x => x.IsImage).ToList())
        {
            string xp = Path.Combine(ImgDir, ex.ImageFile);
            try
            {
                if (!File.Exists(xp) || new FileInfo(xp).Length != size) continue;
                if (newHash == null) newHash = FileHash(path);
                if (FileHash(xp) == newHash)
                {
                    try { File.Delete(path); } catch { }
                    if (!AlreadyTop(ex)) { Promote(ex); Trim(); Save(); }   // такая картинка уже есть
                    return;
                }
            }
            catch { }
        }

        e.Thumb = MakeThumb(path);
        Items.Insert(0, e);
        Trim(); Save();
    }

    static string FileHash(string p)
    {
        using (var sha = System.Security.Cryptography.SHA1.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(sha.ComputeHash(fs));
    }

    public static void Remove(Entry e)
    {
        Items.Remove(e);
        if (e.IsImage) { try { File.Delete(Path.Combine(ImgDir, e.ImageFile)); } catch { } }
    }

    static void Trim()
    {
        var extra = Items.Where(x => !x.Pinned).Skip(MaxUnpinned).ToList();
        foreach (var e in extra) Remove(e);
    }

    public static void ClearUnpinned()
    {
        foreach (var e in Items.Where(x => !x.Pinned).ToList()) Remove(e);
        Save();
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    public const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    // --- глобальный хук мыши для авто-копирования ---
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageStr(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
}

class PickerForm : Form
{
    static readonly Color BgTop = Color.FromArgb(32, 32, 35), BgBottom = Color.FromArgb(12, 12, 13),
                          SearchBg = Color.FromArgb(40, 40, 43),
                          Fg = Color.FromArgb(240, 240, 242), Dim = Color.FromArgb(140, 140, 146),
                          Gold = Color.FromArgb(236, 236, 240), Accent = Color.FromArgb(200, 200, 206);
    static readonly Font UiFont = new Font("Segoe UI Semibold", 10.5f), MetaFont = new Font("Segoe UI", 8.25f),
                         TabFont = new Font("Segoe UI Semibold", 9f), BadgeFont = new Font("Segoe UI", 7.25f, FontStyle.Bold),
                         SearchFont = new Font("Segoe UI", 10.5f), HintFont = new Font("Segoe UI", 8f),
                         NumFont = new Font("Segoe UI Semibold", 8.5f), BigFont = new Font("Segoe UI Semibold", 12f);

    static readonly string[] TabNames = { "Все", "Текст", "Картинки", "Файлы" };
    static readonly string[] BadgeText = { "", "ТЕКСТ", "КАРТИНКА", "ФАЙЛЫ" };
    static readonly Color[] BadgeColor = { Color.Gray, Color.FromArgb(70, 160, 255), Color.FromArgb(168, 105, 250), Color.FromArgb(240, 150, 60) };
    const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    const int Pad = 14, TabsY = 14, TabH = 36, SearchY = 62, SearchH = 34, ListY = 106, HintH = 30, CardH = 58, Pitch = 66;

    TextBox search = new TextBox();
    List<Entry> view = new List<Entry>();
    int category = 0, sel = -1, scrollPx = 0, hoverTab = -1, hoverItem = -1;
    int[] counts = new int[4];

    // ---------- контекстное меню карточки (правая кнопка) ----------
    const int MenuW = 196, MenuRowH = 32, MenuPad = 6;
    static readonly Font MenuFont = new Font("Segoe UI", 9.5f);
    bool menuOn;            // меню открыто (целевое состояние)
    float menuT;            // 0..1 — прогресс анимации появления
    int menuHover = -1;     // пункт под курсором
    Point menuPos;          // левый верхний угол меню
    string[] menuLabels;    // подписи пунктов
    Action[] menuActions;   // действия пунктов
    int MenuH { get { return menuLabels == null ? 0 : menuLabels.Length * MenuRowH + MenuPad * 2; } }

    // короткое подтверждение «Скопировано»
    long copiedId = -1;
    float copiedT;
    IntPtr prevWindow;
    public Action<Entry> OnPick;

    // ---------- анимации ----------
    // Кадры тикают синхронно с обновлением монитора (DwmFlush), а не по грубому таймеру WinForms
    volatile bool animActive;
    int animGen;
    volatile bool tickPending;
    const double OpMax = 0.995;               // < 1, чтобы окно оставалось «слойным» и не мигало при смене режима
    System.Diagnostics.Stopwatch frameWatch = System.Diagnostics.Stopwatch.StartNew();
    double lastMs;
    Bitmap bgCache;                           // готовый фон (градиент + свечение), чтобы не рисовать его каждый кадр
    float scrollCur = 0, selY = 0, tabPos = 0, openT = 0;
    bool opening, closing;
    int baseTop;
    DateTime listShown = DateTime.MinValue;   // момент появления списка — для каскада карточек

    static float Ease(float t) { t = Math.Max(0f, Math.Min(1f, t)); float u = 1 - t; return 1 - u * u * u; }
    void StartAnim()
    {
        if (animActive || !IsHandleCreated) return;   // до создания окна кадры слать некуда
        animActive = true;
        lastMs = frameWatch.Elapsed.TotalMilliseconds;
        int myGen = ++animGen;
        var t = new Thread(() =>
        {
            while (animActive && animGen == myGen)
            {
                if (Native.DwmFlush() != 0) Thread.Sleep(8);   // ждём следующего кадра композитора (vsync)
                if (!animActive || animGen != myGen) break;
                if (tickPending) continue;
                tickPending = true;
                try { BeginInvoke((Action)(() => { tickPending = false; if (animActive) AnimTick(); })); }
                catch { tickPending = false; if (animGen == myGen) animActive = false; return; }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    void AnimateHide()
    {
        if (!Visible || closing) return;
        opening = false; closing = true; StartAnim();
    }

    // Анимация привязана к реальному времени (dt), а не к числу кадров — на любой частоте выглядит одинаково
    void AnimTick()
    {
        double now = frameWatch.Elapsed.TotalMilliseconds;
        float dt = (float)Math.Min(50.0, Math.Max(1.0, now - lastMs));
        lastMs = now;
        bool busy = false;

        float kScroll = 1f - (float)Math.Exp(-dt / 60f);
        float kSel = 1f - (float)Math.Exp(-dt / 45f);
        float kTab = 1f - (float)Math.Exp(-dt / 70f);

        if (Math.Abs(scrollCur - scrollPx) > 0.3f) { scrollCur += (scrollPx - scrollCur) * kScroll; busy = true; } else scrollCur = scrollPx;
        float ty = sel < 0 ? 0 : sel * Pitch;
        if (Math.Abs(selY - ty) > 0.3f) { selY += (ty - selY) * kSel; busy = true; } else selY = ty;
        if (Math.Abs(tabPos - category) > 0.003f) { tabPos += (category - tabPos) * kTab; busy = true; } else tabPos = category;
        if ((DateTime.UtcNow - listShown).TotalMilliseconds < 220 + 30 * 8) busy = true;

        if (opening)
        {
            openT = Math.Min(1f, openT + dt / 170f);
            float k = Ease(openT);
            Opacity = k * OpMax; Top = baseTop + (int)Math.Round((1 - k) * 14);
            if (openT >= 1f) opening = false;
            busy = true;
        }
        else if (closing)
        {
            openT = Math.Max(0f, openT - dt / 120f);
            Opacity = openT * OpMax; Top = baseTop + (int)Math.Round((1 - openT) * 8);
            if (openT <= 0f) { closing = false; Hide(); }
            busy = true;
        }

        // анимация контекстного меню
        if (menuOn) { if (menuT < 1f) { menuT = Math.Min(1f, menuT + dt / 150f); busy = true; } }
        else if (menuT > 0f) { menuT = Math.Max(0f, menuT - dt / 110f); busy = true; }

        // затухание подсказки «Скопировано»
        if (copiedT > 0f) { copiedT = Math.Max(0f, copiedT - dt / 900f); busy = true; }

        if (Visible) { Invalidate(); Update(); }   // Update — рисуем кадр сразу, а не «когда дойдёт очередь»
        if (!busy) animActive = false;
    }

    void EnsureBg()
    {
        if (bgCache != null && bgCache.Width == Width && bgCache.Height == Height) return;
        if (bgCache != null) bgCache.Dispose();
        bgCache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bgCache))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var full = new Rectangle(0, 0, Width, Height);
            using (var br = new LinearGradientBrush(full, BgTop, BgBottom, 90f)) g.FillRectangle(br, full);
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(-Width * 0.3f, -Height * 0.35f, Width * 1.6f, Height * 0.8f);
                using (var pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Color.FromArgb(20, 255, 255, 255);
                    pg.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(pg, gp);
                }
            }
        }
    }

    // Каскад: карточки по очереди «выезжают» снизу при открытии и смене вкладки
    int CascadeOffset(int i, int sc)
    {
        int vi = i - sc / Pitch;
        if (vi < 0 || vi > 8) return 0;
        double age = (DateTime.UtcNow - listShown).TotalMilliseconds - vi * 30;
        return (int)Math.Round((1 - Ease((float)(age / 220.0))) * 16);
    }

    public PickerForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = "ClipVault";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgBottom; Size = new Size(520, 500);

        search.BorderStyle = BorderStyle.None; search.Font = SearchFont;
        search.BackColor = SearchBg; search.ForeColor = Fg;
        search.TextChanged += (s, e) => Refill();
        search.GotFocus += (s, e) => Invalidate();
        search.LostFocus += (s, e) => Invalidate();
        search.HandleCreated += (s, e) => Native.SendMessageStr(search.Handle, 0x1501, (IntPtr)1, "Поиск…");
        Controls.Add(search);
        LayoutSearch();

        Deactivate += (s, e) => AnimateHide();
        KeyDown += OnKey;
        Refill();
    }

    void LayoutSearch()
    {
        search.Left = Pad + 38;
        search.Width = Width - 2 * Pad - 38 - 14;
        search.Top = SearchY + (SearchH - search.Height) / 2;
    }

    int VisibleH { get { return Height - ListY - HintH; } }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutSearch();
        using (var p = RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 14))
            Region = new Region(p);
    }

    public void ShowPicker()
    {
        prevWindow = Native.GetForegroundWindow();
        var scr = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Math.Min(Math.Max(scr.Left, Cursor.Position.X - Width / 2), scr.Right - Width);
        int y = Math.Min(Math.Max(scr.Top, Cursor.Position.Y - 40), scr.Bottom - Height);
        baseTop = y;
        Location = new Point(x, y + 14);
        search.Text = "";
        category = 0; tabPos = 0;
        Refill(true);
        Opacity = 0; openT = 0; opening = true; closing = false;
        Show(); Activate(); search.Focus();
        StartAnim();
    }

    void SetCategory(int c)
    {
        category = c;
        Refill(true);
        search.Focus();
    }

    bool InCategory(Entry e)
    {
        switch (category)
        {
            case 1: return !e.IsImage && !e.IsFile;
            case 2: return e.IsImage;
            case 3: return e.IsFile;
            default: return true;
        }
    }

    void Refill(bool cascade = false)
    {
        string q = search.Text.Trim().ToLowerInvariant();
        view = Store.Items
            .Where(InCategory)
            .Where(e => q.Length == 0 || (!e.IsImage && e.Text.ToLowerInvariant().Contains(q)))
            .OrderByDescending(e => e.Pinned).ToList();

        counts[0] = Store.Items.Count;
        counts[1] = Store.Items.Count(e => !e.IsImage && !e.IsFile);
        counts[2] = Store.Items.Count(e => e.IsImage);
        counts[3] = Store.Items.Count(e => e.IsFile);

        sel = view.Count > 0 ? 0 : -1;
        scrollPx = 0;
        if (cascade) { scrollCur = 0; selY = 0; listShown = DateTime.UtcNow; }
        StartAnim();
        Invalidate();
    }

    // ---------- геометрия ----------
    Rectangle TabRect(int i)
    {
        int gap = 8, w = (Width - 2 * Pad - 3 * gap) / 4;
        return new Rectangle(Pad + i * (w + gap), TabsY, w, TabH);
    }

    int HitTab(Point p)
    {
        for (int i = 0; i < 4; i++) if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    int HitItem(Point p)
    {
        if (p.Y < ListY || p.Y > Height - HintH) return -1;
        int off = p.Y - ListY + (int)Math.Round(scrollCur);
        int idx = off / Pitch;
        if (off % Pitch > CardH || idx >= view.Count) return -1;
        return idx;
    }

    void ClampScroll()
    {
        int max = Math.Max(0, view.Count * Pitch - Pitch + CardH - VisibleH + 4);
        scrollPx = Math.Max(0, Math.Min(max, scrollPx));
    }

    void EnsureVisible()
    {
        if (sel < 0) return;
        int top = sel * Pitch;
        if (top < scrollPx) scrollPx = top;
        else if (top + CardH > scrollPx + VisibleH) scrollPx = top + CardH - VisibleH + 4;
        ClampScroll();
        StartAnim();
    }

    // ---------- рисование ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        EnsureBg();
        g.DrawImageUnscaled(bgCache, 0, 0);
        DrawTabs(g); DrawSearch(g); DrawList(g);
        TextRenderer.DrawText(g, "Клик копировать  ·  Enter вставить  ·  Tab категория  ·  Ctrl+P закрепить  ·  Ctrl+T преобразовать  ·  Ctrl+O открыть  ·  Del удалить",
            HintFont, new Rectangle(Pad + 2, Height - HintH, Width - 2 * Pad, HintH), Dim,
            TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        using (var bp = RoundRect(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 14))
        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f)) g.DrawPath(pen, bp);
        if (copiedT > 0f) DrawCopiedToast(g);
        if (menuT > 0f) DrawMenu(g);
    }

    void DrawCopiedToast(Graphics g)
    {
        string txt = "✓ Скопировано";
        var sz = TextRenderer.MeasureText(g, txt, MetaFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int w = sz.Width + 26, h = 24;
        int x = (Width - w) / 2, y = Height - HintH - h - 8;
        int a = (int)Math.Round(255 * Math.Min(1f, copiedT));
        if (a <= 0) return;
        using (var path = RoundRect(new RectangleF(x, y, w, h), h / 2f))
        {
            using (var b = new SolidBrush(Color.FromArgb((int)(a * 0.92), 30, 30, 33))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb((int)(a * 0.5), 255, 255, 255), 1f)) g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, txt, MetaFont, new Rectangle(x + 13, y, w - 26, h), Color.FromArgb(a, 235, 235, 240),
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
    }

    void DrawTabs(Graphics g)
    {
        // фон всех вкладок
        for (int i = 0; i < 4; i++)
        {
            var r = TabRect(i);
            bool hov = i == hoverTab && i != category;
            using (var path = RoundRect(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(Color.FromArgb(hov ? 36 : 18, 255, 255, 255))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(hov ? 55 : 28, 255, 255, 255), 1f)) g.DrawPath(pen, path);
            }
        }
        // активная «таблетка» плавно переезжает между вкладками
        int i0 = Math.Max(0, Math.Min(3, (int)Math.Floor(tabPos))), i1 = Math.Min(3, i0 + 1);
        float f = tabPos - i0;
        Rectangle ta = TabRect(i0), tb = TabRect(i1);
        var ar = new RectangleF(ta.X + (tb.X - ta.X) * f, ta.Y, ta.Width, ta.Height);
        for (int k = 3; k >= 1; k--)
            using (var gp = RoundRect(RectangleF.Inflate(ar, k, k), ar.Height / 2f + k))
            using (var pen = new Pen(Color.FromArgb(22 - 5 * k, 255, 255, 255), 2f)) g.DrawPath(pen, gp);
        using (var path = RoundRect(ar, ar.Height / 2f))
        {
            using (var b = new LinearGradientBrush(Rectangle.Round(ar), Color.FromArgb(96, 96, 101), Color.FromArgb(58, 58, 62), 90f)) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawPath(pen, path);
        }
        // иконки и подписи
        for (int i = 0; i < 4; i++)
        {
            var r = TabRect(i);
            bool active = i == category, hov = i == hoverTab;
            Color main = active ? Color.White : (hov ? Fg : Color.FromArgb(188, 188, 193));
            Color cnt = active ? Color.FromArgb(205, 205, 210) : Dim;
            DrawIcon(g, i, r.X + 13, r.Y + (r.Height - 16) / 2f, main);
            int x = r.X + 13 + 16 + 7;
            var sz = TextRenderer.MeasureText(g, TabNames[i], TabFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, TabNames[i], TabFont, new Point(x, r.Y + (r.Height - sz.Height) / 2), main, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, counts[i].ToString(), TabFont, new Point(x + sz.Width + 5, r.Y + (r.Height - sz.Height) / 2), cnt, TextFormatFlags.NoPadding);
        }
    }

    void DrawSearch(Graphics g)
    {
        var r = new Rectangle(Pad, SearchY, Width - 2 * Pad, SearchH);
        bool foc = search.Focused;
        using (var p = RoundRect(r, 12))
        using (var b = new SolidBrush(SearchBg))
        using (var pen = new Pen(Color.FromArgb(foc ? 150 : 30, foc ? Accent : Color.White), 1f))
        {
            g.FillPath(b, p); g.DrawPath(pen, p);
        }
        using (var pen = new Pen(Dim, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, Pad + 14, SearchY + 10, 10, 10);
            g.DrawLine(pen, Pad + 22.5f, SearchY + 19.5f, Pad + 26.5f, SearchY + 23.5f);
        }
    }

    void DrawList(Graphics g)
    {
        var clip = new Rectangle(0, ListY, Width, VisibleH);
        g.SetClip(clip);
        if (view.Count == 0)
        {
            TextRenderer.DrawText(g, "Здесь пока пусто", BigFont, new Rectangle(0, ListY + 40, Width, 30), Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Скопируй что-нибудь — оно появится тут", MetaFont, new Rectangle(0, ListY + 70, Width, 20),
                Color.FromArgb(100, 100, 106), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        int sc = (int)Math.Round(scrollCur);
        // подсветка выбора плавно скользит между карточками
        if (sel >= 0 && sel < view.Count)
            DrawHighlight(g, new Rectangle(Pad, ListY + (int)Math.Round(selY) - sc + CascadeOffset(sel, sc), Width - 2 * Pad, CardH));
        for (int i = 0; i < view.Count; i++)
        {
            int y = ListY + i * Pitch - sc;
            if (y + CardH < ListY || y > ListY + VisibleH) continue;
            DrawCard(g, i, y + CascadeOffset(i, sc));
        }
        int content = view.Count * Pitch;
        if (content > VisibleH)
        {
            float h = Math.Max(30f, VisibleH * (float)VisibleH / content);
            float ty = ListY + (VisibleH - h) * scrollCur / Math.Max(1, content - VisibleH);
            using (var p = RoundRect(new RectangleF(Width - 8, ty, 3, h), 1.5f))
            using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillPath(b, p);
        }
        g.ResetClip();
    }

    void DrawCard(Graphics g, int i, int y)
    {
        var e = view[i];
        bool selected = i == sel, hov = i == hoverItem;
        var rect = new Rectangle(Pad, y, Width - 2 * Pad, CardH);
        if (!selected) // у выбранной фон рисует скользящая подсветка
            using (var path = RoundRect(rect, 12))
            {
                using (var b = new SolidBrush(Color.FromArgb(hov ? 30 : 15, 255, 255, 255))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(hov ? 46 : 26, 255, 255, 255), 1f)) g.DrawPath(pen, path);
            }
        // полоска закреплённых
        if (e.Pinned)
            using (var p = RoundRect(new RectangleF(rect.X + 1.5f, rect.Y + 9, 3, CardH - 18), 1.5f))
            using (var b = new SolidBrush(e.Pinned ? Gold : Accent)) g.FillPath(b, p);

        // бейдж типа
        int cat = e.IsImage ? 2 : (e.IsFile ? 3 : 1);
        string kind = e.Kind;
        string badgeLabel = kind ?? BadgeText[cat];
        Color badgeCol = selected ? Color.FromArgb(104, 104, 110) : Color.FromArgb(70, 70, 76);
        var badge = new Rectangle(rect.X + 16, rect.Y + 10, 76, 20);
        using (var p = RoundRect(badge, 10))
        using (var b = new SolidBrush(badgeCol)) g.FillPath(b, p);
        TextRenderer.DrawText(g, badgeLabel, BadgeFont, badge, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // текстовая колонка
        int x = rect.X + 104;
        if (e.IsImage && e.Thumb != null)
        {
            g.DrawImage(e.Thumb, x, rect.Y + (CardH - e.Thumb.Height) / 2, e.Thumb.Width, e.Thumb.Height);
            x += e.Thumb.Width + 12;
        }
        if (kind == "ЦВЕТ")
        {
            Color sw;
            if (Detect.TryColor(e.Text.Trim(), out sw))
            {
                var sr = new Rectangle(x, rect.Y + 11, 16, 16);
                using (var sp = RoundRect(sr, 4))
                {
                    using (var sb2 = new SolidBrush(sw)) g.FillPath(sb2, sp);
                    using (var spen = new Pen(Color.FromArgb(100, 255, 255, 255), 1f)) g.DrawPath(spen, sp);
                }
                x += 24;
            }
        }
        int right = rect.Right - 52;
        string title, meta;
        string time = e.Time.ToString("dd.MM HH:mm");
        if (e.IsImage) { title = "Скриншот / картинка"; meta = time; }
        else if (e.IsFile)
        {
            title = FileTitle(e);
            string dir = ""; try { dir = Path.GetDirectoryName(e.Text.Split('\n')[0]); } catch { }
            meta = time + "  •  " + dir;
        }
        else
        {
            title = e.Text.Replace("\r", "").Split('\n')[0].Replace("\t", " ");
            meta = time + "  •  " + e.Text.Length + " симв.";
        }
        TextRenderer.DrawText(g, title, UiFont, new Rectangle(x, rect.Y + 8, Math.Max(10, right - x), 22), Color.White, TF | TextFormatFlags.VerticalCenter);
        Color metaCol = selected ? Color.FromArgb(215, 215, 220) : Dim;
        int metaX = x, metaRight = right;
        if (e.Pinned) metaRight -= 78;
        TextRenderer.DrawText(g, meta, MetaFont, new Rectangle(metaX, rect.Y + 32, Math.Max(10, metaRight - metaX), 16), metaCol, TF | TextFormatFlags.VerticalCenter);
        if (e.Pinned)
        {
            int mw = TextRenderer.MeasureText(g, meta, MetaFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            int px = Math.Min(metaX + mw + 8, right - 70);
            DrawPin(g, px, rect.Y + 33, Gold);
            TextRenderer.DrawText(g, "закреплено", MetaFont, new Rectangle(px + 12, rect.Y + 32, 64, 16), Gold, TF | TextFormatFlags.VerticalCenter);
        }

        // номер 1–9
        if (i < 9)
        {
            var c = new Rectangle(rect.Right - 14 - 26, rect.Y + (CardH - 26) / 2, 26, 26);
            using (var b = new SolidBrush(Color.FromArgb(selected ? 46 : 28, 255, 255, 255))) g.FillEllipse(b, c);
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f)) g.DrawEllipse(pen, c);
            TextRenderer.DrawText(g, (i + 1).ToString(), NumFont, c, selected ? Color.White : Color.FromArgb(196, 196, 201),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    void DrawHighlight(Graphics g, Rectangle rect)
    {
        using (var path = RoundRect(rect, 12))
        {
            for (int k = 3; k >= 1; k--)
                using (var gp = RoundRect(Rectangle.Inflate(rect, k, k), 12 + k))
                using (var pen = new Pen(Color.FromArgb(24 - 6 * k, 255, 255, 255), 2f)) g.DrawPath(pen, gp);
            using (var b = new LinearGradientBrush(rect, Color.FromArgb(52, 255, 255, 255), Color.FromArgb(30, 255, 255, 255), 90f)) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(150, 220, 220, 225), 1.4f)) g.DrawPath(pen, path);
        }
        using (var p = RoundRect(new RectangleF(rect.X + 1.5f, rect.Y + 9, 3, CardH - 18), 1.5f))
        using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
    }

    static string FileTitle(Entry e)
    {
        var paths = e.Text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) return "(пусто)";
        string first = Path.GetFileName(paths[0].TrimEnd('\\'));
        if (string.IsNullOrEmpty(first)) first = paths[0];
        return paths.Length == 1 ? first : first + "  +" + (paths.Length - 1);
    }

    static void DrawPin(Graphics g, float x, float y, Color c)
    {
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x + 1.5f, y, 6, 6);
        using (var pen = new Pen(c, 1.5f) { EndCap = LineCap.Round }) g.DrawLine(pen, x + 4.5f, y + 6, x + 4.5f, y + 11);
    }

    // иконки 16x16: 0 слои, 1 документ, 2 картинка, 3 папка
    static void DrawIcon(Graphics g, int kind, float x, float y, Color c)
    {
        using (var pen = new Pen(c, 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            switch (kind)
            {
                case 0:
                    g.DrawPolygon(pen, new[] { new PointF(x + 8, y + 1.5f), new PointF(x + 15, y + 5.5f), new PointF(x + 8, y + 9.5f), new PointF(x + 1, y + 5.5f) });
                    g.DrawLines(pen, new[] { new PointF(x + 1, y + 9), new PointF(x + 8, y + 13), new PointF(x + 15, y + 9) });
                    break;
                case 1:
                    using (var p = RoundRect(new RectangleF(x + 2.5f, y + 1, 11, 14), 2)) g.DrawPath(pen, p);
                    g.DrawLine(pen, x + 5.5f, y + 6, x + 10.5f, y + 6);
                    g.DrawLine(pen, x + 5.5f, y + 9.5f, x + 10.5f, y + 9.5f);
                    break;
                case 2:
                    using (var p = RoundRect(new RectangleF(x + 1, y + 2.5f, 14, 11), 2)) g.DrawPath(pen, p);
                    g.DrawEllipse(pen, x + 9.5f, y + 5, 2.5f, 2.5f);
                    g.DrawLines(pen, new[] { new PointF(x + 2.5f, y + 12), new PointF(x + 6, y + 8.5f), new PointF(x + 9, y + 11), new PointF(x + 11.5f, y + 9.5f), new PointF(x + 14, y + 12) });
                    break;
                default:
                    g.DrawPolygon(pen, new[] { new PointF(x + 1.5f, y + 3.5f), new PointF(x + 6, y + 3.5f), new PointF(x + 7.5f, y + 5.5f),
                        new PointF(x + 14.5f, y + 5.5f), new PointF(x + 14.5f, y + 13), new PointF(x + 1.5f, y + 13) });
                    break;
            }
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- мышь ----------
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int t = HitTab(e.Location), it = HitItem(e.Location);
        if (t != hoverTab || it != hoverItem) { hoverTab = t; hoverItem = it; Invalidate(); }
        int mr = MenuRowAt(e.Location);
        if (mr != menuHover) { menuHover = mr; Invalidate(); }
        Cursor = (t >= 0 || it >= 0 || mr >= 0) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        bool changed = hoverTab != -1 || hoverItem != -1 || menuHover != -1;
        hoverTab = hoverItem = -1; menuHover = -1;
        if (changed) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            int rit = HitItem(e.Location);
            if (rit >= 0) { sel = rit; StartAnim(); Invalidate(); OpenItemMenu(view[rit], e.Location); }
            else CloseMenu();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (menuOn)
        {
            int row = MenuRowAt(e.Location);
            if (row >= 0) { var act = menuActions[row]; CloseMenu(); if (act != null) act(); }
            else CloseMenu();
            return;
        }
        int t = HitTab(e.Location);
        if (t >= 0) { SetCategory(t); return; }
        int it = HitItem(e.Location);
        if (it >= 0) { CopyEntry(view[it]); return; }   // клик по карточке — копировать, окно не закрываем
        search.Focus();
    }

    // ---------- контекстное меню карточки (правая кнопка) ----------
    void OpenItemMenu(Entry e, Point at)
    {
        var entry = e;
        menuLabels = new[] { "Скопировать", e.Pinned ? "Открепить" : "Закрепить", "Удалить" };
        menuActions = new Action[]
        {
            () => CopyEntry(entry),
            () => TogglePinEntry(entry),
            () => DeleteEntry(entry)
        };
        int w = MenuW, h = MenuH;
        int x = Math.Max(4, Math.Min(at.X, Width - w - 4));
        int y = Math.Max(4, Math.Min(at.Y, Height - h - 4));
        menuPos = new Point(x, y);
        menuHover = -1;
        menuT = 0f;
        menuOn = true;
        StartAnim();
        Invalidate();
    }

    void CloseMenu()
    {
        if (!menuOn && menuT <= 0f) return;
        menuOn = false;
        StartAnim();
    }

    int MenuRowAt(Point p)
    {
        if (!menuOn || menuLabels == null) return -1;
        int left = menuPos.X, top = menuPos.Y;
        if (p.X < left || p.X > left + MenuW || p.Y < top || p.Y > top + MenuH) return -1;
        int y = p.Y - top - MenuPad;
        if (y < 0) return -1;
        int row = y / MenuRowH;
        return row >= 0 && row < menuLabels.Length ? row : -1;
    }

    void DrawMenu(Graphics g)
    {
        int w = MenuW, h = MenuH;
        float k = Ease(menuT);
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
        using (var mg = Graphics.FromImage(bmp))
        {
            mg.SmoothingMode = SmoothingMode.AntiAlias;
            mg.Clear(Color.Transparent);
            RenderMenu(mg, w, h);
            var dst = new Rectangle(menuPos.X, menuPos.Y + (int)Math.Round((1f - k) * 10f), w, h);
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix();
                cm.Matrix00 = cm.Matrix11 = cm.Matrix22 = 1f;
                cm.Matrix33 = k;
                ia.SetColorMatrix(cm);
                g.DrawImage(bmp, dst, 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
        }
    }

    void RenderMenu(Graphics g, int w, int h)
    {
        var full = new Rectangle(0, 0, w - 1, h - 1);
        using (var path = DarkRenderer.Round(full, 10))
        {
            using (var b = new SolidBrush(Color.FromArgb(252, 43, 43, 46))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(72, 72, 76), 1f)) g.DrawPath(pen, path);
        }
        for (int i = 0; i < menuLabels.Length; i++)
        {
            if (i == 2)
            {
                int ly = MenuPad + i * MenuRowH - 1;
                using (var pen = new Pen(Color.FromArgb(66, 66, 70), 1f)) g.DrawLine(pen, 10, ly, w - 10, ly);
            }
            var row = new Rectangle(4, MenuPad + i * MenuRowH, w - 8, MenuRowH - 2);
            if (i == menuHover)
            {
                using (var path = DarkRenderer.Round(row, 7))
                using (var b = new SolidBrush(Color.FromArgb(64, 64, 68))) g.FillPath(b, path);
            }
            var tr = new Rectangle(row.X + 12, row.Y, row.Width - 16, row.Height);
            TextRenderer.DrawText(g, menuLabels[i], MenuFont, tr, Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scrollPx -= e.Delta / 120 * Pitch;
        ClampScroll();
        StartAnim();
        hoverItem = HitItem(PointToClient(Cursor.Position));
        Invalidate();
    }

    // ---------- клавиатура ----------
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Tab)
        {
            int d = (keyData & Keys.Shift) == Keys.Shift ? -1 : 1;
            SetCategory((category + d + 4) % 4);
            return true;
        }
        if (key == Keys.Escape) { AnimateHide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void OnKey(object sender, KeyEventArgs k)
    {
        if (k.KeyCode == Keys.Enter) { Pick(); k.Handled = true; k.SuppressKeyPress = true; }
        else if (k.KeyCode == Keys.Down) { MoveSel(1); k.Handled = true; }
        else if (k.KeyCode == Keys.Up) { MoveSel(-1); k.Handled = true; }
        else if (k.KeyCode == Keys.T && k.Control) { ShowTransforms(); k.Handled = true; k.SuppressKeyPress = true; }
        else if (k.KeyCode == Keys.O && k.Control) { OpenCurrent(); k.Handled = true; k.SuppressKeyPress = true; }
        else if (k.KeyCode == Keys.P && k.Control) { TogglePin(); k.Handled = true; k.SuppressKeyPress = true; }
        else if (k.KeyCode == Keys.Delete && (search.Text.Length == 0 || k.Shift)) { DeleteSel(); k.Handled = true; }
        else if (k.Control && k.KeyCode >= Keys.D1 && k.KeyCode <= Keys.D9)
        {
            int i = k.KeyCode - Keys.D1;
            if (i < view.Count) { sel = i; Pick(); }
            k.Handled = true; k.SuppressKeyPress = true;
        }
    }

    void MoveSel(int d)
    {
        if (view.Count == 0) return;
        sel = Math.Max(0, Math.Min(view.Count - 1, sel + d));
        EnsureVisible(); Invalidate();
    }

    Entry Current { get { return sel >= 0 && sel < view.Count ? view[sel] : null; } }

    // Ctrl+T: меню быстрых преобразований — результат сразу вставляется в окно, откуда открывали историю
    void ShowTransforms()
    {
        var e = Current;
        if (e == null || e.IsImage || e.IsFile) return;
        var m = new ContextMenuStrip();
        DarkRenderer.Style(m);
        foreach (var kv in Transforms.All)
        {
            var fn = kv.Value;
            var it = m.Items.Add(kv.Key);
            it.Click += (s, ev) => ApplyTransform(e, fn);
        }
        m.Show(this, new Point(Math.Max(8, Width - 300), 70));
    }

    void ApplyTransform(Entry e, Func<string, string> fn)
    {
        string r;
        try { r = fn(e.Text); }
        catch { r = null; }
        if (string.IsNullOrEmpty(r)) { SystemSounds.Beep.Play(); return; }   // например, «Из Base64» на обычном тексте
        PasteEntry(new Entry { Id = DateTime.Now.Ticks, Time = DateTime.Now, Text = r });
    }

    // Ctrl+O: ссылка → браузер, почта → почтовый клиент, файл → показать в проводнике
    void OpenCurrent()
    {
        var e = Current; if (e == null || e.IsImage) return;
        try
        {
            if (e.IsFile)
            {
                string first = e.Text.Split('\n')[0].Trim();
                Hide();
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + first + "\"");
                return;
            }
            string t = e.Text.Trim(), target = null;
            if (e.Kind == "ССЫЛКА") target = t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + t : t;
            else if (e.Kind == "ПОЧТА") target = "mailto:" + t;
            if (target == null) return;
            Hide();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    void TogglePin()
    {
        var e = Current; if (e != null) TogglePinEntry(e);
    }

    void TogglePinEntry(Entry e)
    {
        e.Pinned = !e.Pinned; Store.Save();
        Refill(); sel = view.IndexOf(e); EnsureVisible(); Invalidate();
    }

    void DeleteSel()
    {
        var e = Current; if (e != null) DeleteEntry(e);
    }

    void DeleteEntry(Entry e)
    {
        int idx = view.IndexOf(e);
        Store.Remove(e); Store.Save(); Refill();
        sel = view.Count > 0 ? Math.Max(0, Math.Min(idx, view.Count - 1)) : -1;
        EnsureVisible(); Invalidate();
    }

    void Pick() { PasteEntry(Current); }

    // Клик по карточке: копируем в буфер, но окно не закрываем (можно копировать несколько подряд)
    void CopyEntry(Entry e)
    {
        if (e == null) return;
        if (!e.IsImage && !e.IsFile && string.IsNullOrWhiteSpace(e.Text)) return;   // пустое не копируем
        if (OnPick != null) OnPick(e);      // OnPick -> PutToClipboard: только копирует в буфер
        sel = view.IndexOf(e);
        if (sel < 0) sel = 0;
        copiedId = e.Id; copiedT = 1f;
        StartAnim();
        Invalidate();
    }

    void PasteEntry(Entry e)
    {
        if (e == null) return;
        opening = closing = false; // при вставке прячемся мгновенно, чтобы сразу вернуть фокус
        Hide();
        if (OnPick != null) OnPick(e);
        if (prevWindow != IntPtr.Zero)
        {
            Native.SetForegroundWindow(prevWindow);
            // ждём, пока отпустят Shift/Ctrl, чтобы не получилось Ctrl+Shift+V
            for (int i = 0; i < 40 && ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0); i++) Thread.Sleep(25);
            Thread.Sleep(60);
            SendKeys.SendWait("^v");
        }
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* toolwindow */ return cp; }
    }
}

// ---------- Быстрые преобразования текста (Ctrl+T в окне истории) ----------
static class Transforms
{
    public static readonly KeyValuePair<string, Func<string, string>>[] All = new[]
    {
        K("ВЕРХНИЙ РЕГИСТР", s => s.ToUpper()),
        K("нижний регистр", s => s.ToLower()),
        K("Каждое Слово С Заглавной", s => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower())),
        K("snake_case", s => string.Join("_", Words(s))),
        K("kebab-case", s => string.Join("-", Words(s))),
        K("camelCase", s => { var w = Words(s); return w.Length == 0 ? s : w[0] + string.Join("", w.Skip(1).Select(Cap).ToArray()); }),
        K("PascalCase", s => string.Join("", Words(s).Select(Cap).ToArray())),
        K("Убрать переносы строк", s => Regex.Replace(s, @"\s*[\r\n]+\s*", " ").Trim()),
        K("Убрать лишние пробелы", s => string.Join("\n", Lines(s).Select(l => Regex.Replace(l, @"[ \t]+", " ").Trim()).ToArray())),
        K("Сортировать строки", s => string.Join("\n", Lines(s).OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase).ToArray())),
        K("Убрать повторяющиеся строки", s => string.Join("\n", Lines(s).Distinct().ToArray())),
        K("Строки → через запятую", s => string.Join(", ", Lines(s).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray())),
        K("Нумерованный список", s => string.Join("\n", Lines(s).Where(l => l.Trim().Length > 0).Select((l, i) => (i + 1) + ". " + l.Trim()).ToArray())),
        K("Закодировать для URL", s => Uri.EscapeDataString(s)),
        K("Раскодировать из URL", s => Uri.UnescapeDataString(s)),
        K("В Base64", s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        K("Из Base64", s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())))
    };

    static KeyValuePair<string, Func<string, string>> K(string name, Func<string, string> f)
    {
        return new KeyValuePair<string, Func<string, string>>(name, f);
    }

    static string[] Lines(string s) { return s.Replace("\r", "").Split('\n'); }

    static string Cap(string w) { return w.Length == 0 ? w : char.ToUpper(w[0]) + w.Substring(1); }

    static string[] Words(string s)
    {
        s = Regex.Replace(s, @"(?<=[a-zа-я0-9])(?=[A-ZА-Я])", " ");
        return Regex.Matches(s, @"[\p{L}\p{N}]+").Cast<Match>().Select(m => m.Value.ToLower()).ToArray();
    }
}

// ---------- Элементы управления для окна настроек ----------
class Toggle : Control
{
    bool on;
    public bool Checked { get { return on; } set { on = value; Invalidate(); } }

    public Toggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; Height = 32;
        BackColor = Color.FromArgb(26, 26, 28); ForeColor = Color.FromArgb(236, 236, 240);
        Font = new Font("Segoe UI", 10f);
    }

    protected override void OnClick(EventArgs e) { on = !on; Invalidate(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 60, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        var tr = new Rectangle(Width - 46, (Height - 22) / 2, 42, 22);
        using (var p = DarkRenderer.Round(tr, 11))
        using (var b = new SolidBrush(on ? Color.FromArgb(222, 222, 228) : Color.FromArgb(66, 66, 72))) g.FillPath(b, p);
        int d = 16, tx = on ? tr.Right - d - 3 : tr.X + 3;
        using (var b = new SolidBrush(on ? Color.FromArgb(30, 30, 32) : Color.FromArgb(190, 190, 196))) g.FillEllipse(b, tx, tr.Y + 3, d, d);
    }
}

class Slider : Control
{
    int val = 60; bool drag;
    public event EventHandler Changed;
    public int Value { get { return val; } set { val = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; Height = 28; BackColor = Color.FromArgb(26, 26, 28);
    }

    void SetFromX(int x)
    {
        Value = (int)Math.Round((x - 9) * 100.0 / Math.Max(1, Width - 18));
        if (Changed != null) Changed(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        int cy = Height / 2, x0 = 9, x1 = Width - 9, tx = x0 + (x1 - x0) * val / 100;
        using (var p = new Pen(Color.FromArgb(66, 66, 72), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x0, cy, x1, cy);
        using (var p = new Pen(Color.FromArgb(205, 205, 211), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x0, cy, tx, cy);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, tx - 8, cy - 8, 16, 16);
    }
}

// Поле для записи сочетания клавиш: кликнул, нажал комбинацию — запомнилась
class HotkeyBox : Control
{
    public uint Mods, Key;

    public HotkeyBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true; Cursor = Cursors.Hand; Height = 32;
        BackColor = Color.FromArgb(26, 26, 28); Font = new Font("Segoe UI Semibold", 10f);
    }

    public void Set(uint mods, uint key) { Mods = mods; Key = key; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) { return true; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
        Keys k = keyData & Keys.KeyCode;
        if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return true;
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((keyData & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if ((keyData & Keys.Alt) != 0) mods |= Native.MOD_ALT;
        if (mods == 0) return true;   // нужен хотя бы один модификатор, иначе клавиша «сломается» везде
        Set(mods, (uint)k);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var p = DarkRenderer.Round(r, 9))
        {
            using (var b = new SolidBrush(Color.FromArgb(46, 46, 50))) g.FillPath(b, p);
            using (var pen = new Pen(Focused ? Color.FromArgb(210, 210, 216) : Color.FromArgb(70, 70, 76), 1f)) g.DrawPath(pen, p);
        }
        string t = Focused && Key == 0 ? "Нажми сочетание…" : Settings.HotkeyText(Mods, Key);
        TextRenderer.DrawText(g, t, Font, r, Focused ? Color.White : Color.FromArgb(220, 220, 225),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// ---------- Окно настроек ----------
class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(26, 26, 28), Fg = Color.FromArgb(236, 236, 240), Dim = Color.FromArgb(140, 140, 146), Field = Color.FromArgb(46, 46, 50);
    readonly Action onApply;
    HotkeyBox hkOpen = new HotkeyBox(), hkPlain = new HotkeyBox();
    Toggle tPlain, tAuto, tSound, tAnim, tStart;
    Slider vol = new Slider();
    NumericUpDown hist = new NumericUpDown();
    TextBox excl = new TextBox();

    public SettingsForm(Action apply)
    {
        onApply = apply;
        Text = "ClipVault — настройки";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(520, 704);

        int y = 16;
        Action<string> section = t =>
        {
            Controls.Add(new Label { Text = t, ForeColor = Dim, BackColor = Bg, Font = new Font("Segoe UI Semibold", 8.5f), Left = 24, Top = y, Width = 472, Height = 20 });
            y += 28;
        };
        Func<string, bool, Toggle> toggle = (text, on) =>
        {
            var t = new Toggle { Text = text, Checked = on, Left = 24, Top = y, Width = 472, Height = 32 };
            Controls.Add(t); y += 38; return t;
        };
        Action<string> label = text =>
            Controls.Add(new Label { Text = text, ForeColor = Fg, BackColor = Bg, Left = 24, Top = y + 4, Width = 260, Height = 24 });

        section("ГОРЯЧИЕ КЛАВИШИ");
        label("Открыть историю");
        hkOpen.Set(Settings.HotMods, Settings.HotKey);
        hkOpen.SetBounds(300, y, 196, 32); Controls.Add(hkOpen); y += 44;
        tPlain = toggle("Вставка без форматирования", Settings.PlainHotkeyOn);
        label("Сочетание для неё");
        hkPlain.Set(Settings.PlainMods, Settings.PlainKey);
        hkPlain.SetBounds(300, y, 196, 32); Controls.Add(hkPlain); y += 52;

        section("АВТО-КОПИРОВАНИЕ ВЫДЕЛЕНИЯ");
        tAuto = toggle("Копировать выделенное сразу", Settings.AutoCopy);
        tSound = toggle("Звук при копировании", Settings.Sound);
        label("Громкость");
        vol.Value = Settings.Volume; vol.SetBounds(130, y, 270, 28); Controls.Add(vol);
        var test = MakeButton("Проверить", 412, y - 3, 84, false);
        test.Click += delegate { try { HostForm.MakePop(vol.Value).Play(); } catch { } };
        y += 44;
        Controls.Add(new Label { Text = "Не копировать автоматически в программах (по одной в строке):", ForeColor = Dim, BackColor = Bg, Left = 24, Top = y, Width = 472, Height = 22, Font = new Font("Segoe UI", 9f) });
        y += 26;
        var box = new Panel { Left = 24, Top = y, Width = 472, Height = 96, BackColor = Field, Padding = new Padding(12, 8, 12, 8) };
        excl.Multiline = true; excl.BorderStyle = BorderStyle.None; excl.BackColor = Field; excl.ForeColor = Fg;
        excl.Dock = DockStyle.Fill; excl.ScrollBars = ScrollBars.Vertical; excl.Font = new Font("Consolas", 10f);
        excl.Text = string.Join("\r\n", Settings.ExcludeList());
        box.Controls.Add(excl); Controls.Add(box); y += 112;

        section("ИСТОРИЯ И ВИД");
        label("Хранить записей (не считая закреплённых)");
        hist.Minimum = 20; hist.Maximum = 5000; hist.Value = Math.Max(20, Math.Min(5000, Settings.MaxHistory));
        hist.BackColor = Field; hist.ForeColor = Fg; hist.BorderStyle = BorderStyle.None; hist.TextAlign = HorizontalAlignment.Center;
        hist.SetBounds(412, y + 2, 84, 28); Controls.Add(hist); y += 42;
        tAnim = toggle("Анимации окна", Settings.Animations);

        section("СИСТЕМА");
        tStart = toggle("Запускать вместе с Windows", HostForm.IsAutostart());

        y = ClientSize.Height - 64;
        var folder = MakeButton("Папка данных", 24, y, 140, false);
        folder.Click += delegate
        {
            try
            {
                Directory.CreateDirectory(Store.ImgDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Store.Dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Error("OpenFolder", ex); }
        };
        var cancel = MakeButton("Отмена", 304, y, 92, false);
        cancel.Click += delegate { Close(); };
        var save = MakeButton("Сохранить", 404, y, 92, true);
        save.Click += delegate { Apply(); };
    }

    Button MakeButton(string text, int x, int y, int w, bool primary)
    {
        var b = new Button
        {
            Text = text, Left = x, Top = y, Width = w, Height = 36, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            ForeColor = primary ? Color.FromArgb(20, 20, 22) : Fg,
            BackColor = primary ? Color.FromArgb(226, 226, 231) : Color.FromArgb(54, 54, 58)
        };
        b.FlatAppearance.BorderSize = 0;
        Controls.Add(b);
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }   // тёмный заголовок окна
    }

    void Apply()
    {
        if (tPlain.Checked && hkOpen.Mods == hkPlain.Mods && hkOpen.Key == hkPlain.Key)
        {
            MessageBox.Show("Два действия не могут иметь одно и то же сочетание клавиш.", "ClipVault");
            return;
        }
        if (hkOpen.Key != 0) { Settings.HotMods = hkOpen.Mods; Settings.HotKey = hkOpen.Key; }
        if (hkPlain.Key != 0) { Settings.PlainMods = hkPlain.Mods; Settings.PlainKey = hkPlain.Key; }
        Settings.PlainHotkeyOn = tPlain.Checked;
        Settings.AutoCopy = tAuto.Checked;
        Settings.Sound = tSound.Checked;
        Settings.Volume = vol.Value;
        Settings.Animations = tAnim.Checked;
        Settings.MaxHistory = (int)hist.Value;
        Settings.Exclude = string.Join(",", excl.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray());
        Settings.Save();
        HostForm.SetAutostart(tStart.Checked);
        if (onApply != null) onApply();
        Close();
    }
}

// Тёмное контекстное меню трея в стиле Windows 11
class DarkColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(43, 43, 46);
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Color.FromArgb(70, 70, 74); } }
    public override Color MenuItemBorder { get { return Bg; } }
    public override Color MenuItemSelected { get { return Color.FromArgb(62, 62, 66); } }
    public override Color SeparatorDark { get { return Color.FromArgb(70, 70, 74); } }
    public override Color SeparatorLight { get { return Color.FromArgb(70, 70, 74); } }
}

class DarkRenderer : ToolStripProfessionalRenderer
{
    static readonly Color Bg = Color.FromArgb(43, 43, 46), Hover = Color.FromArgb(64, 64, 68), Line = Color.FromArgb(72, 72, 76);

    public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    // Единый стиль для всех тёмных меню (быстрые преобразования и т.п.)
    public static void Style(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkRenderer();
        menu.BackColor = Color.FromArgb(43, 43, 46);
        menu.ForeColor = Color.White;
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Padding = new Padding(0, 4, 0, 4);
        menu.Opening += (s, e) =>
        {
            foreach (ToolStripItem it in menu.Items)
                if (it is ToolStripMenuItem) it.Padding = new Padding(10, 5, 14, 5);
        };
        menu.Opened += (s, e) =>
        {
            using (var rp = Round(new Rectangle(0, 0, menu.Width, menu.Height), 8)) menu.Region = new Region(rp);
        };
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (var b = new SolidBrush(Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Round(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 8))
        using (var pen = new Pen(Line, 1f)) e.Graphics.DrawPath(pen, p);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Round(new Rectangle(4, 1, e.Item.Width - 9, e.Item.Height - 3), 6))
        using (var b = new SolidBrush(Hover)) e.Graphics.FillPath(b, p);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(120, 120, 125);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            // позиция считается от самого пункта, а не от ImageRectangle (его сдвигают отступы)
            float cx = 18f, cy = e.Item.Height / 2f - 0.5f;
            pen.Width = 1.6f;
            e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4, cy), new PointF(cx - 1, cy + 3.2f), new PointF(cx + 4.5f, cy - 3.4f) });
        }
    }
}

class HostForm : Form
{
    NotifyIcon tray = new NotifyIcon();
    PickerForm picker = new PickerForm();
    bool ignoreNext;

    // --- авто-копирование выделения ---
    AutoCopier copier;
    SettingsForm settingsForm;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public HostForm()
    {
        ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        picker.OnPick = PutToClipboard;

        var menu = new ContextMenuStrip();
        menu.Renderer = new DarkRenderer();
        menu.BackColor = Color.FromArgb(43, 43, 46);
        menu.ForeColor = Color.White;
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(0, 4, 0, 4);
        menu.Opened += (s, e) =>
        {
            using (var rp = DarkRenderer.Round(new Rectangle(0, 0, menu.Width, menu.Height), 8))
                menu.Region = new Region(rp);
        };
        var openItem = new ToolStripMenuItem("Открыть историю", null, (s, e) => picker.ShowPicker());
        menu.Items.Add(openItem);
        menu.Items.Add("Настройки…", null, (s, e) => OpenSettings());
        copier = new AutoCopier(this);
        copier.Enabled = Settings.AutoCopy;
        var ac = new ToolStripMenuItem("Авто-копирование выделения") { Checked = Settings.AutoCopy };
        ac.Click += (s, e) =>
        {
            Settings.AutoCopy = !Settings.AutoCopy;
            copier.Enabled = Settings.AutoCopy; ac.Checked = Settings.AutoCopy;
            Settings.Save();
        };
        menu.Items.Add(ac);
        var auto = new ToolStripMenuItem("Запускать с Windows") { Checked = IsAutostart() };
        auto.Click += (s, e) => { SetAutostart(!auto.Checked); auto.Checked = IsAutostart(); };
        menu.Items.Add(auto);
        menu.Items.Add("Очистить (кроме закреплённых)", null, (s, e) =>
        {
            if (MessageBox.Show("Удалить всю незакреплённую историю?", "ClipVault", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Store.ClearUnpinned();
        });
        menu.Items.Add("Открыть папку данных", null, (s, e) =>
        {
            try
            {
                Directory.CreateDirectory(Store.ImgDir); // папка могла быть удалена — создаём заново
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Store.Dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Не удалось открыть папку:\n" + ex.Message, "ClipVault"); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (s, e) => { tray.Visible = false; Application.Exit(); });

        foreach (ToolStripItem it in menu.Items)
            if (it is ToolStripMenuItem) it.Padding = new Padding(4, 5, 10, 5);
            else if (it is ToolStripSeparator) it.Margin = new Padding(0, 3, 0, 3);

        menu.Opening += (s, e) =>
        {
            openItem.Text = "Открыть историю (" + Settings.HotkeyText(Settings.HotMods, Settings.HotKey) + ")";
            ac.Checked = Settings.AutoCopy;
            auto.Checked = IsAutostart();
        };

        tray.Icon = MakeIcon();
        tray.Text = "ClipVault — Ctrl+Shift+V";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) picker.ShowPicker(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.AddClipboardFormatListener(Handle);
        copier.Start();
        RegisterHotkeys(true);
    }

    void RegisterHotkeys(bool announce)
    {
        Native.UnregisterHotKey(Handle, 1);
        Native.UnregisterHotKey(Handle, 2);
        string main = Settings.HotkeyText(Settings.HotMods, Settings.HotKey);
        bool ok1 = Native.RegisterHotKey(Handle, 1, Settings.HotMods | Native.MOD_NOREPEAT, Settings.HotKey);
        bool ok2 = true;
        string plain = Settings.HotkeyText(Settings.PlainMods, Settings.PlainKey);
        if (Settings.PlainHotkeyOn) ok2 = Native.RegisterHotKey(Handle, 2, Settings.PlainMods | Native.MOD_NOREPEAT, Settings.PlainKey);
        if (!ok1)
            tray.ShowBalloonTip(3500, "ClipVault", main + " занято другой программой. Поменяй сочетание в настройках или открывай историю через иконку в трее.", ToolTipIcon.Warning);
        else if (!ok2)
            tray.ShowBalloonTip(3500, "ClipVault", plain + " занято другой программой. Поменяй сочетание в настройках.", ToolTipIcon.Warning);
        else if (announce)
            tray.ShowBalloonTip(2000, "ClipVault запущен", main + " — история буфера", ToolTipIcon.Info);
        tray.Text = "ClipVault — " + main;
    }

    void OpenSettings()
    {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Show(); settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(ApplySettings);
        settingsForm.Show();
        settingsForm.Activate();
    }

    void ApplySettings()
    {
        copier.Enabled = Settings.AutoCopy;
        copier.RebuildSound();
        RegisterHotkeys(false);
        Store.ApplyLimit();
    }

    // Вставка без форматирования: оставляем в буфере только чистый текст и жмём Ctrl+V
    void PastePlain()
    {
        string t = null;
        for (int i = 0; i < 5; i++)
        {
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); break; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        if (string.IsNullOrEmpty(t)) return;
        ignoreNext = true;
        try { Clipboard.SetText(t); } catch { ignoreNext = false; return; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            for (int i = 0; i < 40 && AutoCopier.AnyModifierDown(); i++) Thread.Sleep(25);
            Thread.Sleep(40);
            AutoCopier.SendCtrlV();
        });
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); if (!IsHandleCreated) CreateHandle(); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnClipboard();
        else if (m.Msg == Native.WM_HOTKEY)
        {
            if ((int)m.WParam == 2) PastePlain(); else picker.ShowPicker();
        }
        base.WndProc(ref m);
    }

    void OnClipboard()
    {
        if (ignoreNext) { ignoreNext = false; return; }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                    Store.AddFiles(Clipboard.GetFileDropList().Cast<string>().ToArray());
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage()) if (img != null) Store.AddImage(img);
                }
                else if (Clipboard.ContainsText())
                    Store.AddText(Clipboard.GetText());
                return;
            }
            catch (ExternalException) { Thread.Sleep(50); } // буфер занят другой прогой
            catch { return; }
        }
    }

    void PutToClipboard(Entry e)
    {
        ignoreNext = true;
        try
        {
            if (e.IsImage)
                using (var img = Image.FromFile(Path.Combine(Store.ImgDir, e.ImageFile))) Clipboard.SetImage(new Bitmap(img));
            else if (e.IsFile)
            {
                var paths = e.Text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
                if (paths.Length > 0)
                {
                    var sc = new StringCollection();
                    sc.AddRange(paths);
                    Clipboard.SetFileDropList(sc);
                }
                else Clipboard.SetText(e.Text);
            }
            else Clipboard.SetText(e.Text);
        }
        catch { ignoreNext = false; }
        // поднимаем выбранное наверх
        if (!e.Pinned) { Store.Items.Remove(e); e.Time = DateTime.Now; Store.Items.Insert(0, e); Store.Save(); }
    }

    // Вызывается из потока авто-копирования: кладём текст в буфер на UI-потоке
    public void SetTextFromAutoCopy(string text)
    {
        BeginInvoke((Action)(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch (ExternalException) { Thread.Sleep(40); }
                catch { return; }
            }
        }));
    }

    // Короткий мягкий «поп» — генерируется на лету, без внешних файлов
    public static System.Media.SoundPlayer MakePop(int volume)
    {
        double amp = 0.5 * Math.Max(0, Math.Min(100, volume)) / 100.0;
        int sr = 44100, n = sr * 80 / 1000, dataLen = n * 2;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16);
        bw.Write((short)1); bw.Write((short)1); bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataLen);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sr;
            double f = 700 + 500 * Math.Min(1.0, t / 0.03);        // быстрый подъём тона 700→1200 Гц
            phase += 2 * Math.PI * f / sr;
            double env = Math.Min(1.0, t / 0.004) * Math.Exp(-t * 48); // мягкая атака, быстрое затухание
            bw.Write((short)(Math.Sin(phase) * env * amp * short.MaxValue));
        }
        bw.Flush(); ms.Position = 0;
        var p = new System.Media.SoundPlayer(ms);
        p.Load();
        return p;
    }

    static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(96, 96, 102))) g.FillRectangle(b, 5, 4, 22, 26);
            using (var b = new SolidBrush(Color.FromArgb(235, 235, 240))) g.FillRectangle(b, 11, 1, 10, 6);
            using (var p = new Pen(Color.White, 2.5f)) { g.DrawLine(p, 9, 14, 23, 14); g.DrawLine(p, 9, 19, 23, 19); g.DrawLine(p, 9, 24, 18, 24); }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static bool IsAutostart()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ClipVault") != null; }
        catch (Exception ex) { Logger.Error("IsAutostart", ex); return false; }
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("ClipVault", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("ClipVault", false);
            }
        }
        catch (Exception ex) { Logger.Error("SetAutostart", ex); }
    }
}

// Авто-копирование выделения.
// Хук мыши живёт в своём потоке и ничего не делает сам (иначе мышь лагает и Windows снимает хук).
// Вся работа — в отдельном рабочем потоке: сначала пробуем взять выделение через UI Automation
// (без нажатий клавиш), и только если приложение это не поддерживает — шлём Ctrl+C через SendInput.
class AutoCopier
{
    public volatile bool Enabled = true;
    readonly HostForm host;
    Native.LowLevelMouseProc proc;   // ссылка, чтобы GC не убил делегат
    IntPtr hook;
    Native.POINT downPt, lastUpPt;
    DateTime lastUp = DateTime.MinValue;

    readonly object gate = new object();
    Native.POINT pendingPt;
    long pendingTicks;
    bool pending;
    readonly AutoResetEvent signal = new AutoResetEvent(false);
    System.Media.SoundPlayer pop = HostForm.MakePop(Settings.Volume);
    readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    public void RebuildSound() { try { pop = HostForm.MakePop(Settings.Volume); } catch (Exception ex) { Logger.Error("RebuildSound", ex); } }

    public AutoCopier(HostForm h) { host = h; }

    public void Start()
    {
        new Thread(HookThread) { IsBackground = true, Name = "ClipVault hook" }.Start();
        var w = new Thread(Worker) { IsBackground = true, Name = "ClipVault autocopy" };
        w.SetApartmentState(ApartmentState.MTA);
        w.Start();
    }

    void HookThread()
    {
        proc = Callback;
        hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, proc, Native.GetModuleHandle(null), 0);
        Native.MSG msg;
        while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int m = (int)wParam;
            if (m == 0x201) // WM_LBUTTONDOWN
            {
                var s = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                downPt = s.pt;
            }
            else if (m == 0x202) // WM_LBUTTONUP
            {
                var s = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                bool drag = Math.Abs(s.pt.X - downPt.X) >= 6 || Math.Abs(s.pt.Y - downPt.Y) >= 6;
                var now = DateTime.UtcNow;
                bool multi = (now - lastUp).TotalMilliseconds <= Native.GetDoubleClickTime()
                             && Math.Abs(s.pt.X - lastUpPt.X) <= 5 && Math.Abs(s.pt.Y - lastUpPt.Y) <= 5;
                lastUp = now; lastUpPt = s.pt;
                if (drag || multi)
                {
                    lock (gate) { pendingPt = downPt; pendingTicks = now.Ticks; pending = true; }
                    signal.Set();
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    void Worker()
    {
        while (true)
        {
            signal.WaitOne();
            // ждём паузу в кликах, чтобы двойной → тройной клик не давал лишних срабатываний
            while (true)
            {
                Thread.Sleep(40);
                long last; lock (gate) last = pendingTicks;
                if ((DateTime.UtcNow.Ticks - last) / 10000 >= 100) break;
            }
            Native.POINT pt;
            lock (gate) { if (!pending) continue; pending = false; pt = pendingPt; }
            try { Process(pt); } catch { }
        }
    }

    public static bool AnyModifierDown()
    {
        int[] keys = { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, Win
        foreach (int k in keys) if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return true;
        return false;
    }

    // Отсекаем заголовок окна, кнопки, скроллбары и рамки — остальное считаем контентом
    static bool IsContentHit(Native.POINT p)
    {
        IntPtr hwnd = Native.WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return true;
        IntPtr root = Native.GetAncestor(hwnd, 2 /*GA_ROOT*/);
        if (root == IntPtr.Zero) root = hwnd;
        IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)), res;
        if (Native.SendMessageTimeout(root, 0x84 /*WM_NCHITTEST*/, IntPtr.Zero, lp, 2, 100, out res) == IntPtr.Zero) return true;
        int h = (int)res;
        return !((h >= 2 && h <= 18) || h == 20 || h == 21);
    }

    void Process(Native.POINT pt)
    {
        if (!Enabled) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint pid; Native.GetWindowThreadProcessId(fg, out pid);
        if (pid == myPid) return;
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { return; }
        if (Settings.IsExcluded(name)) return;
        var sb = new StringBuilder(256); Native.GetClassName(fg, sb, 256);
        string cls = sb.ToString();
        if (cls == "ConsoleWindowClass" || cls == "CASCADIA_HOSTING_WINDOW_CLASS") return;
        if (!IsContentHit(pt)) return;

        // ждём, пока отпустят Shift/Ctrl/Alt/Win (иначе вместо Ctrl+C выйдет, например, Ctrl+Shift+C)
        for (int i = 0; i < 50 && AnyModifierDown(); i++) Thread.Sleep(30);
        if (AnyModifierDown()) return;

        // 1) UI Automation: читаем выделение напрямую, без нажатий
        bool supported;
        string text = TryUiaSelection(pid, out supported);
        if (supported)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return; // ничего не выделено
            host.SetTextFromAutoCopy(text);
            Play();
            return;
        }

        // 2) Запасной путь: настоящий Ctrl+C
        uint before = Native.GetClipboardSequenceNumber();
        SendCtrlC();
        for (int i = 0; i < 24 && Native.GetClipboardSequenceNumber() == before; i++) Thread.Sleep(25);
        if (Native.GetClipboardSequenceNumber() != before) Play();
    }

    static string TryUiaSelection(uint pid, out bool supported)
    {
        supported = false;
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el == null || el.Current.ProcessId != (int)pid) return null;
            var walker = TreeWalker.RawViewWalker;
            for (int depth = 0; el != null && depth < 8; depth++)
            {
                object pat;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                {
                    var ranges = ((TextPattern)pat).GetSelection();
                    supported = true;
                    var sb = new StringBuilder();
                    foreach (var r in ranges) sb.Append(r.GetText(-1));
                    return sb.ToString();
                }
                el = walker.GetParent(el);
            }
        }
        catch { supported = false; }
        return null;
    }

    static Native.INPUT Key(ushort vk, bool up)
    {
        var i = new Native.INPUT { type = 1 /*INPUT_KEYBOARD*/ };
        i.u.ki.wVk = vk;
        i.u.ki.wScan = (ushort)Native.MapVirtualKey(vk, 0);
        i.u.ki.dwFlags = up ? 2u /*KEYEVENTF_KEYUP*/ : 0u;
        return i;
    }

    static void SendCtrlC()
    {
        var inputs = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    public static void SendCtrlV()
    {
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    void Play() { if (!Settings.Sound) return; try { pop.Play(); } catch { } }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "ClipVault_SingleInstance", out created))
        {
            if (!created) { MessageBox.Show("ClipVault уже запущен — ищи иконку в трее.", "ClipVault"); return; }
            Application.EnableVisualStyles();
            // Любая необработанная ошибка пишется в лог, а программа продолжает работать
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Logger.Error("UI", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Logger.Error("Fatal", e.ExceptionObject as Exception);
            Settings.Load();
            Store.Load();
            Application.Run(new HostForm());
        }
    }
}

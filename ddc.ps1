# Long-lived DDC/CI worker. Reads one command per line from stdin, writes one JSON line to stdout.
#   list                 -> enumerate monitors with brightness / contrast / volume
#   set <id> <vcp> <val> -> write a VCP feature (vcp as decimal)
#   hotkeys on|off       -> register global hotkeys; presses arrive as {"hotkey":1|2} lines

$code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class Ddc {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct PM { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string d; }
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)]
  public struct MI { public int cb; public RECT m; public RECT w; public uint f; }
  public delegate bool EnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr p);

  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumProc cb, IntPtr d);
  [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MI mi);
  [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PM[] a);
  [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PM[] a);
  [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
  [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint v);

  [StructLayout(LayoutKind.Sequential)]
  public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
  [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
  [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

  static readonly object outLock = new object();
  public static void Emit(string s) {
    lock (outLock) { Console.Out.WriteLine(s); Console.Out.Flush(); }
  }

  // Global hotkeys live on their own thread with a message loop and are
  // reported as unsolicited {"hotkey":id} lines. 1 = Ctrl+Alt+PgUp, 2 = Ctrl+Alt+PgDn.
  static Thread hkThread;
  static uint hkTid;

  public static string HotkeysOn() {
    if (hkThread != null) return "{\"ok\":true}";
    var ready = new ManualResetEvent(false);
    bool ok = false;
    hkThread = new Thread(() => {
      hkTid = GetCurrentThreadId();
      ok = RegisterHotKey(IntPtr.Zero, 1, 0x0003, 0x21) & RegisterHotKey(IntPtr.Zero, 2, 0x0003, 0x22);
      ready.Set();
      MSG m;
      while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) {
        if (m.message == 0x0312) Emit("{\"hotkey\":" + m.wParam.ToInt32() + "}");
      }
      UnregisterHotKey(IntPtr.Zero, 1);
      UnregisterHotKey(IntPtr.Zero, 2);
    });
    hkThread.IsBackground = true;
    hkThread.Start();
    ready.WaitOne();
    if (!ok) { HotkeysOff(); return "{\"ok\":false,\"error\":\"hotkey is taken by another app\"}"; }
    return "{\"ok\":true}";
  }

  public static string HotkeysOff() {
    if (hkThread != null) {
      PostThreadMessage(hkTid, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
      hkThread.Join(1000);
      hkThread = null;
    }
    return "{\"ok\":true}";
  }

  class Mon { public IntPtr h; public string name; public int w, hgt; public bool primary; }
  static List<Mon> mons = new List<Mon>();
  static List<PM[]> handles = new List<PM[]>();

  public static string List() {
    foreach (var a in handles) DestroyPhysicalMonitors((uint)a.Length, a);
    handles.Clear();
    mons.Clear();

    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hm, IntPtr dc, ref RECT r, IntPtr p) => {
      var mi = new MI();
      mi.cb = Marshal.SizeOf(typeof(MI));
      GetMonitorInfo(hm, ref mi);
      uint n;
      if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out n) || n == 0) return true;
      var a = new PM[n];
      if (!GetPhysicalMonitorsFromHMONITOR(hm, n, a)) return true;
      handles.Add(a);
      foreach (var pm in a) {
        var m = new Mon();
        m.h = pm.h; m.name = pm.d;
        m.w = mi.m.r - mi.m.l; m.hgt = mi.m.b - mi.m.t;
        m.primary = (mi.f & 1) != 0;
        mons.Add(m);
      }
      return true;
    }, IntPtr.Zero);

    var sb = new StringBuilder("[");
    for (int i = 0; i < mons.Count; i++) {
      var m = mons[i];
      if (i > 0) sb.Append(',');
      sb.Append("{\"id\":" + i + ",\"name\":" + Q(m.name) + ",\"width\":" + m.w + ",\"height\":" + m.hgt);
      sb.Append(",\"primary\":" + (m.primary ? "true" : "false") + ",\"features\":{");
      sb.Append("\"brightness\":" + Feat(m.h, 0x10) + ",\"contrast\":" + Feat(m.h, 0x12) + ",\"volume\":" + Feat(m.h, 0x62));
      sb.Append("}}");
    }
    return sb.Append("]").ToString();
  }

  public static string Set(int id, byte code, uint v) {
    if (id < 0 || id >= mons.Count) return "{\"ok\":false,\"error\":\"unknown monitor\"}";
    bool ok = SetVCPFeature(mons[id].h, code, v);
    Thread.Sleep(50); // DDC/CI needs a pause between messages
    return "{\"ok\":" + (ok ? "true" : "false") + "}";
  }

  static string Feat(IntPtr h, byte c) {
    uint cur, max;
    if (GetVCPFeatureAndVCPFeatureReply(h, c, IntPtr.Zero, out cur, out max) && max > 0)
      return "{\"value\":" + cur + ",\"max\":" + max + "}";
    return "null";
  }

  static string Q(string s) {
    var sb = new StringBuilder("\"");
    foreach (char ch in (s ?? "").Trim()) {
      if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
      else if (ch < 32 || ch > 126) sb.Append("\\u" + ((int)ch).ToString("x4"));
      else sb.Append(ch);
    }
    return sb.Append('"').ToString();
  }
}
'@

Add-Type -TypeDefinition $code
[Ddc]::Emit('{"ready":true}')

while ($null -ne ($line = [Console]::In.ReadLine())) {
  $p = $line.Trim().Split(' ')
  try {
    switch ($p[0]) {
      'list' { $out = [Ddc]::List() }
      'set'  { $out = [Ddc]::Set([int]$p[1], [byte]$p[2], [uint32]$p[3]) }
      'hotkeys' { $out = if ($p[1] -eq 'on') { [Ddc]::HotkeysOn() } else { [Ddc]::HotkeysOff() } }
      default { $out = '{"error":"unknown command"}' }
    }
  } catch {
    $out = '{"error":"' + ($_.Exception.Message -replace '[\\"]', "'" -replace '[^\x20-\x7e]', '?') + '"}'
  }
  [Ddc]::Emit($out)
}

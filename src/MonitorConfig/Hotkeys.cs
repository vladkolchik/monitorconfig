namespace MonitorConfig;

/// <summary>Global Ctrl+Alt+PgUp / Ctrl+Alt+PgDn, delivered to a hidden message-only window.</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    public const int Up = 1, Down = 2;
    const uint ModAlt = 0x1, ModControl = 0x2;
    const uint VkPrior = 0x21, VkNext = 0x22;
    const int WmHotkey = 0x0312;

    public event Action<int>? Pressed;
    public bool Registered { get; private set; }

    public Hotkeys()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
    }

    public bool Register()
    {
        if (Registered) return true;
        bool up = Native.RegisterHotKey(Handle, Up, ModControl | ModAlt, VkPrior);
        bool down = Native.RegisterHotKey(Handle, Down, ModControl | ModAlt, VkNext);
        Registered = up && down;
        if (!Registered) Unregister();
        return Registered;
    }

    public void Unregister()
    {
        Native.UnregisterHotKey(Handle, Up);
        Native.UnregisterHotKey(Handle, Down);
        Registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey) Pressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}

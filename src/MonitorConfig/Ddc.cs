using System.Runtime.InteropServices;

namespace MonitorConfig;

public sealed class FeatureState
{
    public uint Value { get; set; }
    public uint Max { get; set; }
}

public sealed class MonitorInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Primary { get; init; }
    public Dictionary<string, FeatureState?> Features { get; init; } = new();
}

/// <summary>
/// DDC/CI access through dxva2.dll. All monitor I/O runs on one dedicated thread, one message at a
/// time — the bus is slow (~50 ms per write). Pending writes to the same control are coalesced, so
/// dragging a slider never builds a backlog.
/// </summary>
public sealed class DdcBus : IDisposable
{
    public static readonly IReadOnlyDictionary<string, byte> Vcp = new Dictionary<string, byte>
    {
        ["brightness"] = 0x10,
        ["contrast"] = 0x12,
        ["volume"] = 0x62,
    };

    sealed class Job
    {
        public string? Key;
        public uint Value;
        public required Func<Job, object?> Run;
        public readonly List<TaskCompletionSource<object?>> Waiters = new();
    }

    readonly LinkedList<Job> queue = new();
    readonly object gate = new();
    readonly Thread thread;
    bool disposed;

    // owned by the bus thread
    readonly List<Native.PHYSICAL_MONITOR[]> handles = new();
    readonly List<IntPtr> physical = new();

    public DdcBus()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "DDC/CI" };
        thread.Start();
    }

    public async Task<List<MonitorInfo>> ListAsync() =>
        (List<MonitorInfo>)(await Enqueue(new Job { Run = _ => Enumerate() }))!;

    public async Task<bool> SetAsync(int id, byte vcp, uint value) =>
        (bool)(await Enqueue(new Job
        {
            Key = $"{id}:{vcp}",
            Value = value,
            Run = j =>
            {
                if (id < 0 || id >= physical.Count) return false;
                bool ok = Native.SetVCPFeature(physical[id], vcp, j.Value);
                Thread.Sleep(50); // DDC/CI needs a pause between messages
                return ok;
            },
        }))!;

    Task<object?> Enqueue(Job job)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DdcBus));
            var pending = job.Key == null ? null : queue.FirstOrDefault(j => j.Key == job.Key);
            if (pending != null)
            {
                pending.Value = job.Value;
                pending.Waiters.Add(tcs);
            }
            else
            {
                job.Waiters.Add(tcs);
                queue.AddLast(job);
                Monitor.Pulse(gate);
            }
        }
        return tcs.Task;
    }

    void Loop()
    {
        while (true)
        {
            Job job;
            lock (gate)
            {
                while (queue.Count == 0)
                {
                    if (disposed) { Release(); return; }
                    Monitor.Wait(gate);
                }
                job = queue.First!.Value;
                queue.RemoveFirst();
            }
            try
            {
                var result = job.Run(job);
                foreach (var w in job.Waiters) w.SetResult(result);
            }
            catch (Exception e)
            {
                foreach (var w in job.Waiters) w.SetException(e);
            }
        }
    }

    List<MonitorInfo> Enumerate()
    {
        Release();
        var result = new List<MonitorInfo>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr p) =>
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(hMon, ref mi);
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) || n == 0) return true;
            var arr = new Native.PHYSICAL_MONITOR[n];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
            handles.Add(arr);
            foreach (var pm in arr)
            {
                physical.Add(pm.hPhysicalMonitor);
                result.Add(new MonitorInfo
                {
                    Id = result.Count,
                    Name = pm.szPhysicalMonitorDescription.Trim(),
                    Width = mi.rcMonitor.right - mi.rcMonitor.left,
                    Height = mi.rcMonitor.bottom - mi.rcMonitor.top,
                    Primary = (mi.dwFlags & 1) != 0,
                    Features = Vcp.ToDictionary(kv => kv.Key, kv => Read(pm.hPhysicalMonitor, kv.Value)),
                });
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    static FeatureState? Read(IntPtr h, byte vcp) =>
        Native.GetVCPFeatureAndVCPFeatureReply(h, vcp, IntPtr.Zero, out uint cur, out uint max) && max > 0
            ? new FeatureState { Value = cur, Max = max }
            : null;

    void Release()
    {
        foreach (var arr in handles) Native.DestroyPhysicalMonitors((uint)arr.Length, arr);
        handles.Clear();
        physical.Clear();
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; Monitor.Pulse(gate); }
        thread.Join(2000);
    }
}

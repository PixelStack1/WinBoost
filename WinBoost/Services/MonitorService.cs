using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WinBoost.Services;

public sealed record MonitorSnapshot(double? Cpu, double? Gpu, double RamUsedGb, double RamTotalGb, double? GpuTemp, string GpuSource, string GpuTempSource)
{
    public double RamPercent => RamTotalGb <= 0 ? 0 : 100.0 * RamUsedGb / RamTotalGb;
}

/// <summary>
/// Read-only hardware monitoring: performance counters, GlobalMemoryStatusEx, nvidia-smi (GPU temperature).
/// No drivers, nothing written. Every source fails independently and reports null ("N/A"). Never throws.
/// </summary>
public static class MonitorService
{
    private static readonly object Gate = new();
    private static PerformanceCounter? _cpu;
    private static bool _cpuInit;
    private static List<PerformanceCounter> _gpu = new();
    private static DateTime _gpuRefresh = DateTime.MinValue;
    private static bool _gpuCountersBroken;

    private static bool? _nvsmiAvailable;
    private static string _nvsmiPath = "nvidia-smi.exe";
    private static (double? Temp, double? Util) _nv;
    private static DateTime _nvAt = DateTime.MinValue;
    private static int _nvBusy;


    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    /// <summary>Call from a background thread (~1 s interval).</summary>
    public static MonitorSnapshot Sample()
    {
        lock (Gate)
        {
            double? cpu = SampleCpu();
            var (used, total) = SampleRam();
            double? gpuCounters = SampleGpuCounters();
            RefreshNvidiaAsync();
            double? gpu = gpuCounters ?? _nv.Util;
            string src = _nvsmiAvailable == true ? "NVIDIA" : "";
            // GPU temperature: nvidia-smi when present, else N/A.
            return new MonitorSnapshot(cpu, gpu, used, total, _nv.Temp, src, _nv.Temp is not null ? "nvidia-smi" : "");
        }
    }

    private static double? SampleCpu()
    {
        try
        {
            if (!_cpuInit)
            {
                _cpuInit = true;
                try { _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true); _cpu.NextValue(); }
                catch { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _cpu.NextValue(); }
                return null;
            }
            return _cpu is null ? null : Math.Clamp(_cpu.NextValue(), 0, 100);
        }
        catch (Exception ex) { if (_cpu is not null) AppLog.Warn("CPU counter failed: " + ex.Message); _cpu = null; return null; }
    }

    private static (double, double) SampleRam()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
            const double GB = 1024d * 1024 * 1024;
            return ((m.ullTotalPhys - m.ullAvailPhys) / GB, m.ullTotalPhys / GB);
        }
        catch { return (0, 0); }
    }

    private static double? SampleGpuCounters()
    {
        if (_gpuCountersBroken) return null;
        try
        {
            if (DateTime.UtcNow - _gpuRefresh > TimeSpan.FromSeconds(10))
            {
                foreach (var c in _gpu) c.Dispose();
                _gpu = new();
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames().Where(i => i.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)))
                {
                    try { var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true); c.NextValue(); _gpu.Add(c); } catch { }
                }
                _gpuRefresh = DateTime.UtcNow;
                return null;
            }
            double sum = 0;
            foreach (var c in _gpu) { try { sum += c.NextValue(); } catch { } }
            return Math.Clamp(sum, 0, 100);
        }
        catch (Exception ex) { _gpuCountersBroken = true; AppLog.Warn("GPU Engine counters unavailable: " + ex.Message); return null; }
    }

    private static void RefreshNvidiaAsync()
    {
        if (_nvsmiAvailable == false || DateTime.UtcNow - _nvAt < TimeSpan.FromSeconds(2)) return;
        if (Interlocked.Exchange(ref _nvBusy, 1) == 1) return;
        _nvAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_nvsmiAvailable is null)
                {
                    string sys = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
                    string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
                    _nvsmiPath = File.Exists(sys) ? sys : File.Exists(pf) ? pf : "nvidia-smi.exe";
                }
                var r = await ProcessRunner.RunAsync(_nvsmiPath, "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits", 5000).ConfigureAwait(false);
                if (r.ExitCode != 0)
                {
                    if (_nvsmiAvailable is null) { _nvsmiAvailable = false; AppLog.Info("nvidia-smi not available; GPU temp N/A"); }
                    return;
                }
                var parts = r.Output.Split('\n')[0].Split(',');
                double? t = parts.Length > 0 && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tv) ? tv : null;
                double? u = parts.Length > 1 && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var uv) ? uv : null;
                _nv = (t, u);
                _nvsmiAvailable = true;
            }
            catch (Exception ex) { _nvsmiAvailable ??= false; AppLog.Warn("nvidia-smi failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _nvBusy, 0); }
        });
    }

}

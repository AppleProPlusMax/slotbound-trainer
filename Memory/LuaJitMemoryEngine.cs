using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SlotboundTrainer.Memory;

public sealed class RosterUnitInfo
{
    public int Index { get; set; }
    public string Symbol { get; set; } = "";
    public string Subclass { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Grade { get; set; } = "";
    public string GradeLabel => Localization.LangManager.GradeLabel(Grade);
    public double Rank { get; set; }
    public double AbsorbLv { get; set; }
    public double MaxImprintSlots { get; set; }
    public double Atk { get; set; }
    public double Hp { get; set; }
    public double Aspd { get; set; }
}

public sealed class ProbabilitySnapshot
{
    public bool Available { get; set; }
    // Rarity probabilities in percentage (0 ~ 100)
    public double NormalPct { get; set; }
    public double AdvancedPct { get; set; }
    public double RarePct { get; set; }
    public double LegendaryPct { get; set; }
    public double PrismPct { get; set; }

    // Slot win probability in percentage (0 ~ 100)
    public double ForceWinPct { get; set; }
    public double WinSuppressPct { get; set; }

    // Symbol weights in percentage (0 ~ 100)
    public double WeightW { get; set; }
    public double WeightA { get; set; }
    public double WeightS { get; set; }
    public double WeightH { get; set; }
    public double WeightM { get; set; }
    public double WeightD { get; set; }
}

public sealed class GameStateSnapshot
{
    public bool Attached { get; set; }
    public string Phase { get; set; } = "-";
    public bool IsInMenu => string.Equals(Phase, "menu", StringComparison.OrdinalIgnoreCase);
    public double Stage { get; set; }
    public double WaveId { get; set; }
    public double Coins { get; set; }
    public double Jungsu { get; set; }
    public double SpinsLeft { get; set; }
    public double FreeSpinCredits { get; set; }
    public double SpinCost { get; set; }
    public double CoreFreeRerolls { get; set; }
    public double BaseHp { get; set; }
    public double OriginiteEarned { get; set; }
    public bool ForceWinOnce { get; set; }
    public int RosterCount { get; set; }
    public int AlliesCount { get; set; }
    public int EnemiesCount { get; set; }
}

/// <summary>
/// Pure external memory reader/writer for Slotbound (LÖVE 11.5 / LuaJIT 2.1 GC64 x64).
/// Safety guarantees:
/// - Thread-safe handle management via internal lock.
/// - Zero DLL injection and zero game file modification.
/// - All writes verify target page is committed private RW memory, verify LuaJIT TValue type tag
///   and NaN-boxing bounds, and clamp numeric values to safe ranges.
/// - Never writes through stale cached Lua table addresses; always resolves live pointers from _G.
/// </summary>
public sealed class LuaJitMemoryEngine : IDisposable
{
    private const ulong TAG_NIL = 0x1FFFFUL;
    private const ulong TAG_FALSE = 0x1FFFEUL;
    private const ulong TAG_TRUE = 0x1FFFDUL;
    private const ulong TAG_STR = 0x1FFFBUL;
    private const ulong TAG_TAB = 0x1FFF4UL;
    private const ulong TAG_MAX_NUM = 0x1FFF2UL;
    private const ulong PTR_MASK = 0x7FFFFFFFFFFFUL;

    private const long MIN_USER_ADDR = 0x10000L;
    private const long MAX_USER_ADDR = 0x7FFFFFFFFFFFL;

    private const ulong TV_FALSE = TAG_FALSE << 47; // 0xFFFF000000000000
    private const ulong TV_TRUE = TAG_TRUE << 47;   // 0xFFFE800000000000

    private readonly object _sync = new();
    private IntPtr _hProcess = IntPtr.Zero;
    private Process? _process;
    private long _globalTableAddr;
    private bool _disposed;

    // Saved original spin cost settings so unchecking "Zero Spin Cost" restores defaults
    private bool _hasOriginalSpinEcon;
    private double _origSpinCostStep = 50;
    private double _origPaidStartCost = 100;
    private double _origPaidCostStep = 50;
    private double _origPaidMaxPerWave = 3;
    private bool _hasOriginalShopReroll;
    private double _origShopRerollCost = 1;
    private Dictionary<string, string>? _unitNameCache;
    private ProbabilitySnapshot? _origProbabilities;

    public bool IsAttached
    {
        get
        {
            lock (_sync)
            {
                return IsAttachedUnsafe();
            }
        }
    }

    public int ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _process?.Id ?? 0;
            }
        }
    }

    public long GlobalTableAddress
    {
        get
        {
            lock (_sync)
            {
                return _globalTableAddr;
            }
        }
    }

    private bool IsAttachedUnsafe()
    {
        if (_disposed || _hProcess == IntPtr.Zero || _globalTableAddr == 0 || _process == null)
            return false;
        try
        {
            return !_process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Detach()
    {
        lock (_sync)
        {
            DetachUnsafe();
        }
    }

    private void DetachUnsafe()
    {
        if (_hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        if (_process != null)
        {
            try { _process.Dispose(); } catch { /* ignore */ }
            _process = null;
        }
        _globalTableAddr = 0;
        _hasOriginalSpinEcon = false;
        _hasOriginalShopReroll = false;
        _unitNameCache = null;
        _origProbabilities = null;
    }

    public bool Attach(out string message)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                message = "修改器正在关闭。";
                return false;
            }

            DetachUnsafe();

            Process[] procs = Process.GetProcessesByName("Slotbound");
            if (procs.Length == 0)
            {
                message = "未找到正在运行的 Slotbound.exe 进程，请先启动游戏。";
                return false;
            }

            Process? target = null;
            foreach (var p in procs)
            {
                if (target == null && HasLua51Module(p))
                {
                    target = p;
                }
                else
                {
                    p.Dispose();
                }
            }

            if (target == null)
            {
                message = "找到同名进程，但未检测到 lua51.dll 模块，请确认目标为正版 Slotbound 游戏进程。";
                return false;
            }

            _process = target;
            _hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_MEM, false, _process.Id);
            if (_hProcess == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                DetachUnsafe();
                message = err == 5
                    ? "权限不足 (ACCESS_DENIED)，请右键以「管理员身份」运行本修改器。"
                    : $"打开游戏进程失败 (Win32 Error {err})。";
                return false;
            }

            long gTab = ScanMainGlobalTableUnsafe();
            if (gTab == 0)
            {
                DetachUnsafe();
                message = "已找到进程，但未定位到 LuaJIT 主全局表（游戏可能尚未加载完成，请稍后重试）。";
                return false;
            }

            _globalTableAddr = gTab;
            message = $"已安全附加到 Slotbound.exe (PID: {_process.Id})，主 _G = 0x{_globalTableAddr:X}";
            return true;
        }
    }

    private static bool HasLua51Module(Process p)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                if (string.Equals(m.ModuleName, "lua51.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Fallback: allow OpenProcess to report exact permission error if module enumeration is blocked
            return true;
        }
        return false;
    }

    public bool VerifyAlive()
    {
        lock (_sync)
        {
            return VerifyAliveUnsafe();
        }
    }

    private bool VerifyAliveUnsafe()
    {
        if (!IsAttachedUnsafe())
        {
            DetachUnsafe();
            return false;
        }
        byte[]? hdr = ReadBytesUnsafe(_globalTableAddr, 16);
        if (hdr == null || hdr[9] != 11)
        {
            DetachUnsafe();
            return false;
        }
        return true;
    }

    private long ScanMainGlobalTableUnsafe()
    {
        byte[] pat = Encoding.ASCII.GetBytes("collectgarbage\0");
        var cgKeySet = new HashSet<ulong>();
        var regions = new List<(long BaseAddr, long Size)>();

        long addr = MIN_USER_ADDR;
        uint mbiSize = (uint)Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION>();

        // Pass 1: enumerate private RW regions and find all GCstr("collectgarbage")
        while (addr < MAX_USER_ADDR &&
               NativeMethods.VirtualQueryEx(_hProcess, (IntPtr)addr, out var mbi, mbiSize) != 0)
        {
            long size = mbi.RegionSize.ToInt64();
            if (mbi.State == NativeMethods.MEM_COMMIT &&
                (mbi.Protect & 0xFF) == NativeMethods.PAGE_READWRITE &&
                mbi.Type == NativeMethods.MEM_PRIVATE &&
                size > 0 && size <= 128L * 1024 * 1024)
            {
                long baseAddr = mbi.BaseAddress.ToInt64();
                regions.Add((baseAddr, size));

                byte[]? buf = ReadBytesUnsafe(baseAddr, (int)size);
                if (buf != null)
                {
                    for (int i = 24; i <= buf.Length - pat.Length; i++)
                    {
                        if (buf[i] == (byte)'c' && buf[i + 1] == (byte)'o' && buf[i + 2] == (byte)'l')
                        {
                            bool ok = true;
                            for (int j = 0; j < pat.Length; j++)
                            {
                                if (buf[i + j] != pat[j]) { ok = false; break; }
                            }
                            if (ok && buf[i - 15] == 4 && BitConverter.ToUInt32(buf, i - 4) == 14)
                            {
                                long gcstrAddr = baseAddr + i - 24;
                                cgKeySet.Add((TAG_STR << 47) | (ulong)gcstrAddr);
                            }
                        }
                    }
                }
            }

            long next = mbi.BaseAddress.ToInt64() + size;
            if (next <= addr) break;
            addr = next;
        }

        if (cgKeySet.Count == 0) return 0;

        // Pass 2: single scan over regions matching ALL collectgarbage TValues at once
        long bestG = 0;
        int bestCount = 0;
        var seen = new HashSet<long>();

        foreach (var reg in regions)
        {
            byte[]? buf = ReadBytesUnsafe(reg.BaseAddr, (int)reg.Size);
            if (buf == null) continue;

            for (int i = 8; i <= buf.Length - 16; i += 8)
            {
                ulong candidate = BitConverter.ToUInt64(buf, i);
                if (!cgKeySet.Contains(candidate)) continue;

                long gTab = 0;
                long s0 = Math.Max(0, i - 8 - 24 * 512);
                long s1 = Math.Min(buf.Length - 24, i - 8 + 24 * 512);
                for (long off = s0; off <= s1; off += 24)
                {
                    ulong kv = BitConverter.ToUInt64(buf, (int)off + 8);
                    if ((kv >> 47) == TAG_STR && ReadGCstrUnsafe((long)(kv & PTR_MASK)) == "_G")
                    {
                        ulong vv = BitConverter.ToUInt64(buf, (int)off);
                        if ((vv >> 47) == TAG_TAB)
                        {
                            long ptr = (long)(vv & PTR_MASK);
                            if (IsValidUserPtr(ptr))
                            {
                                gTab = ptr;
                                break;
                            }
                        }
                    }
                }

                if (gTab != 0 && seen.Add(gTab))
                {
                    var entries = ReadTableEntriesUnsafe(gTab);
                    if (entries.ContainsKey("battlefield") || entries.ContainsKey("coins") || entries.ContainsKey("phase"))
                    {
                        return gTab;
                    }
                    if (entries.Count > bestCount)
                    {
                        bestCount = entries.Count;
                        bestG = gTab;
                    }
                }
            }
        }

        return bestG;
    }

    private static bool IsValidUserPtr(long addr) =>
        addr >= MIN_USER_ADDR && addr <= MAX_USER_ADDR && (addr & 0x3) == 0;

    private bool IsWritablePrivateMemoryUnsafe(long addr, int len)
    {
        if (!IsValidUserPtr(addr) || len <= 0) return false;
        uint mbiSize = (uint)Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION>();
        if (NativeMethods.VirtualQueryEx(_hProcess, (IntPtr)addr, out var mbi, mbiSize) == 0)
            return false;

        long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
        return mbi.State == NativeMethods.MEM_COMMIT &&
               mbi.Type == NativeMethods.MEM_PRIVATE &&
               (mbi.Protect & 0xFF) == NativeMethods.PAGE_READWRITE &&
               addr + len <= regionEnd;
    }

    private byte[]? ReadBytesUnsafe(long addr, int len)
    {
        if (_hProcess == IntPtr.Zero || !IsValidUserPtr(addr) || len <= 0 || len > 128 * 1024 * 1024)
            return null;
        byte[] buf = new byte[len];
        if (!NativeMethods.ReadProcessMemory(_hProcess, (IntPtr)addr, buf, len, out int read) || read != len)
            return null;
        return buf;
    }

    private bool WriteU64SafeUnsafe(long addr, ulong value)
    {
        if (_hProcess == IntPtr.Zero || !IsWritablePrivateMemoryUnsafe(addr, 8))
            return false;
        byte[] bytes = BitConverter.GetBytes(value);
        return NativeMethods.WriteProcessMemory(_hProcess, (IntPtr)addr, bytes, 8, out int written) && written == 8;
    }

    private string? ReadGCstrUnsafe(long gcstrAddr)
    {
        if (!IsValidUserPtr(gcstrAddr)) return null;
        byte[]? hdr = ReadBytesUnsafe(gcstrAddr, 24);
        if (hdr == null || hdr[9] != 4) return null;
        uint len = BitConverter.ToUInt32(hdr, 20);
        if (len > 512) return null;
        byte[]? data = ReadBytesUnsafe(gcstrAddr + 24, (int)len);
        return data == null ? null : Encoding.UTF8.GetString(data);
    }

    private Dictionary<string, (long ValAddr, ulong TVal)> ReadTableEntriesUnsafe(long tabAddr)
    {
        var result = new Dictionary<string, (long, ulong)>();
        if (!IsValidUserPtr(tabAddr)) return result;
        byte[]? hdr = ReadBytesUnsafe(tabAddr, 64);
        if (hdr == null || hdr[9] != 11) return result;

        long nodePtr = BitConverter.ToInt64(hdr, 0x28);
        uint hmask = BitConverter.ToUInt32(hdr, 0x34);
        if (!IsValidUserPtr(nodePtr) || hmask > 65536) return result;

        int count = (int)hmask + 1;
        byte[]? nodes = ReadBytesUnsafe(nodePtr, count * 24);
        if (nodes == null) return result;

        for (int i = 0; i < count; i++)
        {
            ulong val = BitConverter.ToUInt64(nodes, i * 24);
            ulong key = BitConverter.ToUInt64(nodes, i * 24 + 8);
            if ((val >> 47) == TAG_NIL) continue;
            if ((key >> 47) == TAG_STR)
            {
                string? k = ReadGCstrUnsafe((long)(key & PTR_MASK));
                if (k != null && !result.ContainsKey(k))
                {
                    result[k] = (nodePtr + i * 24L, val);
                }
            }
        }
        return result;
    }

    private List<long> ReadTableArrayTablesUnsafe(long tabAddr)
    {
        var list = new List<long>();
        if (!IsValidUserPtr(tabAddr)) return list;
        byte[]? hdr = ReadBytesUnsafe(tabAddr, 64);
        if (hdr == null || hdr[9] != 11) return list;

        long arrPtr = BitConverter.ToInt64(hdr, 0x10);
        uint asize = BitConverter.ToUInt32(hdr, 0x30);
        if (IsValidUserPtr(arrPtr) && asize > 1 && asize < 4096)
        {
            byte[]? arr = ReadBytesUnsafe(arrPtr, (int)asize * 8);
            if (arr != null)
            {
                for (int i = 1; i < asize; i++)
                {
                    ulong v = BitConverter.ToUInt64(arr, i * 8);
                    if ((v >> 47) == TAG_TAB)
                    {
                        long ptr = (long)(v & PTR_MASK);
                        if (IsValidUserPtr(ptr)) list.Add(ptr);
                    }
                }
            }
        }
        return list;
    }

    private bool TryFindFieldUnsafe(long tabAddr, string key, out long valAddr, out ulong tval)
    {
        valAddr = 0;
        tval = 0;
        if (!IsValidUserPtr(tabAddr)) return false;
        byte[]? hdr = ReadBytesUnsafe(tabAddr, 64);
        if (hdr == null || hdr[9] != 11) return false;

        long nodePtr = BitConverter.ToInt64(hdr, 0x28);
        uint hmask = BitConverter.ToUInt32(hdr, 0x34);
        if (!IsValidUserPtr(nodePtr) || hmask > 65536) return false;

        int count = (int)hmask + 1;
        byte[]? nodes = ReadBytesUnsafe(nodePtr, count * 24);
        if (nodes == null) return false;

        for (int i = 0; i < count; i++)
        {
            ulong k = BitConverter.ToUInt64(nodes, i * 24 + 8);
            if ((k >> 47) != TAG_STR) continue;
            string? ks = ReadGCstrUnsafe((long)(k & PTR_MASK));
            if (ks == key)
            {
                valAddr = nodePtr + i * 24L;
                tval = BitConverter.ToUInt64(nodes, i * 24);
                return (tval >> 47) != TAG_NIL;
            }
        }
        return false;
    }

    private long ResolveTablePathUnsafe(params string[] path)
    {
        long cur = _globalTableAddr;
        foreach (string seg in path)
        {
            if (!TryFindFieldUnsafe(cur, seg, out _, out ulong tv)) return 0;
            if ((tv >> 47) != TAG_TAB) return 0;
            long next = (long)(tv & PTR_MASK);
            if (!IsValidUserPtr(next)) return 0;
            cur = next;
        }
        return cur;
    }

    private static bool IsNumber(ulong tv) => (tv >> 47) <= TAG_MAX_NUM;
    private static double ToDouble(ulong tv) => BitConverter.Int64BitsToDouble((long)tv);
    private static ulong FromDouble(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);

    private static bool TryExtractNumber(Dictionary<string, (long ValAddr, ulong TVal)> map, string key, out double val)
    {
        val = 0;
        if (!map.TryGetValue(key, out var entry) || !IsNumber(entry.TVal)) return false;
        val = ToDouble(entry.TVal);
        return true;
    }

    private bool TryReadNumberUnsafe(long tabAddr, string key, out double value)
    {
        value = 0;
        if (tabAddr == 0 || !TryFindFieldUnsafe(tabAddr, key, out _, out ulong tv)) return false;
        if (!IsNumber(tv)) return false;
        value = ToDouble(tv);
        return true;
    }

    private bool TryWriteNumberUnsafe(long tabAddr, string key, double value, double minVal, double maxVal)
    {
        if (tabAddr == 0 || double.IsNaN(value) || double.IsInfinity(value)) return false;
        double clamped = Math.Clamp(value, minVal, maxVal);
        ulong bits = FromDouble(clamped);
        if (!IsNumber(bits)) return false; // Hard NaN-boxing guard

        if (!TryFindFieldUnsafe(tabAddr, key, out long valAddr, out ulong cur)) return false;
        if (!IsNumber(cur)) return false; // GC safety: only overwrite existing numeric slots
        return WriteU64SafeUnsafe(valAddr, bits);
    }

    private bool TryReadBoolUnsafe(long tabAddr, string key, out bool value)
    {
        value = false;
        if (tabAddr == 0 || !TryFindFieldUnsafe(tabAddr, key, out _, out ulong tv)) return false;
        ulong tag = tv >> 47;
        if (tag == TAG_TRUE) { value = true; return true; }
        if (tag == TAG_FALSE) { value = false; return true; }
        return false;
    }

    private bool TryWriteBoolUnsafe(long tabAddr, string key, bool value)
    {
        if (tabAddr == 0) return false;
        if (!TryFindFieldUnsafe(tabAddr, key, out long valAddr, out ulong cur)) return false;
        ulong tag = cur >> 47;
        if (tag != TAG_TRUE && tag != TAG_FALSE) return false; // GC safety: only overwrite existing bool slots
        return WriteU64SafeUnsafe(valAddr, value ? TV_TRUE : TV_FALSE);
    }

    private string ReadStringFieldUnsafe(long tabAddr, string key, string fallback = "-")
    {
        if (tabAddr == 0 || !TryFindFieldUnsafe(tabAddr, key, out _, out ulong tv)) return fallback;
        if ((tv >> 47) != TAG_STR) return fallback;
        return ReadGCstrUnsafe((long)(tv & PTR_MASK)) ?? fallback;
    }

    // ==================== High-Level Thread-Safe Game API ====================

    public GameStateSnapshot ReadSnapshot()
    {
        lock (_sync)
        {
            var snap = new GameStateSnapshot { Attached = VerifyAliveUnsafe() };
            if (!snap.Attached) return snap;

            // Single bulk read of _G to minimize ReadProcessMemory calls
            var g = ReadTableEntriesUnsafe(_globalTableAddr);

            if (g.TryGetValue("phase", out var phEntry) && (phEntry.TVal >> 47) == TAG_STR)
                snap.Phase = ReadGCstrUnsafe((long)(phEntry.TVal & PTR_MASK)) ?? "-";

            if (TryExtractNumber(g, "CurrentStage", out double stage)) snap.Stage = stage;
            if (TryExtractNumber(g, "coins", out double coins)) snap.Coins = coins;
            if (TryExtractNumber(g, "jungsu", out double jungsu)) snap.Jungsu = jungsu;
            if (TryExtractNumber(g, "spinsLeft", out double spinsLeft)) snap.SpinsLeft = spinsLeft;
            if (TryExtractNumber(g, "freeSpinCredits", out double freeSpins)) snap.FreeSpinCredits = freeSpins;
            if (TryExtractNumber(g, "spinCost", out double spinCost)) snap.SpinCost = spinCost;
            if (TryExtractNumber(g, "coreFreeRerollsRemaining", out double rerolls)) snap.CoreFreeRerolls = rerolls;

            if (g.TryGetValue("battlefield", out var bfEntry) && (bfEntry.TVal >> 47) == TAG_TAB)
            {
                long bfAddr = (long)(bfEntry.TVal & PTR_MASK);
                var bf = ReadTableEntriesUnsafe(bfAddr);
                if (TryExtractNumber(bf, "baseHP", out double baseHp)) snap.BaseHp = baseHp;
                if (TryExtractNumber(bf, "originiteEarnedThisRun", out double orig)) snap.OriginiteEarned = orig;

                if (bf.TryGetValue("wave", out var wEntry) && (wEntry.TVal >> 47) == TAG_TAB)
                {
                    if (TryReadNumberUnsafe((long)(wEntry.TVal & PTR_MASK), "id", out double wid))
                        snap.WaveId = wid;
                }
                if (bf.TryGetValue("rosterUnits", out var rEntry) && (rEntry.TVal >> 47) == TAG_TAB)
                    snap.RosterCount = ReadTableArrayTablesUnsafe((long)(rEntry.TVal & PTR_MASK)).Count;
                if (bf.TryGetValue("allies", out var aEntry) && (aEntry.TVal >> 47) == TAG_TAB)
                    snap.AlliesCount = ReadTableArrayTablesUnsafe((long)(aEntry.TVal & PTR_MASK)).Count;
                if (bf.TryGetValue("enemies", out var eEntry) && (eEntry.TVal >> 47) == TAG_TAB)
                    snap.EnemiesCount = ReadTableArrayTablesUnsafe((long)(eEntry.TVal & PTR_MASK)).Count;
            }

            if (g.TryGetValue("slot", out var sEntry) && (sEntry.TVal >> 47) == TAG_TAB)
            {
                if (TryReadBoolUnsafe((long)(sEntry.TVal & PTR_MASK), "forceWinOnce", out bool fw))
                    snap.ForceWinOnce = fw;
            }

            return snap;
        }
    }

    public bool SetCoins(double value)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            bool ok = TryWriteNumberUnsafe(_globalTableAddr, "coins", value, 0, 1e9);
            TryWriteNumberUnsafe(_globalTableAddr, "displayCoins", value, 0, 1e9);
            long ui = ResolveTablePathUnsafe("ui");
            if (ui != 0)
            {
                TryWriteNumberUnsafe(ui, "goldBalance", value, 0, 1e9);
                TryWriteNumberUnsafe(ui, "coinsBalanceTarget", value, 0, 1e9);
            }
            return ok;
        }
    }

    public bool SetJungsu(double value)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            bool ok = TryWriteNumberUnsafe(_globalTableAddr, "jungsu", value, 0, 1e8);
            TryWriteNumberUnsafe(_globalTableAddr, "displayJungsu", value, 0, 1e8);
            long ui = ResolveTablePathUnsafe("ui");
            if (ui != 0)
            {
                TryWriteNumberUnsafe(ui, "jungsuBalance", value, 0, 1e8);
                TryWriteNumberUnsafe(ui, "jungsuBalanceTarget", value, 0, 1e8);
            }
            return ok;
        }
    }

    public bool SetSpinsLeft(double value)
    {
        lock (_sync)
        {
            return VerifyAliveUnsafe() && TryWriteNumberUnsafe(_globalTableAddr, "spinsLeft", value, 0, 99999);
        }
    }

    public bool SetFreeSpinCredits(double value)
    {
        lock (_sync)
        {
            return VerifyAliveUnsafe() && TryWriteNumberUnsafe(_globalTableAddr, "freeSpinCredits", value, 0, 99999);
        }
    }

    public bool SetCoreFreeRerolls(double value)
    {
        lock (_sync)
        {
            return VerifyAliveUnsafe() && TryWriteNumberUnsafe(_globalTableAddr, "coreFreeRerollsRemaining", value, 0, 99999);
        }
    }

    public bool SetZeroSpinCost()
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;

            long econSpins = ResolveTablePathUnsafe("econ", "spins");
            if (!_hasOriginalSpinEcon)
            {
                if (TryReadNumberUnsafe(_globalTableAddr, "spinCostStep", out double step) && step > 0)
                    _origSpinCostStep = step;
                if (econSpins != 0)
                {
                    if (TryReadNumberUnsafe(econSpins, "paidStartCost", out double start) && start > 0)
                        _origPaidStartCost = start;
                    if (TryReadNumberUnsafe(econSpins, "paidCostStep", out double pstep) && pstep > 0)
                        _origPaidCostStep = pstep;
                    if (TryReadNumberUnsafe(econSpins, "paidMaxPerWave", out double pmax) && pmax > 0)
                        _origPaidMaxPerWave = pmax;
                }
                _hasOriginalSpinEcon = true;
            }

            bool ok = TryWriteNumberUnsafe(_globalTableAddr, "spinCost", 0, 0, 1e8);
            TryWriteNumberUnsafe(_globalTableAddr, "spinCostStep", 0, 0, 1e8);
            TryWriteNumberUnsafe(_globalTableAddr, "paidSpinsInWave", 0, 0, 9999);
            if (econSpins != 0)
            {
                TryWriteNumberUnsafe(econSpins, "paidStartCost", 0, 0, 1e8);
                TryWriteNumberUnsafe(econSpins, "paidCostStep", 0, 0, 1e8);
                TryWriteNumberUnsafe(econSpins, "paidMaxPerWave", 999, 1, 9999);
            }
            return ok;
        }
    }

    public bool RestoreSpinCostDefaults()
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe() || !_hasOriginalSpinEcon) return false;
            bool ok = TryWriteNumberUnsafe(_globalTableAddr, "spinCostStep", _origSpinCostStep, 0, 1e8);
            long econSpins = ResolveTablePathUnsafe("econ", "spins");
            if (econSpins != 0)
            {
                TryWriteNumberUnsafe(econSpins, "paidStartCost", _origPaidStartCost, 0, 1e8);
                TryWriteNumberUnsafe(econSpins, "paidCostStep", _origPaidCostStep, 0, 1e8);
                TryWriteNumberUnsafe(econSpins, "paidMaxPerWave", _origPaidMaxPerWave, 1, 9999);
            }
            return ok;
        }
    }

    public bool SetFreeShopReroll()
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            long ui = ResolveTablePathUnsafe("ui");
            if (ui == 0) return false;
            if (!_hasOriginalShopReroll)
            {
                if (TryReadNumberUnsafe(ui, "_itemRerollCost", out double cost) && cost > 0)
                    _origShopRerollCost = cost;
                _hasOriginalShopReroll = true;
            }
            return TryWriteNumberUnsafe(ui, "_itemRerollCost", 0, 0, 1e6);
        }
    }

    public bool RestoreShopRerollCost()
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe() || !_hasOriginalShopReroll) return false;
            long ui = ResolveTablePathUnsafe("ui");
            return ui != 0 && TryWriteNumberUnsafe(ui, "_itemRerollCost", _origShopRerollCost, 0, 1e6);
        }
    }

    public bool SetBaseHp(double value)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            long bf = ResolveTablePathUnsafe("battlefield");
            if (bf == 0) return false;
            bool ok = TryWriteNumberUnsafe(bf, "baseHP", value, 1, 999999);
            TryWriteNumberUnsafe(bf, "_prevBaseHP", value, 1, 999999);
            TryWriteNumberUnsafe(bf, "_waveStartBaseHP", value, 1, 999999);
            return ok;
        }
    }

    public bool SetOriginiteEarned(double value)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            long bf = ResolveTablePathUnsafe("battlefield");
            if (bf == 0) return false;
            bool ok = TryWriteNumberUnsafe(bf, "originiteEarnedThisRun", value, 0, 1e8);
            TryWriteNumberUnsafe(bf, "originitePending", value, 0, 1e8);
            return ok;
        }
    }

    public bool SetForceWinOnce(bool enabled)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            long slot = ResolveTablePathUnsafe("slot");
            return slot != 0 && TryWriteBoolUnsafe(slot, "forceWinOnce", enabled);
        }
    }

    public int HealAlliesFull()
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return 0;
            long allies = ResolveTablePathUnsafe("battlefield", "allies");
            if (allies == 0) return 0;

            int healed = 0;
            foreach (long unitAddr in ReadTableArrayTablesUnsafe(allies))
            {
                if (TryReadBoolUnsafe(unitAddr, "alive", out bool alive) && !alive) continue;
                if (TryReadBoolUnsafe(unitAddr, "dying", out bool dying) && dying) continue;
                if (TryReadNumberUnsafe(unitAddr, "maxhp", out double maxhp) && maxhp > 0)
                {
                    if (TryWriteNumberUnsafe(unitAddr, "hp", maxhp, 1, 1e8)) healed++;
                }
            }
            return healed;
        }
    }

    public int WeakenAllEnemies(double targetHp = 1.0)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return 0;
            long enemies = ResolveTablePathUnsafe("battlefield", "enemies");
            if (enemies == 0) return 0;

            double safeTarget = Math.Clamp(targetHp, 1.0, 1e8);
            int count = 0;
            foreach (long enemyAddr in ReadTableArrayTablesUnsafe(enemies))
            {
                if (TryReadBoolUnsafe(enemyAddr, "dying", out bool dying) && dying) continue;
                if (TryReadNumberUnsafe(enemyAddr, "hp", out double hp) && hp > safeTarget)
                {
                    if (TryWriteNumberUnsafe(enemyAddr, "hp", safeTarget, 1.0, 1e8)) count++;
                }
            }
            return count;
        }
    }

    public List<RosterUnitInfo> GetRosterUnits()
    {
        lock (_sync)
        {
            var list = new List<RosterUnitInfo>();
            if (!VerifyAliveUnsafe()) return list;

            long roster = ResolveTablePathUnsafe("battlefield", "rosterUnits");
            if (roster == 0) return list;

            var tables = ReadTableArrayTablesUnsafe(roster);
            for (int i = 0; i < tables.Count; i++)
            {
                long uAddr = tables[i];
                var info = new RosterUnitInfo
                {
                    Index = i + 1,
                    Symbol = ReadStringFieldUnsafe(uAddr, "sym", "?"),
                    Subclass = ReadStringFieldUnsafe(uAddr, "subclassName", ""),
                    Grade = ReadStringFieldUnsafe(uAddr, "grade", "")
                };
                if (string.IsNullOrEmpty(info.Subclass))
                    info.Subclass = ReadStringFieldUnsafe(uAddr, "subclass", info.Symbol);
                info.DisplayName = ResolveUnitDisplayNameUnsafe(info.Subclass);

                if (TryReadNumberUnsafe(uAddr, "rank", out double rank)) info.Rank = rank;
                if (TryReadNumberUnsafe(uAddr, "absorbLV", out double alv)) info.AbsorbLv = alv;
                if (TryReadNumberUnsafe(uAddr, "maxImprintSlots", out double slots)) info.MaxImprintSlots = slots;

                if (TryFindFieldUnsafe(uAddr, "stats", out _, out ulong statsTv) && (statsTv >> 47) == TAG_TAB)
                {
                    long statsAddr = (long)(statsTv & PTR_MASK);
                    if (TryReadNumberUnsafe(statsAddr, "atk", out double atk)) info.Atk = atk;
                    if (TryReadNumberUnsafe(statsAddr, "hp", out double hp)) info.Hp = hp;
                    if (TryReadNumberUnsafe(statsAddr, "aspd", out double aspd)) info.Aspd = aspd;
                }

                list.Add(info);
            }
            return list;
        }
    }

    /// <summary>
    /// Safely updates a roster unit by re-resolving its live table address from _G -> battlefield.rosterUnits
    /// and verifying its identity (Symbol + Subclass) before writing. Never writes to stale cached pointers.
    /// </summary>
    public bool UpdateRosterUnitByIdentity(
        int oneBasedIndex, string expectedSymbol, string expectedSubclass,
        double? newAtk, double? newHp, double? newAspd, double? newAbsorbLv, double? newImprintSlots)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe() || oneBasedIndex < 1) return false;

            long roster = ResolveTablePathUnsafe("battlefield", "rosterUnits");
            if (roster == 0) return false;

            var tables = ReadTableArrayTablesUnsafe(roster);
            if (oneBasedIndex > tables.Count) return false;

            long uAddr = tables[oneBasedIndex - 1];
            string curSym = ReadStringFieldUnsafe(uAddr, "sym", "?");
            string curSub = ReadStringFieldUnsafe(uAddr, "subclassName", "");
            if (string.IsNullOrEmpty(curSub)) curSub = ReadStringFieldUnsafe(uAddr, "subclass", curSym);

            if (curSym != expectedSymbol || curSub != expectedSubclass)
                return false; // Unit changed or reordered since list was refreshed; abort safely

            return WriteUnitFieldsUnsafe(uAddr, oneBasedIndex, newAtk, newHp, newAspd, newAbsorbLv, newImprintSlots);
        }
    }

    public int BoostAllRosterUnits(double atkMul, double hpMul, double aspdMul)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return 0;
            long roster = ResolveTablePathUnsafe("battlefield", "rosterUnits");
            if (roster == 0) return 0;

            int ok = 0;
            var tables = ReadTableArrayTablesUnsafe(roster);
            for (int i = 0; i < tables.Count; i++)
            {
                long uAddr = tables[i];
                if (!TryFindFieldUnsafe(uAddr, "stats", out _, out ulong statsTv) || (statsTv >> 47) != TAG_TAB)
                    continue;
                long statsAddr = (long)(statsTv & PTR_MASK);

                double? newAtk = null, newHp = null, newAspd = null;
                if (atkMul > 0 && TryReadNumberUnsafe(statsAddr, "atk", out double curAtk) && curAtk > 0)
                {
                    newAtk = Math.Round(curAtk * atkMul, 2);
                    if (TryReadNumberUnsafe(uAddr, "_randomOptionAtkMul", out double m) && m > 0)
                        TryWriteNumberUnsafe(uAddr, "_randomOptionAtkMul", m * atkMul, 0.1, 10000);
                }
                if (hpMul > 0 && TryReadNumberUnsafe(statsAddr, "hp", out double curHp) && curHp > 0)
                {
                    newHp = Math.Round(curHp * hpMul, 2);
                    if (TryReadNumberUnsafe(uAddr, "_randomOptionHpMul", out double m) && m > 0)
                        TryWriteNumberUnsafe(uAddr, "_randomOptionHpMul", m * hpMul, 0.1, 10000);
                }
                if (aspdMul > 0 && TryReadNumberUnsafe(statsAddr, "aspd", out double curAspd) && curAspd > 0)
                {
                    newAspd = Math.Round(curAspd * aspdMul, 3);
                    if (TryReadNumberUnsafe(uAddr, "_randomOptionAspdMul", out double m) && m > 0)
                        TryWriteNumberUnsafe(uAddr, "_randomOptionAspdMul", m * aspdMul, 0.1, 100);
                }

                if (WriteUnitFieldsUnsafe(uAddr, i + 1, newAtk, newHp, newAspd, null, null))
                    ok++;
            }
            return ok;
        }
    }

    private bool WriteUnitFieldsUnsafe(
        long uAddr, int rosterIndex, double? newAtk, double? newHp, double? newAspd, double? newAbsorbLv, double? newImprintSlots)
    {
        long statsAddr = 0;
        if (TryFindFieldUnsafe(uAddr, "stats", out _, out ulong statsTv) && (statsTv >> 47) == TAG_TAB)
            statsAddr = (long)(statsTv & PTR_MASK);

        long allyRefAddr = 0;
        if (TryFindFieldUnsafe(uAddr, "allyRef", out _, out ulong allyTv) && (allyTv >> 47) == TAG_TAB)
            allyRefAddr = (long)(allyTv & PTR_MASK);

        // Also find live battlefield allies matching this rosterIndex so active battle updates immediately
        var liveAllies = new List<long>();
        if (allyRefAddr != 0) liveAllies.Add(allyRefAddr);
        long alliesTab = ResolveTablePathUnsafe("battlefield", "allies");
        if (alliesTab != 0)
        {
            foreach (long aAddr in ReadTableArrayTablesUnsafe(alliesTab))
            {
                if (aAddr != allyRefAddr &&
                    TryReadNumberUnsafe(aAddr, "rosterIndex", out double rIdx) &&
                    (int)Math.Round(rIdx) == rosterIndex)
                {
                    liveAllies.Add(aAddr);
                }
            }
        }

        bool any = false;

        if (newAtk.HasValue && newAtk.Value > 0)
        {
            double atk = Math.Clamp(newAtk.Value, 1, 1e8);
            if (statsAddr != 0) any |= TryWriteNumberUnsafe(statsAddr, "atk", atk, 1, 1e8);
            foreach (long a in liveAllies) any |= TryWriteNumberUnsafe(a, "dmg", atk, 1, 1e8);
        }
        if (newHp.HasValue && newHp.Value > 0)
        {
            double hp = Math.Clamp(newHp.Value, 1, 1e8);
            if (statsAddr != 0) any |= TryWriteNumberUnsafe(statsAddr, "hp", hp, 1, 1e8);
            foreach (long a in liveAllies)
            {
                any |= TryWriteNumberUnsafe(a, "maxhp", hp, 1, 1e8);
                TryWriteNumberUnsafe(a, "hp", hp, 1, 1e8);
            }
        }
        if (newAspd.HasValue && newAspd.Value > 0)
        {
            double aspd = Math.Clamp(newAspd.Value, 0.05, 50.0);
            if (statsAddr != 0) any |= TryWriteNumberUnsafe(statsAddr, "aspd", aspd, 0.05, 50.0);
            foreach (long a in liveAllies) TryWriteNumberUnsafe(a, "atkPeriod", 1.0 / aspd, 0.02, 20.0);
        }
        if (newAbsorbLv.HasValue && newAbsorbLv.Value >= 0)
        {
            double alv = Math.Clamp(newAbsorbLv.Value, 0, 9999);
            any |= TryWriteNumberUnsafe(uAddr, "absorbLV", alv, 0, 9999);
            foreach (long a in liveAllies) TryWriteNumberUnsafe(a, "absorbLV", alv, 0, 9999);
        }
        if (newImprintSlots.HasValue && newImprintSlots.Value >= 0)
        {
            double slots = Math.Clamp(newImprintSlots.Value, 0, 99);
            any |= TryWriteNumberUnsafe(uAddr, "maxImprintSlots", slots, 0, 99);
            foreach (long a in liveAllies) TryWriteNumberUnsafe(a, "maxImprintSlots", slots, 0, 99);
        }

        return any;
    }

    private string ResolveUnitDisplayNameUnsafe(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;
        if (_unitNameCache == null)
        {
            _unitNameCache = new Dictionary<string, string>();
            try
            {
                long loaded = ResolveTablePathUnsafe("package", "loaded");
                if (loaded != 0 && TryFindFieldUnsafe(loaded, "src.data.language", out _, out ulong langTv) && (langTv >> 47) == TAG_TAB)
                {
                    long langAddr = (long)(langTv & PTR_MASK);
                    var zhMap = new Dictionary<string, string>();
                    if (TryFindFieldUnsafe(langAddr, "zh", out _, out ulong zhTv) && (zhTv >> 47) == TAG_TAB)
                    {
                        foreach (var kv in ReadTableEntriesUnsafe((long)(zhTv & PTR_MASK)))
                        {
                            if ((kv.Value.TVal >> 47) == TAG_STR)
                            {
                                string? s = ReadGCstrUnsafe((long)(kv.Value.TVal & PTR_MASK));
                                if (!string.IsNullOrEmpty(s)) zhMap[kv.Key] = s;
                            }
                        }
                    }

                    if (TryFindFieldUnsafe(langAddr, "unitNameMap", out _, out ulong unmTv) && (unmTv >> 47) == TAG_TAB)
                    {
                        foreach (var kv in ReadTableEntriesUnsafe((long)(unmTv & PTR_MASK)))
                        {
                            if ((kv.Value.TVal >> 47) == TAG_STR)
                            {
                                string? en = ReadGCstrUnsafe((long)(kv.Value.TVal & PTR_MASK));
                                if (string.IsNullOrEmpty(en)) continue;
                                if (zhMap.TryGetValue("unit.name." + en, out string? zhName) ||
                                    zhMap.TryGetValue("unit_name_" + en, out zhName) ||
                                    zhMap.TryGetValue(en, out zhName))
                                {
                                    _unitNameCache[kv.Key] = zhName;
                                }
                                else
                                {
                                    _unitNameCache[kv.Key] = en;
                                }
                            }
                        }
                    }
                }
            }
            catch { /* fallback to raw name */ }
        }

        return _unitNameCache.TryGetValue(rawName, out string? mapped) ? mapped : rawName;
    }

    private long ResolveConstantsTableUnsafe()
    {
        long loaded = ResolveTablePathUnsafe("package", "loaded");
        if (loaded == 0) return 0;
        if (!TryFindFieldUnsafe(loaded, "src.data.constants", out _, out ulong tv) || (tv >> 47) != TAG_TAB)
            return 0;
        long ptr = (long)(tv & PTR_MASK);
        return IsValidUserPtr(ptr) ? ptr : 0;
    }

    private long ResolveSubTableUnsafe(long parentTab, params string[] keys)
    {
        long cur = parentTab;
        foreach (string k in keys)
        {
            if (cur == 0 || !TryFindFieldUnsafe(cur, k, out _, out ulong tv) || (tv >> 47) != TAG_TAB)
                return 0;
            long next = (long)(tv & PTR_MASK);
            if (!IsValidUserPtr(next)) return 0;
            cur = next;
        }
        return cur;
    }

    public ProbabilitySnapshot ReadProbabilities()
    {
        lock (_sync)
        {
            return ReadProbabilitiesUnsafe();
        }
    }

    private ProbabilitySnapshot ReadProbabilitiesUnsafe()
    {
        var snap = new ProbabilitySnapshot();
        if (!VerifyAliveUnsafe()) return snap;

        long constants = ResolveConstantsTableUnsafe();
        if (constants == 0) return snap;

        long probs = ResolveSubTableUnsafe(constants, "unitGrades", "probabilities");
        if (probs != 0)
        {
            if (TryReadNumberUnsafe(probs, "normal", out double n)) snap.NormalPct = Math.Round(n * 100.0, 4);
            if (TryReadNumberUnsafe(probs, "advanced", out double a)) snap.AdvancedPct = Math.Round(a * 100.0, 4);
            if (TryReadNumberUnsafe(probs, "rare", out double r)) snap.RarePct = Math.Round(r * 100.0, 4);
            if (TryReadNumberUnsafe(probs, "legendary", out double l)) snap.LegendaryPct = Math.Round(l * 100.0, 4);
            if (TryReadNumberUnsafe(probs, "prism", out double p)) snap.PrismPct = Math.Round(p * 100.0, 4);
            snap.Available = true;
        }

        long slotConst = ResolveSubTableUnsafe(constants, "slot");
        if (slotConst != 0)
        {
            if (TryReadNumberUnsafe(slotConst, "forceWinProb", out double fw)) snap.ForceWinPct = Math.Round(fw * 100.0, 4);
            if (TryReadNumberUnsafe(slotConst, "winSuppressProb", out double ws)) snap.WinSuppressPct = Math.Round(ws * 100.0, 4);

            long symW = ResolveSubTableUnsafe(slotConst, "symbolWeights");
            if (symW != 0)
            {
                if (TryReadNumberUnsafe(symW, "W", out double w)) snap.WeightW = Math.Round(w * 100.0, 4);
                if (TryReadNumberUnsafe(symW, "A", out double a)) snap.WeightA = Math.Round(a * 100.0, 4);
                if (TryReadNumberUnsafe(symW, "S", out double s)) snap.WeightS = Math.Round(s * 100.0, 4);
                if (TryReadNumberUnsafe(symW, "H", out double h)) snap.WeightH = Math.Round(h * 100.0, 4);
                if (TryReadNumberUnsafe(symW, "M", out double m)) snap.WeightM = Math.Round(m * 100.0, 4);
                if (TryReadNumberUnsafe(symW, "D", out double d)) snap.WeightD = Math.Round(d * 100.0, 4);
            }
        }

        if (snap.Available && _origProbabilities == null)
        {
            _origProbabilities = new ProbabilitySnapshot
            {
                Available = true,
                NormalPct = snap.NormalPct,
                AdvancedPct = snap.AdvancedPct,
                RarePct = snap.RarePct,
                LegendaryPct = snap.LegendaryPct,
                PrismPct = snap.PrismPct,
                ForceWinPct = snap.ForceWinPct,
                WinSuppressPct = snap.WinSuppressPct,
                WeightW = snap.WeightW,
                WeightA = snap.WeightA,
                WeightS = snap.WeightS,
                WeightH = snap.WeightH,
                WeightM = snap.WeightM,
                WeightD = snap.WeightD
            };
        }

        return snap;
    }

    public bool SetRarityProbabilities(double normalPct, double advancedPct, double rarePct, double legendaryPct, double prismPct)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            double n = Math.Clamp(normalPct, 0, 100);
            double a = Math.Clamp(advancedPct, 0, 100);
            double r = Math.Clamp(rarePct, 0, 100);
            double l = Math.Clamp(legendaryPct, 0, 100);
            double pr = Math.Clamp(prismPct, 0, 100);
            if (n + a + r + l + pr <= 0) return false; // Guard against zero total probability

            if (_origProbabilities == null) ReadProbabilitiesUnsafe();

            long constants = ResolveConstantsTableUnsafe();
            long probs = ResolveSubTableUnsafe(constants, "unitGrades", "probabilities");
            if (probs == 0) return false;

            bool ok = true;
            ok &= TryWriteNumberUnsafe(probs, "normal", n / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(probs, "advanced", a / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(probs, "rare", r / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(probs, "legendary", l / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(probs, "prism", pr / 100.0, 0, 1);
            return ok;
        }
    }

    public bool RestoreDefaultRarityProbabilities()
    {
        var orig = _origProbabilities;
        if (orig != null)
            return SetRarityProbabilities(orig.NormalPct, orig.AdvancedPct, orig.RarePct, orig.LegendaryPct, orig.PrismPct);
        return SetRarityProbabilities(80.3, 15.0, 4.5, 0.2, 0.0);
    }

    public bool SetSlotWinProbability(double forceWinPct, double suppressPct = 0.0)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            long constants = ResolveConstantsTableUnsafe();
            long slotConst = ResolveSubTableUnsafe(constants, "slot");
            if (slotConst == 0) return false;

            bool ok = TryWriteNumberUnsafe(slotConst, "forceWinProb", Math.Clamp(forceWinPct, 0, 100) / 100.0, 0, 1);
            TryWriteNumberUnsafe(slotConst, "winSuppressProb", Math.Clamp(suppressPct, 0, 100) / 100.0, 0, 1);
            return ok;
        }
    }

    public bool SetSymbolWeights(double wPct, double aPct, double sPct, double hPct, double mPct, double dPct)
    {
        lock (_sync)
        {
            if (!VerifyAliveUnsafe()) return false;
            double w = Math.Clamp(wPct, 0, 100);
            double a = Math.Clamp(aPct, 0, 100);
            double s = Math.Clamp(sPct, 0, 100);
            double h = Math.Clamp(hPct, 0, 100);
            double m = Math.Clamp(mPct, 0, 100);
            double d = Math.Clamp(dPct, 0, 100);
            if (w + a + s + h + m + d <= 0) return false; // Guard against zero total weight

            if (_origProbabilities == null) ReadProbabilitiesUnsafe();

            long constants = ResolveConstantsTableUnsafe();
            long symW = ResolveSubTableUnsafe(constants, "slot", "symbolWeights");
            if (symW == 0) return false;

            bool ok = true;
            ok &= TryWriteNumberUnsafe(symW, "W", w / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(symW, "A", a / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(symW, "S", s / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(symW, "H", h / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(symW, "M", m / 100.0, 0, 1);
            ok &= TryWriteNumberUnsafe(symW, "D", d / 100.0, 0, 1);
            return ok;
        }
    }

    public bool RestoreDefaultSymbolWeights()
    {
        var orig = _origProbabilities;
        if (orig != null)
            return SetSymbolWeights(orig.WeightW, orig.WeightA, orig.WeightS, orig.WeightH, orig.WeightM, orig.WeightD);
        return SetSymbolWeights(20, 20, 20, 15, 15, 10);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DetachUnsafe();
        }
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenHW.Hardware;

/// <summary>
/// The kernel side of the card filter: a cgroup device program, assembled here (no compiler), that refuses opens of
/// the lent card's device nodes to processes under the cgroup it's attached to — unless their own cgroup, or the
/// deepest ancestor with a verdict, is marked allowed. Unmarked cgroups are refused, so a program that starts
/// before the helper has looked at its cgroup can't grab the card. Ported from RTXinVM's rtxinvm_bpf.py (which used
/// an allow-list of four slices); here every cgroup the helper has classified carries a verdict.
///   devices  array, 8 × u64: major &lt;&lt; 32 | minor of the card's nodes (0 = empty)
///   verdicts hash, u64 cgroup id → u32: 1 = allowed, 2 = hidden
/// </summary>
public static class Bpf
{
    public const uint Allowed = 1, Hidden = 2;
    public const int DeviceSlots = 8, MaxVerdicts = 16384;
    private const int MaxDepth = 12; // user.slice(1)/user-N.slice(2)/user@N.service(3)/app.slice(4)/…

    private const long SysBpf = 321; // x86_64
    private const int MapCreate = 0, MapLookup = 1, MapUpdate = 2, MapDelete = 3, MapNextKey = 4, ProgLoad = 5,
        ObjPin = 6, ObjGet = 7, ProgAttach = 8, ProgDetach = 9, ProgQuery = 16;
    private const uint MapTypeHash = 1, MapTypeArray = 2, ProgTypeCgroupDevice = 15, AttachCgroupDevice = 6, AllowMulti = 2;
    private const int DevCharType = 2, HelperMapLookup = 1, HelperAncestorCgroupId = 123;

    // ── assembler ──────────────────────────────────────────────────────────────────────────────────────────────

    private const byte LdxW = 0x61, StxDw = 0x7b, Mov64K = 0xb7, Mov64X = 0xbf, And64K = 0x57, Lsh64K = 0x67,
        Or64X = 0x4f, Add64K = 0x07, Ja = 0x05, JeqK = 0x15, JneK = 0x55, JeqX = 0x1d, Call = 0x85, Exit = 0x95, LdDw = 0x18,
        LdxDw = 0x79, StW = 0x62;

    private sealed class Asm
    {
        private readonly List<object> _code = []; // byte[8] or a pending jump
        private readonly Dictionary<string, int> _labels = [];
        private sealed record Jump(byte Code, int Dst, int Src, string Label, int Imm);

        public void Emit(byte code, int dst = 0, int src = 0, short off = 0, int imm = 0) => _code.Add(Insn(code, dst, src, off, imm));
        public void LoadMap(int reg, int fd) { _code.Add(Insn(LdDw, reg, 1, 0, fd)); _code.Add(Insn(0, 0, 0, 0, 0)); } // two slots
        public void Jmp(byte code, int dst, int src, string label, int imm = 0) => _code.Add(new Jump(code, dst, src, label, imm));
        public void Label(string name) => _labels[name] = _code.Count;

        public byte[] Build()
        {
            var output = new byte[_code.Count * 8];
            for (var i = 0; i < _code.Count; i++)
            {
                var bytes = _code[i] is Jump j ? Insn(j.Code, j.Dst, j.Src, (short)(_labels[j.Label] - i - 1), j.Imm) : (byte[])_code[i];
                bytes.CopyTo(output, i * 8);
            }
            return output;
        }

        private static byte[] Insn(byte code, int dst, int src, short off, int imm)
        {
            var b = new byte[8];
            b[0] = code;
            b[1] = (byte)(src << 4 | dst);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), off);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), imm);
            return b;
        }
    }

    /// <summary>The filter program for these two maps (exposed for tests: instruction count, determinism).</summary>
    public static byte[] Program(int devicesFd, int verdictsFd)
    {
        var a = new Asm();
        a.Emit(Mov64X, 6, 1);                       // r6 = ctx
        a.Emit(LdxW, 7, 6, 0);                      // r7 = access_type: device type in the low 16 bits
        a.Emit(And64K, 7, imm: 0xffff);
        a.Jmp(JneK, 7, 0, "allow", DevCharType);
        a.Emit(LdxW, 8, 6, 4);                      // r8 = major << 32 | minor
        a.Emit(Lsh64K, 8, imm: 32);
        a.Emit(LdxW, 9, 6, 8);
        a.Emit(Or64X, 8, 9);
        for (var slot = 0; slot < DeviceSlots; slot++)
        {
            a.Emit(StW, 10, 0, -4, slot);           // key on the stack
            a.LoadMap(1, devicesFd);
            a.Emit(Mov64X, 2, 10);
            a.Emit(Add64K, 2, imm: -4);
            a.Emit(Call, imm: HelperMapLookup);
            a.Jmp(JeqK, 0, 0, $"dev{slot}");
            a.Emit(LdxDw, 0, 0, 0);
            a.Jmp(JeqK, 0, 0, $"dev{slot}");        // empty slot
            a.Jmp(JeqX, 0, 8, "card");
            a.Label($"dev{slot}");
        }
        a.Jmp(Ja, 0, 0, "allow");                   // not the lent card: nothing to decide

        a.Label("card");
        a.Emit(Mov64K, 9, imm: 0);                  // r9 = verdict of the deepest classified cgroup (0 = none)
        for (var level = 1; level <= MaxDepth; level++)
        {
            a.Emit(Mov64K, 1, imm: level);
            a.Emit(Call, imm: HelperAncestorCgroupId);
            a.Jmp(JeqK, 0, 0, "decide");            // deeper than the process's own cgroup
            a.Emit(StxDw, 10, 0, -16);
            a.LoadMap(1, verdictsFd);
            a.Emit(Mov64X, 2, 10);
            a.Emit(Add64K, 2, imm: -16);
            a.Emit(Call, imm: HelperMapLookup);
            a.Jmp(JeqK, 0, 0, $"level{level}");
            a.Emit(LdxW, 9, 0, 0);
            a.Label($"level{level}");
        }
        a.Label("decide");
        a.Jmp(JeqK, 9, 0, "allow", (int)Allowed);   // anything else — hidden, or never classified — is refused
        a.Label("deny");
        a.Emit(Mov64K, 0, imm: 0);
        a.Emit(Exit);
        a.Label("allow");
        a.Emit(Mov64K, 0, imm: 1);
        a.Emit(Exit);
        return a.Build();
    }

    // ── syscalls ───────────────────────────────────────────────────────────────────────────────────────────────

    private static unsafe int Sys(int cmd, ReadOnlySpan<byte> attr, out byte[] after)
    {
        var buffer = new byte[128];
        attr.CopyTo(buffer);
        long r;
        fixed (byte* p = buffer) r = Native.syscall(SysBpf, cmd, (nint)p, buffer.Length);
        after = buffer;
        if (r < 0) throw new IOException($"bpf({cmd}) failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return (int)r;
    }

    private static int Sys(int cmd, ReadOnlySpan<byte> attr) => Sys(cmd, attr, out _);

    private static byte[] Attr(params object[] fields)
    {
        var b = new List<byte>();
        foreach (var f in fields)
            b.AddRange(f switch
            {
                uint u => BitConverter.GetBytes(u),
                int i => BitConverter.GetBytes(i),
                ulong q => BitConverter.GetBytes(q),
                long l => BitConverter.GetBytes(l),
                byte[] raw => raw,
                _ => throw new ArgumentException(f.GetType().Name),
            });
        return b.ToArray();
    }

    private static byte[] Name(string name) { var b = new byte[16]; System.Text.Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(15, name.Length)).CopyTo(b); return b; }

    public static int CreateDevicesMap() => Sys(MapCreate, Attr(MapTypeArray, 4u, 8u, (uint)DeviceSlots, 0u, 0u, 0u, Name("openhw_devs")));
    public static int CreateVerdictsMap() => Sys(MapCreate, Attr(MapTypeHash, 8u, 4u, (uint)MaxVerdicts, 0u, 0u, 0u, Name("openhw_verdict")));

    public static unsafe int LoadProgram(int devicesFd, int verdictsFd)
    {
        var code = Program(devicesFd, verdictsFd);
        var license = "GPL\0"u8.ToArray();
        var log = new byte[1 << 16];
        fixed (byte* c = code) fixed (byte* l = license) fixed (byte* g = log)
        {
            try
            {
                return Sys(ProgLoad, Attr(ProgTypeCgroupDevice, (uint)(code.Length / 8), (ulong)c, (ulong)l, 1u, (uint)log.Length, (ulong)g,
                    0u, 0u, Name("openhw_card"), 0u, AttachCgroupDevice));
            }
            catch (IOException ex)
            {
                var text = System.Text.Encoding.ASCII.GetString(log).TrimEnd('\0');
                throw new IOException(ex.Message + ": " + (text.Length > 2000 ? text[^2000..] : text));
            }
        }
    }

    public static unsafe void SetDevice(int fd, uint slot, ulong value)
    {
        uint key = slot; ulong v = value;
        Sys(MapUpdate, Attr((uint)fd, 0u, (ulong)&key, (ulong)&v, 0ul));
    }

    public static unsafe ulong GetDevice(int fd, uint slot)
    {
        uint key = slot; ulong v = 0;
        Sys(MapLookup, Attr((uint)fd, 0u, (ulong)&key, (ulong)&v, 0ul));
        return v;
    }

    public static unsafe void SetVerdict(int fd, ulong cgroupId, uint verdict)
    {
        ulong key = cgroupId; uint v = verdict;
        Sys(MapUpdate, Attr((uint)fd, 0u, (ulong)&key, (ulong)&v, 0ul));
    }

    public static unsafe uint? GetVerdict(int fd, ulong cgroupId)
    {
        ulong key = cgroupId; uint v = 0;
        try { Sys(MapLookup, Attr((uint)fd, 0u, (ulong)&key, (ulong)&v, 0ul)); return v; }
        catch (IOException) { return null; }
    }

    public static unsafe void DeleteVerdict(int fd, ulong cgroupId)
    {
        ulong key = cgroupId;
        try { Sys(MapDelete, Attr((uint)fd, 0u, (ulong)&key, 0ul, 0ul)); } catch (IOException) { }
    }

    /// <summary>Every cgroup id the verdicts map holds.</summary>
    public static unsafe List<ulong> VerdictKeys(int fd)
    {
        var keys = new List<ulong>();
        ulong previous = 0, key = 0;
        while (true)
        {
            // A null key asks for the first; ENOENT after the last ends the walk.
            try { Sys(MapNextKey, Attr((uint)fd, 0u, keys.Count == 0 ? 0ul : (ulong)&previous, (ulong)&key)); }
            catch (IOException) { return keys; }
            keys.Add(key);
            previous = key;
        }
    }

    public static unsafe void Pin(int fd, string path)
    {
        var p = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* b = p) Sys(ObjPin, Attr((ulong)b, (uint)fd, 0u));
    }

    public static unsafe int? GetPinned(string path)
    {
        var p = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        try { fixed (byte* b = p) return Sys(ObjGet, Attr((ulong)b, 0u, 0u)); }
        catch (IOException) { return null; }
    }

    public static void Attach(int progFd, string cgroup) => WithCgroup(cgroup, cg => Sys(ProgAttach, Attr((uint)cg, (uint)progFd, AttachCgroupDevice, AllowMulti)));
    public static void Detach(int progFd, string cgroup) => WithCgroup(cgroup, cg => Sys(ProgDetach, Attr((uint)cg, (uint)progFd, AttachCgroupDevice, 0u)));

    /// <summary>Ids of the device programs attached directly to this cgroup.</summary>
    public static unsafe List<uint> AttachedIds(string cgroup) => WithCgroup(cgroup, cg =>
    {
        var ids = new uint[64];
        fixed (uint* p = ids)
        {
            Sys(ProgQuery, Attr((uint)cg, AttachCgroupDevice, 0u, 0u, (ulong)p, 64u), out var after);
            return ids.Take((int)BitConverter.ToUInt32(after, 24)).ToList();
        }
    });

    /// <summary>A loaded program's id, from /proc/self/fdinfo.</summary>
    public static uint ProgramId(int progFd) =>
        File.ReadLines($"/proc/self/fdinfo/{progFd}").Where(l => l.StartsWith("prog_id:", StringComparison.Ordinal))
            .Select(l => uint.Parse(l[8..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();

    private static T WithCgroup<T>(string path, Func<int, T> action)
    {
        var fd = Native.open(path, 0x10000 /* O_DIRECTORY */ | 0x80000 /* O_CLOEXEC */);
        if (fd < 0) throw new IOException($"cannot open {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        try { return action(fd); }
        finally { Native.close(fd); }
    }

    /// <summary>On cgroup v2 a cgroup's id is its directory's inode number.</summary>
    public static unsafe ulong CgroupId(string path)
    {
        var st = stackalloc byte[256];
        if (Native.stat(path, st) != 0) throw new IOException($"stat {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        return *(ulong*)(st + 8); // struct stat (x86_64): st_dev, then st_ino
    }

    public static ulong EncodeDevice(uint major, uint minor) => (ulong)major << 32 | minor;

    public static void Close(int fd) => Native.close(fd);

    private static unsafe class Native
    {
        [DllImport("libc", SetLastError = true)] public static extern long syscall(long number, long cmd, nint attr, long size);
        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc")] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int stat(string path, byte* buffer);
    }
}

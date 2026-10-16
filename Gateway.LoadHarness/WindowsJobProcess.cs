using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Gateway.LoadHarness;

// No Process.Start fallback: the gateway cannot execute managed code before assignment
// and verification. Only the three child pipe ends appear in HANDLE_LIST.
internal sealed class WindowsJobProcess : IDisposable
{
    private const uint ProcessMemory = 0x100, KillOnClose = 0x2000;
    private KernelHandle? job, process;
    private bool disposed;
    internal StreamWriter StandardInput { get; private set; } = null!;
    internal StreamReader StandardOutput { get; private set; } = null!;
    internal StreamReader StandardError { get; private set; } = null!;

    internal static WindowsJobProcess Start(long bytes, Enforcement evidence, params string[] args)
    {
        Common.Require(OperatingSystem.IsWindows());
        var child = new WindowsJobProcess();
        try
        {
            child.job = Native.CreateJobObjectW(IntPtr.Zero, null); // anonymous, non-inheritable
            Check(!child.job.IsInvalid);
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = ProcessMemory | KillOnClose }, ProcessMemoryLimit = checked((nuint)bytes) };
            Check(Native.SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));

            using var input = new Pipe(parentReads: false);
            using var output = new Pipe(parentReads: true);
            using var error = new Pipe(parentReads: true);
            using var handles = new NativeBuffer(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles.DangerousGetHandle(), 0, input.Child.DangerousGetHandle());
            Marshal.WriteIntPtr(handles.DangerousGetHandle(), IntPtr.Size, output.Child.DangerousGetHandle());
            Marshal.WriteIntPtr(handles.DangerousGetHandle(), 2 * IntPtr.Size, error.Child.DangerousGetHandle());
            using var attributes = new AttributeList();
            Check(Native.UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles.DangerousGetHandle(), (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    Input = input.Child.DangerousGetHandle(),
                    Output = output.Child.DangerousGetHandle(),
                    Error = error.Child.DangerousGetHandle()
                },
                Attributes = attributes.DangerousGetHandle()
            };
            var host = Environment.ProcessPath;
            if (!string.Equals(Path.GetFileName(host), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            Common.Require(host is not null && Path.IsPathFullyQualified(host) && File.Exists(host));
            var command = new StringBuilder(string.Join(" ", new[] { host!, typeof(WindowsJobProcess).Assembly.Location }.Concat(args).Select(Quote)));
            Common.Require(command.Length < 32767);
            Check(Native.CreateProcessW(host!, command, IntPtr.Zero, IntPtr.Zero, true,
                0x00000004 | 0x00080000 | 0x08000000, IntPtr.Zero, null, ref startup, out var created));
            child.process = new KernelHandle(created.Process);
            using var thread = new KernelHandle(created.Thread);
            Check(Native.AssignProcessToJobObject(child.job, child.process));
            evidence.AssignedBeforeResume = true;
            child.Snapshot(evidence, before: true);
            Common.Require(evidence.VerificationBefore == true);
            // Transfer parent ends to streams before resume; any failure still kills suspended child.
            child.StandardInput = new StreamWriter(new FileStream(input.TakeParent(), FileAccess.Write, 4096, false), new UTF8Encoding(false));
            child.StandardOutput = new StreamReader(new FileStream(output.TakeParent(), FileAccess.Read, 4096, false), Encoding.UTF8);
            child.StandardError = new StreamReader(new FileStream(error.TakeParent(), FileAccess.Read, 4096, false), Encoding.UTF8);
            Check(Native.ResumeThread(thread) == 1);
            return child;
        }
        catch
        {
            evidence.Cleanup = false; // failed launch can never count as cooperative cleanup
            child.Dispose();
            throw new InvalidOperationException("Hard-limit launch failed.");
        }
    }

    // CommandLineToArgvW / CRT quoting, including empty arguments, quotes and trailing slashes.
    private static string Quote(string value)
    {
        Common.Require(!value.Contains('\0'));
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal void Snapshot(Enforcement e, bool before)
    {
        var queried = Native.QueryInformationJobObject(job!, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _);
        var memberKnown = Native.IsProcessInJob(process!, job!, out var member);
        long? bytes = queried ? checked((long)limits.ProcessMemoryLimit) : null;
        bool? memory = queried ? (limits.Basic.Flags & ProcessMemory) != 0 : null;
        bool? kill = queried ? (limits.Basic.Flags & KillOnClose) != 0 : null;
        bool? membership = memberKnown ? member : null;
        if (queried) e.PeakProcessCommitBytes = Math.Max(e.PeakProcessCommitBytes ?? 0, checked((long)limits.PeakProcessMemoryUsed));
        var verified = bytes == e.RequestedBytes && memory == true && kill == true && membership == true;
        if (before)
        {
            e.QueriedBytesBefore = bytes; e.MembershipBefore = membership;
            e.ProcessMemoryFlagBefore = memory; e.KillOnCloseFlagBefore = kill;
            e.VerificationBefore = verified && e.AssignedBeforeResume == true;
        }
        else
        {
            e.QueriedBytesAfter = bytes; e.MembershipAfter = membership;
            e.ProcessMemoryFlagAfter = memory; e.KillOnCloseFlagAfter = kill; e.VerificationAfter = verified;
            if (!queried) e.PeakProcessCommitBytes = null;
        }
    }

    internal void FinalPeak(Enforcement e)
    {
        if (Native.QueryInformationJobObject(job!, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _))
            e.PeakProcessCommitBytes = Math.Max(e.PeakProcessCommitBytes ?? 0, checked((long)limits.PeakProcessMemoryUsed));
        else { e.PeakProcessCommitBytes = null; e.VerificationAfter = false; }
    }

    internal bool HasExited
    {
        get { var result = Native.WaitForSingleObject(process!, 0); Check(result is 0 or 258); return result == 0; }
    }
    internal int ExitCode
    {
        get { Common.Require(HasExited); Check(Native.GetExitCodeProcess(process!, out var code)); return unchecked((int)code); }
    }
    internal async Task WaitForExitAsync(CancellationToken ct)
    {
        while (!HasExited) { ct.ThrowIfCancellationRequested(); await Task.Delay(20, ct); }
    }
    internal void KillTree() => Check(Native.TerminateJobObject(job!, 1));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (process is not null && !process.IsInvalid)
            {
                // Also covers failure before assignment; closing the job alone would not.
                if (Native.WaitForSingleObject(process, 0) != 0) Native.TerminateProcess(process, 1);
            }
        }
        finally
        {
            job?.Dispose(); // KILL_ON_JOB_CLOSE fallback, including descendants
            try
            {
                if (process is not null && !process.IsInvalid) Native.WaitForSingleObject(process, 10000);
                StandardInput?.Dispose();
            }
            finally
            {
                try { StandardOutput?.Dispose(); }
                finally { try { StandardError?.Dispose(); } finally { process?.Dispose(); } }
            }
        }
    }
    private static void Check(bool ok) { if (!ok) throw new InvalidOperationException("Windows job operation failed."); }

    private sealed class Pipe : IDisposable
    {
        internal SafeFileHandle Child { get; }
        private SafeFileHandle? parent;
        internal Pipe(bool parentReads)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            Check(Native.CreatePipe(out var read, out var write, ref security, 0));
            Child = parentReads ? write : read; parent = parentReads ? read : write;
            if (!Native.SetHandleInformation(parent, 1, 0)) { Dispose(); Check(false); }
        }
        internal SafeFileHandle TakeParent() { var result = parent!; parent = null; return result; }
        public void Dispose() { parent?.Dispose(); Child.Dispose(); }
    }
    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        internal KernelHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    private sealed class NativeBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeBuffer(int size) : base(true) => SetHandle(Marshal.AllocHGlobal(size));
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }
    private sealed class AttributeList : SafeHandleZeroOrMinusOneIsInvalid
    {
        private bool initialized;
        internal AttributeList() : base(true)
        {
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            Common.Require(size > 0 && size <= int.MaxValue);
            SetHandle(Marshal.AllocHGlobal((int)size));
            initialized = Native.InitializeProcThreadAttributeList(handle, 1, 0, ref size);
            if (!initialized) { Dispose(); Check(false); }
        }
        protected override bool ReleaseHandle()
        {
            if (initialized) Native.DeleteProcThreadAttributeList(handle);
            Marshal.FreeHGlobal(handle); return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern KernelHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(KernelHandle job, int type, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(KernelHandle job, int type, out ExtendedLimits info, uint length, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(KernelHandle process, KernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(AttributeList list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(KernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(KernelHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(KernelHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Gateway;

namespace Gateway.LoadHarness;

internal static class Common
{
    internal const int BodySize = 1048576;
    internal static readonly int[] Concurrencies = [1, 2, 4, 8];
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static string Revision => typeof(PanResponseTransform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    internal static byte[] Payload()
    {
        var text = new StringBuilder("{\"payment\":{\"cardNumber\":\"" + TestApi.SyntheticApi.Pan + "\"},\"items\":[");
        for (var i = 0; i < 24000; i++) { if (i != 0) text.Append(','); text.Append("{\"a\":1,\"b\":true,\"c\":\"x\"}"); }
        text.Append("],\"padding\":\"");
        text.Append('x', BodySize - Encoding.UTF8.GetByteCount(text.ToString()) - 2).Append("\"}");
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        using var parsed = JsonDocument.Parse(bytes);
        Require(bytes.Length == BodySize && parsed.RootElement.GetProperty("payment").GetProperty("cardNumber").GetString() == TestApi.SyntheticApi.Pan);
        return bytes;
    }
    internal static bool Canary(ReadOnlySpan<byte> bytes, int size) => bytes.Length == size
        && bytes.IndexOf("4111111111111111"u8) < 0 && bytes.IndexOf("************1111"u8) >= 0;
    internal static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Harness invariant failed."); }
    internal static string OutputDirectory(string value)
    {
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        Require(File.Exists(Path.Combine(workspace, "ProyectoSeguridad.slnx")));
        if (OperatingSystem.IsWindows())
            Require(new DriveInfo(Path.GetPathRoot(workspace)!).DriveType == DriveType.Fixed);
        var root = Path.Combine(workspace, "artifacts", "mvp05");
        var output = Path.GetFullPath(value);
        var relative = Path.GetRelativePath(root, output);
        Require(relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative));
        for (var current = new DirectoryInfo(output); current is not null; current = current.Parent)
        {
            if (current.Exists) Require((current.Attributes & FileAttributes.ReparsePoint) == 0);
        }
        return output;
    }
    internal static void Collect(bool compact = false)
    {
        if (compact) GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
    internal static Sample Measure(string phase, bool forcedGc, InspectionBudget budget)
    {
        var managed = GC.GetTotalMemory(false); var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess(); process.Refresh(); var snapshot = budget.Snapshot;
        return new(phase, forcedGc, managed, info.HeapSizeBytes, info.TotalCommittedBytes, info.FragmentedBytes,
            info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes, GC.GetTotalAllocatedBytes(), GC.CollectionCount(0),
            GC.CollectionCount(1), GC.CollectionCount(2), process.WorkingSet64, process.PrivateMemorySize64,
            snapshot.ActiveInspections, snapshot.ReservedBodyBytes);
    }
    internal static async Task Atomic(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory); var target = Path.Combine(directory, name); var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false)); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static async Task Save<T>(string directory, string name, T model, Func<T, string> markdown)
    {
        await Atomic(directory, name + ".json", JsonSerializer.Serialize(model, Json));
        await Atomic(directory, name + ".md", markdown(model));
    }
}

internal sealed record Sample(string Phase, bool ForcedGc, long ManagedBytes, long HeapSizeBytes, long TotalCommittedBytes,
    long FragmentedBytes, long MemoryLoadBytes, long HighMemoryLoadThresholdBytes, long TotalAllocatedBytes,
    int Gen0, int Gen1, int Gen2, long WorkingSetBytes, long PrivateBytes, int Active, long Reserved);

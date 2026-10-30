using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Gateway;

// No mutable options or IConfiguration escapes compilation.
internal sealed class LocalPolicySnapshot
{
    internal PanPolicyCatalog Catalog { get; }
    internal ImmutableArray<(string Route, string Policy)> Routes { get; }
    internal ImmutableArray<(string Route, string Selector)> Coverage { get; }
    internal string CoverageVersion { get; }
    internal (int Timeout, int Concurrency, long Bytes) Budget { get; }

    internal LocalPolicySnapshot(PanPolicyOptions options, IConfiguration configuration)
    {
        Catalog = new PanPolicyCatalog(Options.Create(options), configuration);
        Routes = options.Policies.OrderBy(p => p.RouteId, StringComparer.Ordinal)
            .Select(p => (p.RouteId, p.PolicyId)).ToImmutableArray();
        Coverage = options.CoverageContract!.Required.OrderBy(p => p.RouteId, StringComparer.Ordinal)
            .ThenBy(p => p.Selector, StringComparer.Ordinal).Select(p => (p.RouteId, p.Selector)).ToImmutableArray();
        CoverageVersion = options.CoverageContract.Version;
        var b = options.InspectionBudget!;
        Budget = (b.InspectionTimeoutSeconds, b.MaxConcurrentInspections, b.MaxReservedBodyBytes);
    }

    internal bool Compatible(LocalPolicySnapshot other) => Routes.SequenceEqual(other.Routes)
        && Coverage.SequenceEqual(other.Coverage) && CoverageVersion == other.CoverageVersion && Budget == other.Budget;

    internal static bool SameBehavior(CompiledPanPolicy a, CompiledPanPolicy b) =>
        a.Mode == b.Mode && a.Transformation == b.Transformation && a.MaxBodyBytes == b.MaxBodyBytes
        && a.SecondaryPanDetection == b.SecondaryPanDetection && a.Segments.SequenceEqual(b.Segments)
        && a.AdditionalRules.Length == b.AdditionalRules.Length
        && a.AdditionalRules.Zip(b.AdditionalRules).All(pair => pair.First.Transformation == pair.Second.Transformation
            && pair.First.Segments.SequenceEqual(pair.Second.Segments));
}

internal sealed record LocalPolicyView(LocalPolicySnapshot Snapshot, DashboardPolicyReload Reload);

internal sealed class LocalPolicySnapshots
{
    private LocalPolicyView active;
    private readonly LocalPolicySnapshot baseline;
    private readonly FrozenSet<string> protectedRoutes;
    internal LocalPolicySnapshots(LocalPolicySnapshot baseline)
    {
        this.baseline = baseline;
        active = new(baseline, new(false, "disabled", null, null, null));
        protectedRoutes = baseline.Routes.Select(p => p.Route).ToFrozenSet(StringComparer.Ordinal);
    }
    internal LocalPolicyView Capture() => Volatile.Read(ref active);
    internal LocalPolicySnapshot Active => Capture().Snapshot;
    internal bool IsProtected(string routeId) => protectedRoutes.Contains(routeId);

    // Single writer: initialization followed by the sequential reload service.
    internal bool Publish(LocalPolicySnapshot candidate, CancellationToken cancellationToken, bool initial = false,
        DateTimeOffset? checkedAtUtc = null)
    {
        var view = Capture();
        var previous = view.Snapshot;
        if (!baseline.Compatible(candidate)) throw new LocalPolicyReadException("incompatible_snapshot");
        var changed = initial;
        foreach (var (route, _) in baseline.Routes)
        {
            var before = previous.Catalog.Find(route)!;
            var after = candidate.Catalog.Find(route)!;
            var same = LocalPolicySnapshot.SameBehavior(before, after);
            if (!initial && !same && before.Version == after.Version)
                throw new LocalPolicyReadException("incompatible_snapshot");
            changed |= !same || before.Version != after.Version;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = changed ? candidate : previous;
        if (checkedAtUtc is { } checkedAt)
        {
            var utc = checkedAt.ToUniversalTime();
            Volatile.Write(ref active, new(snapshot, new(true, "valid", null, utc,
                changed ? utc : view.Reload.LastAppliedAtUtc)));
        }
        else if (changed) Volatile.Write(ref active, new(snapshot, view.Reload));
        return changed;
    }

    internal void RecordRejection(string reason, DateTimeOffset checkedAtUtc)
    {
        if (reason is not ("read_failed" or "too_large" or "invalid_configuration" or "incompatible_snapshot"))
            throw new ArgumentOutOfRangeException(nameof(reason));
        var view = Capture();
        Volatile.Write(ref active, new(view.Snapshot, new(true, "rejected", reason,
            checkedAtUtc.ToUniversalTime(), view.Reload.LastAppliedAtUtc)));
    }
}

internal sealed class LocalPolicyReadException(string reason) : Exception
{
    internal string Reason { get; } = reason;
}

internal sealed class LocalPolicyFileReader(string path, IReadOnlyList<RouteConfig> routes,
    Func<string, Stream>? streamOpener = null)
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly Func<string, Stream> openStream = streamOpener ?? OpenFile;

    private static Stream OpenFile(string path) => new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal async Task<LocalPolicySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidatePath(path);
            // Internal test seam never bypasses location validation; production always uses OpenFile.
            // One stream, never trust metadata Length for the actual byte ceiling.
            await using var stream = openStream(path);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumBytes) throw new LocalPolicyReadException("too_large");
            cancellationToken.ThrowIfCancellationRequested();
            return Compile(bytes.AsMemory(0, count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new LocalPolicyReadException("read_failed"); }
    }

    private LocalPolicySnapshot Compile(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 1 || fields[0].Name != PanPolicyOptions.SectionName
                || fields[0].Value.ValueKind != JsonValueKind.Object) throw new FormatException();
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(input).Build();
            var options = new PanPolicyOptions();
            configuration.GetSection(PanPolicyOptions.SectionName).Bind(options, b => b.ErrorOnUnknownConfiguration = true);
            var result = new PanPolicyValidator(routes).Validate(Options.DefaultName, options);
            if (result.Failed) throw new FormatException();
            return new LocalPolicySnapshot(options, configuration);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OptionsValidationException)
        { throw new LocalPolicyReadException("invalid_configuration"); }
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)) throw new LocalPolicyReadException("read_failed");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        // This local Windows slice deliberately requires a fixed local filesystem, not devices/mounts/pipes.
        if (!OperatingSystem.IsWindows() || new DriveInfo(root).DriveType != DriveType.Fixed
            || full[root.Length..].Contains(':')) throw new LocalPolicyReadException("read_failed");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || (current == full && (attributes & FileAttributes.Directory) != 0))
                throw new LocalPolicyReadException("read_failed");
        }
    }
}

internal sealed record LocalPolicyRequestCapture(LocalPolicySnapshot Snapshot, CompiledPanPolicy Policy);

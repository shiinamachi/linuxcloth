namespace LinuxCloth.Desktop.Setup;

public enum PackageChangeKind
{
    AlreadyInstalled,
    Install,
    Update,
    Reinstall,
    Downgrade,
    Remove,
    Other,
}

public sealed record PackageChange(
    string Name,
    string Version,
    string Architecture,
    string Repository,
    string PackageId,
    string Summary,
    PackageChangeKind Kind,
    ulong DownloadSize);

public sealed class PackageInstallPreview
{
    public PackageInstallPreview(
        PackagePlan plan,
        bool isPackageKitAvailable,
        IReadOnlyList<PackageChange> changes,
        IReadOnlyList<string> unresolvedPackages,
        IReadOnlyList<string> installPackageIds)
    {
        Plan = plan;
        IsPackageKitAvailable = isPackageKitAvailable;
        Changes = changes;
        UnresolvedPackages = unresolvedPackages;
        InstallPackageIds = installPackageIds;
        Repositories = changes
            .Where(change => change.Kind != PackageChangeKind.AlreadyInstalled)
            .Select(change => change.Repository)
            .Where(repository => !string.IsNullOrWhiteSpace(repository))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        DownloadSize = changes
            .Where(change => change.Kind != PackageChangeKind.AlreadyInstalled)
            .Aggregate(0UL, static (sum, change) => checked(sum + change.DownloadSize));
    }

    public PackagePlan Plan { get; }

    public bool IsPackageKitAvailable { get; }

    public IReadOnlyList<PackageChange> Changes { get; }

    public IReadOnlyList<string> UnresolvedPackages { get; }

    public IReadOnlyList<string> Repositories { get; }

    public ulong DownloadSize { get; }

    public bool IsAlreadySatisfied =>
        IsPackageKitAvailable && UnresolvedPackages.Count == 0 && InstallPackageIds.Count == 0;

    public bool CanInstall =>
        IsPackageKitAvailable && UnresolvedPackages.Count == 0 && InstallPackageIds.Count > 0;

    internal IReadOnlyList<string> InstallPackageIds { get; }
}

public sealed record PackageInstallProgress(
    string Status,
    string? PackageName = null,
    int? Percentage = null);

public sealed record PackageInstallResult(bool Succeeded, string Message);

public interface IPackageInstaller
{
    Task<PackageInstallPreview> ResolveAsync(
        PackagePlan plan,
        CancellationToken cancellationToken = default);

    Task<PackageInstallResult> InstallAsync(
        PackageInstallPreview preview,
        IProgress<PackageInstallProgress> progress,
        CancellationToken cancellationToken = default);
}

public sealed record PackageKitPackage(uint Info, string PackageId, string Summary)
{
    public PackageIdParts ParseId() => PackageIdParts.Parse(PackageId);

    public bool IsInstalled =>
        Info == PackageKitEnums.InfoInstalled ||
        ParseId().Repository.StartsWith("installed", StringComparison.Ordinal);
}

public sealed record PackageIdParts(
    string Name,
    string Version,
    string Architecture,
    string Repository)
{
    public static PackageIdParts Parse(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var parts = packageId.Split(';');
        if (parts.Length != 4 || parts.Any(static part => part.Any(char.IsControl)))
        {
            throw new InvalidDataException($"PackageKit 패키지 ID 형식이 올바르지 않습니다: {packageId}");
        }

        return new PackageIdParts(parts[0], parts[1], parts[2], parts[3]);
    }
}

public sealed record PackageKitDetails(string PackageId, ulong Size, string? Summary);

public interface IPackageKitClient : IAsyncDisposable
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageKitPackage>> ResolveAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageKitPackage>> SimulateInstallAsync(
        IReadOnlyList<string> packageIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, PackageKitDetails>> GetDetailsAsync(
        IReadOnlyList<string> packageIds,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        IReadOnlyList<string> packageIds,
        IProgress<PackageInstallProgress> progress,
        CancellationToken cancellationToken = default);
}

public static class PackageKitEnums
{
    public const uint ExitSuccess = 1;
    public const uint InfoInstalled = 1;
    public const uint InfoAvailable = 2;
    public const ulong FilterNone = 1UL << 1;
    public const ulong TransactionOnlyTrusted = 1UL << 1;
    public const ulong TransactionSimulate = 1UL << 2;

    public static PackageChangeKind ToChangeKind(uint info) => info switch
    {
        1 => PackageChangeKind.AlreadyInstalled,
        11 => PackageChangeKind.Update,
        12 or 27 => PackageChangeKind.Install,
        13 or 28 => PackageChangeKind.Remove,
        18 => PackageChangeKind.Reinstall,
        19 or 30 => PackageChangeKind.Downgrade,
        _ => PackageChangeKind.Other,
    };
}

public sealed class PackageKitPackageInstaller : IPackageInstaller, IAsyncDisposable
{
    private readonly HashSet<PackageInstallPreview> _approvedPreviews = [];
    private readonly IPackageKitClient _client;

    public PackageKitPackageInstaller(IPackageKitClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<PackageInstallPreview> ResolveAsync(
        PackagePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!await _client.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Approve(new PackageInstallPreview(plan, false, [], [], []));
        }

        var resolved = await _client.ResolveAsync(plan.AllPackages, cancellationToken)
            .ConfigureAwait(false);
        var resolvedByName = resolved
            .Select(package => (Package: package, Id: package.ParseId()))
            .GroupBy(item => item.Id.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var unresolved = new List<string>();
        var alreadyInstalled = new List<PackageKitPackage>();
        var installIds = new List<string>();
        foreach (var packageName in plan.AllPackages)
        {
            if (!resolvedByName.TryGetValue(packageName, out var candidates))
            {
                unresolved.Add(packageName);
                continue;
            }

            var installed = candidates.FirstOrDefault(candidate => candidate.Package.IsInstalled);
            if (installed.Package is not null)
            {
                alreadyInstalled.Add(installed.Package);
                continue;
            }

            var available = candidates
                .Where(candidate => candidate.Package.Info == PackageKitEnums.InfoAvailable)
                .OrderBy(candidate => candidate.Package.PackageId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (available.Package is null)
            {
                unresolved.Add(packageName);
                continue;
            }

            installIds.Add(available.Package.PackageId);
        }

        IReadOnlyList<PackageKitPackage> simulated = installIds.Count == 0
            ? []
            : await _client.SimulateInstallAsync(installIds, cancellationToken).ConfigureAwait(false);
        var changedPackages = simulated
            .GroupBy(package => package.PackageId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var detailIds = changedPackages.Select(package => package.PackageId).ToArray();
        IReadOnlyDictionary<string, PackageKitDetails> details = detailIds.Length == 0
            ? new Dictionary<string, PackageKitDetails>(StringComparer.Ordinal)
            : await _client.GetDetailsAsync(detailIds, cancellationToken).ConfigureAwait(false);
        var changes = alreadyInstalled
            .Select(package => CreateChange(package, null, PackageChangeKind.AlreadyInstalled))
            .Concat(changedPackages.Select(package =>
                CreateChange(
                    package,
                    details.GetValueOrDefault(package.PackageId),
                    PackageKitEnums.ToChangeKind(package.Info))))
            .OrderBy(change => change.Kind == PackageChangeKind.AlreadyInstalled ? 1 : 0)
            .ThenBy(change => change.Name, StringComparer.Ordinal)
            .ToArray();
        return Approve(
            new PackageInstallPreview(
                plan,
                true,
                changes,
                unresolved,
                changedPackages
                    .Where(package => PackageKitEnums.ToChangeKind(package.Info) != PackageChangeKind.Remove)
                    .Select(package => package.PackageId)
                    .ToArray()));
    }

    public async Task<PackageInstallResult> InstallAsync(
        PackageInstallPreview preview,
        IProgress<PackageInstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(progress);
        if (!_approvedPreviews.Remove(preview))
        {
            throw new InvalidOperationException("현재 패키지 설치 관리자가 승인한 미리보기가 아닙니다.");
        }

        if (preview.IsAlreadySatisfied)
        {
            return new PackageInstallResult(true, "필수 구성 요소가 이미 설치되어 있습니다.");
        }

        if (!preview.CanInstall)
        {
            throw new InvalidOperationException(
                preview.IsPackageKitAvailable
                    ? "해결되지 않은 패키지가 있어 설치할 수 없습니다."
                    : "PackageKit을 사용할 수 없어 앱에서 자동 설치할 수 없습니다.");
        }

        progress.Report(new PackageInstallProgress("관리자 인증을 기다리고 있습니다…"));
        await _client.InstallAsync(preview.InstallPackageIds, progress, cancellationToken)
            .ConfigureAwait(false);
        return new PackageInstallResult(true, "필수 구성 요소 설치를 완료했습니다.");
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private PackageInstallPreview Approve(PackageInstallPreview preview)
    {
        _approvedPreviews.Add(preview);
        return preview;
    }

    private static PackageChange CreateChange(
        PackageKitPackage package,
        PackageKitDetails? details,
        PackageChangeKind kind)
    {
        var id = package.ParseId();
        return new PackageChange(
            id.Name,
            id.Version,
            id.Architecture,
            id.Repository,
            package.PackageId,
            details?.Summary ?? package.Summary,
            kind,
            details?.Size ?? 0);
    }
}

public sealed class PackageKitException : Exception
{
    public PackageKitException(string message)
        : base(message)
    {
    }
}

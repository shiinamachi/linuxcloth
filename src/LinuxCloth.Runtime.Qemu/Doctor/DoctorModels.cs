namespace LinuxCloth.Runtime.Qemu.Doctor;

public sealed record DoctorCheck(
    string Name,
    bool IsRequired,
    bool IsAvailable,
    string Detail,
    string? ResolvedPath = null);

public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    public bool CanLaunch => Checks.Where(static check => check.IsRequired).All(static check => check.IsAvailable);

    public string? FindPath(string name) =>
        Checks.FirstOrDefault(check => string.Equals(check.Name, name, StringComparison.Ordinal))?.ResolvedPath;
}


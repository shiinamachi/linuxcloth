namespace LinuxCloth.Application.Setup;

public interface ISetupRunStore
{
    Task<SetupRun?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SetupRun run, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

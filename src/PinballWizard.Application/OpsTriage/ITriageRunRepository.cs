namespace PinballWizard.Application.OpsTriage;

public interface ITriageRunRepository
{
    Task UpsertAsync(TriageRun run, CancellationToken cancellationToken);

    Task<TriageRun?> GetAsync(string id, string alertDay, CancellationToken cancellationToken);

    /// <summary>Newest-first runs across recent partition days (admin list).</summary>
    Task<IReadOnlyList<TriageRun>> ListRecentAsync(int take, CancellationToken cancellationToken);
}

using ClapBack.Core.Models;

namespace ClapBack.Core.Abstractions;

public interface ISettingsStore
{
    Task<ClapBackSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ClapBackSettings settings, CancellationToken cancellationToken = default);
}

public interface ICredentialStore
{
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

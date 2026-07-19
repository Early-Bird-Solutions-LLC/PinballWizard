namespace PinballWizard.Application.Resolution;

// Catalog abstraction injected into MachineAliasLoader to verify that each
// alias resolves to a real group or machine. Production binds this against
// IMachineRepository; tests supply an in-memory fake so no Cosmos call is
// made from tests.
//
// Separate from IMachineAliasLoader because the two have different
// implementers and live on different sides of the boundary: the loader is an
// Application-layer singleton, while this is bound to an Infrastructure
// repository.
public interface IMachineAliasCatalog
{
    Task<bool> GroupExistsAsync(string groupId, string manufacturerKey, CancellationToken cancellationToken);
    Task<bool> MachineExistsAsync(string machineId, string manufacturerKey, CancellationToken cancellationToken);
}

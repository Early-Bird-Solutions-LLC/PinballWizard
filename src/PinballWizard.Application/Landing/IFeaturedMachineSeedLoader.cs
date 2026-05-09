using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Landing;

// Abstraction over the featured_machines.v1.json file load.
// Sealed by FeaturedMachineSeedLoader (Application layer, file-system read).
// Isolated as an interface so the CLI verb and tests can substitute a
// fake without touching the file system.
public interface IFeaturedMachineSeedLoader
{
    Task<IReadOnlyList<FeaturedMachineDocument>> LoadAsync(CancellationToken cancellationToken);
}

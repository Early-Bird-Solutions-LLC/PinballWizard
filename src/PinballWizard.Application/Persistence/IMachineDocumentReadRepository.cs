using PinballWizard.Application.Catalog;

namespace PinballWizard.Application.Persistence;

public interface IMachineDocumentReadRepository
{
    // Tier 1: single-partition read of scraped_documents by machine_id.
    IAsyncEnumerable<MachineDocumentLink> StreamByMachineIdAsync(string machineId, CancellationToken cancellationToken);
}

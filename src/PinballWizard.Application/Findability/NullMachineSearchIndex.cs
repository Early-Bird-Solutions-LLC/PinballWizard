namespace PinballWizard.Application.Findability;

// Null-object implementation of IMachineSearchIndex (ADR-0049 phase 2b).
// NOT registered in the DI container — the nullable-parameter pattern is used
// instead: MachineGroundingTool takes IMachineSearchIndex? and .NET DI injects
// null when AI Search is not configured, routing the tool to the Cosmos
// SearchByTitleContainsAsync safety net.
//
// This class exists for unit tests that need an explicit "no results" double
// without wiring the full AI Search stack.
public sealed class NullMachineSearchIndex : IMachineSearchIndex
{
    public Task<IReadOnlyList<MachineSearchHit>> SearchAsync(
        string query,
        int top,
        string? manufacturerKey,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MachineSearchHit>>([]);
}

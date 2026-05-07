using System.ComponentModel;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Ai.Tools;

// Foundry function tool exposed to all four Wave-2 agents per ADR-0014.
// The Microsoft Agent Framework's AIFunctionFactory.Create wraps the
// GetMachineByTitleAsync method into an AIFunction that the agent can
// invoke on demand; [Description] attributes flow into the JSON-Schema
// the model sees, so the agent prompt does not need to repeat the
// argument shape.
//
// Phase 4 RAG will introduce a sibling tool (searchCorpus) over the
// AI Search index; the agents pick the right tool per the question's
// intent. The function-tool contract is stable across phases — a
// future swap of the backing repository for an IRetriever doesn't
// change what the agent sees.
public sealed class MachineGroundingTool
{
    private readonly IMachineRepository _machines;
    private readonly ILogger<MachineGroundingTool> _logger;

    public MachineGroundingTool(IMachineRepository machines, ILogger<MachineGroundingTool> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(logger);
        _machines = machines;
        _logger = logger;
    }

    [Description("Look up a pinball machine by its title (case-insensitive). Returns the manufacturer, year, themes, designers, editions, and OPDB source URL — everything you need to ground an answer about that machine. Returns null if no machine matches the title.")]
    public async Task<MachineGroundingDto?> GetMachineByTitleAsync(
        [Description("The pinball-machine title to look up, case-insensitive (for example: 'Foo Fighters', 'Stranger Things', 'Godzilla').")] string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        Machine? match = null;
        await foreach (var machine in _machines.QueryByTitleAsync(title, cancellationToken).ConfigureAwait(false))
        {
            // Take the first match; if multiple machines share a title
            // (rare — typically only when one manufacturer re-issues an
            // old title), the agent gets the first OPDB-ordered hit.
            // PR 6 may extend the tool to return up-to-N matches if eval
            // surfaces ambiguity issues.
            match = machine;
            break;
        }

        if (match is null)
        {
            _logger.LogDebug("MachineGroundingTool: no match for title '{Title}'.", title);
            return null;
        }

        var editions = match.Editions
            .Select(e => new MachineEditionGroundingDto(
                Name: e.Name,
                Msrp: e.Msrp,
                Availability: e.Availability,
                Description: e.Description))
            .ToList();

        return new MachineGroundingDto(
            OpdbId: match.Id,
            Title: match.Title,
            Manufacturer: match.ManufacturerDisplayName,
            Year: match.Year,
            Themes: match.Themes.AsReadOnly(),
            Designers: match.Designers.AsReadOnly(),
            OpdbSourceUrl: match.OpdbSourceUrl,
            Editions: editions);
    }
}

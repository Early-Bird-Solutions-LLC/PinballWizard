using Microsoft.Agents.AI;

namespace PinballWizard.Application.Ai.Citations;

// Extracts the citations attached to a Wizard answer. Phase 4 introduces
// two impls per ADR-0022:
//
// - ToolTraceCitationExtractor (the structural one) reads citations from
//   the agent's tool-call result trace on AgentResponse.Messages. The
//   getMachineByTitle function returns a MachineGroundingDto carrying
//   OpdbSourceUrl + OpdbId; that's a citation. Connected sub-agent
//   function calls (Valuation / Rules / Repair, wired in W1-1) return
//   the sub-agent's text response which carries embedded OPDB URLs from
//   the sub-agent's own grounding — those are mined from the function
//   result's text payload.
//
// - RegexLegacyCitationExtractor (Phase 3 fallback) scans the agent's
//   final response text for OPDB machine URLs via the original regex.
//   Retained behind a config flag for cutover observability per ADR-0022
//   § Telemetry — the cutover counter compares the two extractors'
//   counts so a behavioral regression in the new one would be visible
//   before H3 rerun.
//
// AiRouter consults the primary impl for the WizardAnswer's Citations
// field and runs the legacy in parallel for the cutover counter only.
// After H2 baseline confirms parity (or improvement), the regex impl
// + flag are deleted in a follow-up PR.
public interface ICitationExtractor
{
    string SourceTag { get; }

    IReadOnlyList<Citation> Extract(AgentResponse? response);
}

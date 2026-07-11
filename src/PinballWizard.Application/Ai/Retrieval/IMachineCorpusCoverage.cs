namespace PinballWizard.Application.Ai.Retrieval;

// Answers "does the RAG index hold any chunks for this machine?" without
// running retrieval or the LLM. Used by AiRouter (ADR-0053) to skip the
// Foundry agent turn on a machine-scoped ask that could only ever refuse.
// Backed by the SAME AI Search index and machine_id filter the retriever
// uses, so a positive answer means the agent genuinely has grounding
// (e.g. a synthesized metadata card) and must run.
public interface IMachineCorpusCoverage
{
    Task<bool> HasIndexedContentAsync(string machineId, CancellationToken ct);
}

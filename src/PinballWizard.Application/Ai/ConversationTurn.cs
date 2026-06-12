namespace PinballWizard.Application.Ai;

// One completed exchange in a multi-turn Wizard conversation, supplied by
// the client with each follow-up ask. The conversation lives client-side
// (Blazor circuit component state) per the 2026-06-11 design decision:
// no server-side session store, no per-user tracking — ADR-0027's
// privacy posture holds because the server sees history only for the
// lifetime of the request that carries it.
//
// Citations ride along so the router can ground contextual follow-ups
// that answer from conversation context without re-firing a retrieval
// tool (see the inheritance block in AiRouter.ApplyPostAgentGuardrailsAsync).
public sealed record ConversationTurn(
    string Question,
    string AnswerText,
    IReadOnlyList<Citation>? Citations = null);

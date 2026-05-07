namespace PinballWizard.Application.Ai;

// In-process LRU cache at the user-question layer per ADR-0015. The
// IAiRouter checks this on every call; on hit it returns the cached
// WizardAnswer without invoking Foundry agents.
//
// Key composition is intentional: SHA-256 of (normalized_question +
// prompt_version). A prompt change implicitly invalidates by changing
// the version part of the key, so unrelated cached entries survive
// across prompt updates. Cache is per-process; ACA scale events evict.
public interface ISemanticAnswerCache
{
    bool TryGet(string normalizedQuestion, string promptVersion, out WizardAnswer answer);

    void Store(string normalizedQuestion, string promptVersion, WizardAnswer answer);
}

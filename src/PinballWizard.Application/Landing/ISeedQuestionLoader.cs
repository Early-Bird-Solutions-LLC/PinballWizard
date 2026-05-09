namespace PinballWizard.Application.Landing;

// Abstraction over the wizard_seed_questions.v1.json file load.
// Sealed by SeedQuestionLoader (Application layer, file-system read).
// Isolated as an interface so LandingServiceTests can substitute a
// fake without touching the file system.
public interface ISeedQuestionLoader
{
    Task<IReadOnlyList<SeedQuestion>> LoadAsync(CancellationToken cancellationToken);
}

using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.GameOverviews;

public interface IGameOverviewSynthesizer
{
    IReadOnlyList<Chunk> Synthesize(Machine machine);
}

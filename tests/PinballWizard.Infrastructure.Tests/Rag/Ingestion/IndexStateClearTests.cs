using PinballWizard.Infrastructure.Tests.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Ingestion;

public sealed class IndexStateClearTests
{
    [Fact]
    public async Task ClearAll_RemovesStoredHashesSoTheNextReadMisses()
    {
        var state = new InMemoryIndexState();
        await state.RecordIndexedAsync("pintips_2_G5pe4-Mpro", "G5pe4-Mpro", "abc", 1, 0, CancellationToken.None);

        var cleared = await state.ClearAllAsync(CancellationToken.None);

        Assert.Equal(1, cleared);
        Assert.Null(await state.GetLastIndexedHashAsync("pintips_2_G5pe4-Mpro", "G5pe4-Mpro", CancellationToken.None));
    }
}

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Web.Components.Shared;
using Xunit;

namespace PinballWizard.Web.Tests.Components.SharedComponents;

public sealed class DocumentListColumnsTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _repo = Substitute.For<IRawDocumentRepository>();

    public DocumentListColumnsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        _repo.StreamDocumentsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Empty());

        Services.AddSingleton(_repo);
        Services.AddSingleton<ILogger<DocumentList>>(NullLogger<DocumentList>.Instance);
    }

    // The grid renders column headers regardless of row count; an empty stream suffices.
    private static async IAsyncEnumerable<DocumentListItem> Empty(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task Grid_HasNoFormatColumn()
    {
        // Render DocumentList with MudPopoverProvider sibling (required by MudDataGrid v9).
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<DocumentList>(1);
            builder.AddAttribute(2, nameof(DocumentList.IsAdmin), true);
            builder.CloseComponent();
        });
        var cut = fragment.FindComponent<DocumentList>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // MudDataGrid renders column-header title text directly in the markup.
        // The same pattern is used by AdminColumns_HiddenOnPublicPage in DocumentListTests.
        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Format", cut.Markup));
    }
}

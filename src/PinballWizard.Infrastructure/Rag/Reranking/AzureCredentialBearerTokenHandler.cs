using Azure.Core;
using System.Net.Http.Headers;

namespace PinballWizard.Infrastructure.Rag.Reranking;

// Attaches a DefaultAzureCredential-sourced bearer token to every outbound
// request. Used by the "CohereReranker" named HttpClient so that calls to
// the Foundry external-connection proxy are authenticated with the managed
// identity (ACA) or az login session (local dev).
internal sealed class AzureCredentialBearerTokenHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _tokenContext;

    internal AzureCredentialBearerTokenHandler(TokenCredential credential, string[] scopes)
    {
        _credential = credential;
        _tokenContext = new TokenRequestContext(scopes);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _credential
            .GetTokenAsync(_tokenContext, cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

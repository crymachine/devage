using Azure.Identity;
using Microsoft.Graph;

namespace Devage.Tools.Teams.Graph;

internal static class GraphClientFactory
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    public static GraphServiceClient Create(GraphAuthConfig config)
    {
        var credential = new ClientSecretCredential(
            config.TenantId,
            config.ClientId,
            config.ClientSecret);

        return new GraphServiceClient(credential, Scopes);
    }
}

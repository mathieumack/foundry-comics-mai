using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.SignalR;

namespace MaiImageGenerator.Api.Services;

public sealed class AuthenticatedUserIdProvider(IHttpContextAccessor httpContextAccessor) : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => GetRequiredUserId();

    public string GetRequiredUserId()
    {
        var userId = httpContextAccessor.GetUserId();
        return !string.IsNullOrWhiteSpace(userId)
            ? userId
            : throw new InvalidOperationException("A user identifier claim is required to negotiate a SignalR connection.");
    }
}

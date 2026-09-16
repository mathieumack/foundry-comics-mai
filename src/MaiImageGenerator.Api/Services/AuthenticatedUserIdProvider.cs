using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace MaiImageGenerator.Api.Services;

public sealed class AuthenticatedUserIdProvider(IHttpContextAccessor httpContextAccessor) : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string GetRequiredUserId()
    {
        var userId = httpContextAccessor.GetUserId();
        return !string.IsNullOrWhiteSpace(userId)
            ? userId
            : throw new InvalidOperationException("A user identifier claim is required to negotiate a SignalR connection.");
    }
}

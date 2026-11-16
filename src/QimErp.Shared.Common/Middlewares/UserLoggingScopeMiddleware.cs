using QimErp.Shared.Common.Logging;

namespace QimErp.Shared.Common.Middlewares;

/// <summary>
/// After authentication, attaches TenantId / UserEmail / UserName to the logging scope for the remainder of the request.
/// </summary>
public sealed class UserLoggingScopeMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger _logger = loggerFactory.CreateLogger("QimErp.RequestUserContext");

    public Task InvokeAsync(HttpContext context, ICurrentUserService currentUserService)
    {
        var anonymousRequest = AnonymousRequests.ContextOf(context);
        if (AnonymousRequests.IsAnonymousEndpoint(context))
        {
            anonymousRequest ??= new AnonymousRequestContext();
            anonymousRequest.MarkAnonymous();
        }

        using (_logger.BeginUserContextScope(currentUserService, anonymousRequest))
            return _next(context);
    }
}

public static class UserLoggingScopeMiddlewareExtensions
{
    public static IApplicationBuilder UseUserLoggingScope(this IApplicationBuilder app) =>
        app.UseMiddleware<UserLoggingScopeMiddleware>();
}

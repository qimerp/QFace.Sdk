using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Diagnostics;

namespace QimErp.Shared.Common.Logging;

/// <summary>
/// A MediatR request whose SDK logs carry no caller identity (user id, email, IP, connection, correlation id).
/// Also mark its endpoint with <see cref="AnonymousRequestEndpointExtensions.AsAnonymousRequest{TBuilder}"/>: the request-start log is written before MediatR runs.
/// </summary>
public interface IAnonymousRequest
{
}

public sealed class AnonymousRequestMetadata
{
    public static readonly AnonymousRequestMetadata Instance = new();

    private AnonymousRequestMetadata()
    {
    }
}

public static class AnonymousRequestEndpointExtensions
{
    public static TBuilder AsAnonymousRequest<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AnonymousRequestMetadata.Instance);
}

/// <summary>Scoped. Once marked, SDK logging stops attaching caller identity for the rest of the scope.</summary>
public sealed class AnonymousRequestContext
{
    public bool IsAnonymous { get; private set; }

    public void MarkAnonymous() => IsAnonymous = true;
}

internal static class AnonymousRequests
{
    public static bool IsAnonymousEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<AnonymousRequestMetadata>() is not null
        || context.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata.GetMetadata<AnonymousRequestMetadata>() is not null;

    public static AnonymousRequestContext? ContextOf(HttpContext context) =>
        context.RequestServices?.GetService<AnonymousRequestContext>();

    public static bool IsAnonymous(HttpContext? context) =>
        context is not null && (IsAnonymousEndpoint(context) || ContextOf(context)?.IsAnonymous == true);

    /// <summary>Exception messages can carry identity (constraint details, input), so an anonymous request logs only the type chain and the stack trace.</summary>
    public static Exception TypeOnly(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            types.Add(current.GetType().FullName ?? current.GetType().Name);
        return ExceptionDispatchInfo.SetRemoteStackTrace(new Exception(string.Join(" --> ", types)), exception.StackTrace ?? string.Empty);
    }
}

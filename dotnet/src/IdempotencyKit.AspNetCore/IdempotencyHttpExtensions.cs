using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyKit.AspNetCore;

/// <summary>Mark an MVC controller action or controller for Standard-mode idempotency.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class IdempotentAttribute(string operationId) : Attribute
{
    public string OperationId { get; } = !string.IsNullOrWhiteSpace(operationId)
        ? operationId
        : throw new ArgumentException("Operation ID must be stable and nonempty.", nameof(operationId));
}

/// <summary>Trusted principal and tenant identifiers, resolved after authentication.</summary>
public sealed record IdempotencyCallerScope(string PrincipalId, string? TenantId = null);

public sealed class IdempotencyHttpOptions
{
    /// <summary>Stable service namespace (e.g. orders-api); required.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Must extract caller identity from authenticated server-side context.</summary>
    public Func<HttpContext, IdempotencyCallerScope>? ResolveCaller { get; set; }

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// HTTP integration uses endpoint metadata plus middleware so it executes before
/// Minimal API body binding and MVC model binding. It is not a result-only filter.
/// </summary>
public static class IdempotencyHttpExtensions
{
    public static IServiceCollection AddIdempotencyKit(
        this IServiceCollection services,
        Action<IdempotencyHttpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new IdempotencyHttpOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Namespace) || options.ResolveCaller is null)
            throw new ArgumentException("Namespace and a trusted ResolveCaller delegate are required.");
        if (options.LeaseDuration < TimeSpan.FromMilliseconds(1) ||
            options.LeaseDuration > TimeSpan.FromHours(24) ||
            options.Retention < TimeSpan.FromMilliseconds(1) ||
            options.Retention > TimeSpan.FromDays(365))
            throw new ArgumentException("LeaseDuration or Retention is outside the supported range.");

        services.AddSingleton(options);
        return services;
    }

    /// <summary>
    /// Call after routing, authentication and authorization, and before endpoint
    /// execution / MapControllers(). Captures the original JSON body pre-binding.
    /// </summary>
    public static IApplicationBuilder UseIdempotencyKit(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyMiddleware>();

    /// <summary>
    /// Opt in a Minimal API route (or any endpoint convention builder) using
    /// the exact same metadata-based engine as MVC's IdempotentAttribute.
    /// </summary>
    public static TBuilder WithIdempotency<TBuilder>(
        this TBuilder builder, string operationId) where TBuilder : IEndpointConventionBuilder
    {
        var metadata = new IdempotentAttribute(operationId);
        builder.Add(endpoint => endpoint.Metadata.Add(metadata));
        return builder;
    }
}

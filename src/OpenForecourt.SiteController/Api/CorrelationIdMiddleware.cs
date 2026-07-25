using System.Diagnostics;
using Serilog.Context;

namespace OpenForecourt.SiteController.Api;

/// <summary>
/// Ensures every request carries a correlation id — taken from the inbound
/// <c>X-Correlation-Id</c> header (so it flows from the OPT through to the host) or minted —
/// and threads it onto the trace and the log context (CLAUDE.md Phase 5, "correlation id
/// flowing from OPT through to host request").
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>The header carrying the correlation id across component boundaries.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Invokes the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var value) && !string.IsNullOrEmpty(value)
            ? value.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}

/// <summary>Reads the correlation id the middleware attached to the request.</summary>
public static class CorrelationIdAccessor
{
    /// <summary>Returns the request's correlation id, or a new one if absent.</summary>
    public static Guid Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var raw) &&
               raw is string s && Guid.TryParse(s, out var id)
            ? id
            : Guid.CreateVersion7();
    }
}

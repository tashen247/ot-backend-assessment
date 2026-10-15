using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OT.Assessment.Core.Abstractions;

namespace OT.Assessment.App.ErrorHandling;

/// <summary>
/// If the message bus is unavailable the wager was not accepted, so the caller gets a 503 and
/// can retry, rather than a generic 500.
/// </summary>
internal sealed class MessagePublishExceptionHandler : IExceptionHandler
{
    private readonly ILogger<MessagePublishExceptionHandler> _logger;

    public MessagePublishExceptionHandler(ILogger<MessagePublishExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not MessagePublishException)
        {
            return false;
        }

        _logger.LogError(exception, "Failed to publish casino wager");

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "The wager could not be accepted right now.",
            Detail = "The message bus is unavailable. Retry the request.",
            Instance = httpContext.Request.Path
        }, cancellationToken);

        return true;
    }
}

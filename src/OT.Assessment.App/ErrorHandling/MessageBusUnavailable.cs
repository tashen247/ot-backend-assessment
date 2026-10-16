using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OT.Assessment.Core.Abstractions;

namespace OT.Assessment.App.ErrorHandling;

/// <summary>
/// The response when a wager could not be handed to the message bus: a 503 with a Retry-After, so
/// the caller knows the wager was not accepted and when to try again, rather than a generic 500.
/// <para>
/// This is an expected outcome of posting a wager, so the controller returns it directly instead of
/// letting the exception reach the exception-handler middleware. In .NET 8 that middleware logs every
/// exception it sees as an error with a stack trace, which during an outage, with the circuit open,
/// would mean one error per refused request.
/// </para>
/// </summary>
internal static class MessageBusUnavailable
{
    private const int DefaultRetryAfterSeconds = 5;

    public static IActionResult Result(HttpContext httpContext, MessagePublishException exception, ILogger logger)
    {
        if (exception.RetryAfter is null)
        {
            logger.LogError(exception, "Failed to publish casino wager");
        }
        else
        {
            // The circuit is open. The publisher logged a warning when it opened; one line per refused request would be noise.
            logger.LogDebug("Casino wager refused while the publish circuit is open");
        }

        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds(exception.RetryAfter).ToString(CultureInfo.InvariantCulture);

        return new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "The wager could not be accepted right now.",
            Detail = "The message bus is unavailable. Retry the request.",
            Instance = httpContext.Request.Path
        })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>Whole seconds, rounded up and at least 1, as Retry-After requires.</summary>
    internal static int RetryAfterSeconds(TimeSpan? retryAfter) =>
        retryAfter is { } wait ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : DefaultRetryAfterSeconds;
}

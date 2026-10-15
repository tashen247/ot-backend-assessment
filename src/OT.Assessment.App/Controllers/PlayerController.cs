using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using OT.Assessment.App.Contracts;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Models;

namespace OT.Assessment.App.Controllers;

/// <summary>Player casino wager capture and queries.</summary>
[ApiController]
[Route("api/player")]
[Produces("application/json")]
public sealed class PlayerController : ControllerBase
{
    public const int MaxPageSize = 100;
    public const int MaxTopSpenders = 100;

    private readonly ICasinoWagerPublisher _publisher;
    private readonly IPlayerReadRepository _players;

    public PlayerController(ICasinoWagerPublisher publisher, IPlayerReadRepository players)
    {
        _publisher = publisher;
        _players = players;
    }

    /// <summary>Accepts a casino wager event and publishes it to the message bus for storage.</summary>
    /// <remarks>
    /// Returns once the broker has durably stored the message; the wager is written to the
    /// database asynchronously by OT.Assessment.Consumer.
    /// </remarks>
    /// <response code="200">The wager was accepted.</response>
    /// <response code="400">The wager failed validation.</response>
    /// <response code="503">The message bus is unavailable; retry.</response>
    [HttpPost("casinowager")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostCasinoWager([FromBody] CasinoWagerRequest request, CancellationToken cancellationToken)
    {
        await _publisher.PublishAsync(request.ToMessage(), cancellationToken);
        return Ok();
    }

    /// <summary>Returns a player's casino wagers, latest first.</summary>
    /// <param name="playerId">The player's account id.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Wagers per page (1-100).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("{playerId:guid}/casino")]
    [ProducesResponseType(typeof(PagedResult<PlayerWager>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<PlayerWager>>> GetCasinoWagers(
        Guid playerId,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, MaxPageSize)] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _players.GetCasinoWagersAsync(playerId, page, pageSize, cancellationToken));
    }

    /// <summary>Returns the players with the highest total amount wagered, highest first.</summary>
    /// <param name="count">Number of players to return (1-100).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("topSpenders")]
    [ProducesResponseType(typeof(IReadOnlyList<TopSpender>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<TopSpender>>> GetTopSpenders(
        [FromQuery, Range(1, MaxTopSpenders)] int count = 10,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _players.GetTopSpendersAsync(count, cancellationToken));
    }
}

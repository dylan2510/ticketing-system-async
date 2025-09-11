using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Ticketing.Reservation.Api.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly IConnectionMultiplexer _redis;

    public AdminController(IConnectionMultiplexer redis) => _redis = redis;

    /// <summary>
    /// Seed stock for an event (dev helper).
    /// </summary>
    [HttpPost("events/{eventId}/seed/{stock:int}")]
    public async Task<IActionResult> Seed([FromRoute] string eventId, [FromRoute] int stock)
    {
        if (string.IsNullOrWhiteSpace(eventId) || stock < 0)
            return BadRequest();

        await _redis.GetDatabase().StringSetAsync($"stock:{eventId}", stock);
        return Ok(new { eventId, stock });
    }
}

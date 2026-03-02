using System.Security.Claims;
using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Controllers;

[ApiController]
[Route("reviews")]
public class ReviewsController(AppDbContext db) : ControllerBase
{
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserReview review)
    {
        review.ReviewerId = GetUserId();
        db.UserReviews.Add(review);
        await db.SaveChangesAsync();
        return Ok(review);
    }

    [HttpGet("user/{userId}")]
    public async Task<IActionResult> GetByUser(int userId) =>
        Ok(await db.UserReviews.Where(r => r.TargetUserId == userId).ToListAsync());

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}

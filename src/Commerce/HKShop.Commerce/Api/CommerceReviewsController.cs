using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace HKShop.Commerce.Api;

[Route("api/commerce/reviews")]
[Authorize(Policy = ShopPolicies.CommerceCustomer)]
public sealed class CommerceReviewsController(ShopRequest request, CatalogService catalog) : Controller
{
    [HttpPost("{slug}"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Review([FromRoute] string slug, [FromForm] int rating, [FromForm] string? content)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var productSlug = await catalog.SubmitReview(await request.Actor(), slug, content, rating);
        TempData["Notice"] = "Review submitted.";
        return LocalRedirect($"/products/{Uri.EscapeDataString(productSlug)}#reviews");
    }

    [HttpPost("{slug}/{reviewId:long}"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> EditReview([FromRoute] string slug, [FromRoute] long reviewId,
        [FromForm] int rating, [FromForm] string? content, [FromForm] int reviewPage)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var productSlug = await catalog.EditReview(await request.Actor(), slug, reviewId, content, rating);
        TempData["Notice"] = "Review changes saved.";
        var pageUrl = QueryHelpers.AddQueryString($"/products/{Uri.EscapeDataString(productSlug)}", "reviewPage", reviewPage.ToString());
        return LocalRedirect($"{pageUrl}#review-{reviewId}");
    }
}

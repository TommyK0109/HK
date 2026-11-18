using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/marketplace")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficeMarketplaceController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("offers/{offerId:long}")]
    public async Task<IActionResult> ReviewOffer([FromRoute] long offerId, [FromForm] string decision)
    {
        var approved = ParseDecision(decision);
        await commerce.ReviewOfferAsync(await request.Actor(), offerId, approved);
        TempData["Notice"] = approved ? "Offer approved." : "Offer rejected.";
        return Redirect("/eoffice/marketplace");
    }

    [HttpPost("proposals/{proposalId:long}")]
    public async Task<IActionResult> ReviewProposal([FromRoute] long proposalId, [FromForm] string decision)
    {
        var approved = ParseDecision(decision);
        var productId = await commerce.ReviewProposalAsync(await request.Actor(), proposalId, approved);
        if (productId is int id)
        {
            TempData["Notice"] = "Proposal approved and draft product created.";
            return Redirect($"/eoffice/products/{id}");
        }
        TempData["Notice"] = "Proposal rejected.";
        return Redirect("/eoffice/marketplace#proposals");
    }

    private static bool ParseDecision(string? decision) => decision switch
    {
        "approve" => true,
        "reject" => false,
        _ => throw new ShopRuleException("Select a valid review decision.")
    };
}

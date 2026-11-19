using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/store-applications")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficeStoreApplicationsController(IStoreApplicationReview applications, IShopRequestContext request) : Controller
{
    [HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decide(Guid id, [FromForm] string decision, [FromForm] string? reason)
    {
        if (!ModelState.IsValid || decision is not ("approve" or "decline"))
            throw new ShopRuleException("Choose approve or decline.");
        await applications.Decide(await request.Actor(), id, decision == "approve", reason);
        TempData["Notice"] = "Decision recorded and notification queued.";
        return LocalRedirect($"/eoffice/sellers/applications/{id:D}");
    }

    [HttpGet("{id:guid}/documents/{kind}")]
    public async Task<IActionResult> Document(Guid id, StoreDocumentKind kind)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(kind)) return NotFound();
        var document = await applications.Document(await request.Actor(), id, kind);
        if (document is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        var extension = document.ContentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            _ => "pdf"
        };
        return File(document.Data, document.ContentType, $"{kind}.{extension}");
    }
}

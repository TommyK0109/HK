using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/catalog")]
[Authorize(Policy = ShopPolicies.EOfficeStaff)]
public sealed class EOfficeCatalogController(IShopRequestContext request, ICatalogOperationsPort catalog) : Controller
{
    private const int MaxImageBytes = 5 * 1024 * 1024;

    [HttpPost("category")]
    public async Task<IActionResult> Category(long id, string name, bool active)
    {
        RequireForm();
        await catalog.SaveCategoryAsync(await request.Actor(), id, name, active);
        return Updated("/eoffice/products");
    }

    [HttpPost("product")]
    public async Task<IActionResult> Product(int id, string name, string slug, string? description, long? categoryId)
    {
        RequireForm();
        var saved = await catalog.SaveProductAsync(await request.Actor(), id, name, slug, description ?? "", categoryId);
        return Updated($"/eoffice/products/{saved}");
    }

    [HttpPost("sku")]
    public async Task<IActionResult> Sku(int productId, long skuId, string code, string? size, string? color, decimal price, bool active)
    {
        RequireForm();
        await catalog.SaveSkuAsync(await request.Actor(), productId, skuId, code, size ?? "", color ?? "", price, active);
        return Updated($"/eoffice/products/{productId}");
    }

    [HttpPost("image"), RequestSizeLimit(MaxImageBytes + 65_536)]
    public async Task<IActionResult> Image(int productId, string alt, IFormFile image)
    {
        RequireForm();
        if (image is null || image.Length == 0 || image.Length > MaxImageBytes)
            throw new ShopRuleException("Maximum file size is 5 MB.");
        await using var source = image.OpenReadStream();
        await catalog.AddImageAsync(await request.Actor(), productId, alt, source);
        return Updated($"/eoffice/products/{productId}");
    }

    [HttpPost("publication")]
    public async Task<IActionResult> Publication(int id, bool publish)
    {
        RequireForm();
        await catalog.ChangePublicationAsync(await request.Actor(), id, publish);
        return Updated($"/eoffice/products/{id}");
    }

    private IActionResult Updated(string path)
    {
        TempData["Notice"] = "Saved changes.";
        return Redirect(path);
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
    }
}

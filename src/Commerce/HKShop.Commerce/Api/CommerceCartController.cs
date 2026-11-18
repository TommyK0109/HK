using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Route("api/commerce/cart")]
public sealed class CommerceCartController(ShopRequest request, CartService cart) : Controller
{
    [HttpPost(""), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Cart(long? offerId, long? skuId, int quantity, bool add, bool remove)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var actor = await request.Actor();
        if (actor.IsEmployee)
            throw new ShopRuleException("Employee and shipper accounts cannot use shopping features.");
        if ((offerId.HasValue == skuId.HasValue) || offerId is <= 0 || skuId is <= 0)
            throw new ShopRuleException("Please select a product in your cart.");
        var user = request.UserId;
        var guest = user is null ? request.GuestHash(create: true) : null;
        if (offerId is long selectedOffer)
            await cart.SetOffer(user, guest, selectedOffer, remove ? 0 : quantity, add);
        else if (skuId is long selectedSku)
            await cart.Set(user, guest, selectedSku, remove ? 0 : quantity, add);
        TempData["Notice"] = add ? "Added to cart." : "Updated cart.";
        return Redirect("/cart");
    }

    [HttpPost("buy-again"), Authorize(Policy = ShopPolicies.CommerceCustomer)]
    public async Task<IActionResult> BuyAgain(Guid id)
    {
        var adjusted = await cart.BuyAgain(await request.Actor(), id);
        TempData["Notice"] = adjusted > 0
            ? "Added product back to cart. Some quantities were adjusted according to inventory."
            : "Added product back to cart.";
        return Redirect("/cart");
    }
}

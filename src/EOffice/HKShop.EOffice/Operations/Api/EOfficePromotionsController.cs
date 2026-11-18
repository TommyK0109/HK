using System.Globalization;
using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/promotions")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficePromotionsController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost]
    public async Task<IActionResult> Promotion(long id, string code, bool active, bool percentage, string value, decimal minimum,
        int cap, string starts, string ends)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        if (!VietnamDate(starts, out var start) || !VietnamDate(ends, out var end))
            throw new ShopRuleException("Invalid date format.");
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var discount))
            throw new ShopRuleException("Invalid discount value.");
        await commerce.SavePromotionAsync(await request.Actor(), id, code, active, percentage, discount, minimum, cap, start, end);
        TempData["Notice"] = "Saved changes.";
        return Redirect("/eoffice/promotions");
    }

    private static bool VietnamDate(string value, out DateTimeOffset date)
    {
        date = default;
        if (!DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        date = new DateTimeOffset(time, TimeSpan.FromHours(7));
        return true;
    }
}

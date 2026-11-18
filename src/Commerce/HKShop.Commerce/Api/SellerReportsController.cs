using System.Globalization;
using System.Text;
using HKShop.Application;
using HKShop.Application.Reporting;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/reports")]
public sealed class SellerReportsController(ShopRequest request, ReportingService reports) : Controller
{
    [HttpGet("export")]
    [HttpGet("/seller/{sellerId:long}/reports/export")]
    public async Task<IActionResult> Export([FromRoute] long sellerId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        if (!ModelState.IsValid || from is null || to is null)
            throw new ShopRuleException("The report date range is invalid.");

        var sales = await reports.SellerSales(await request.Actor(), sellerId, from.Value, to.Value);
        var csv = new StringBuilder("Metric,Value\r\n");
        csv.Append(CultureInfo.InvariantCulture, $"Collected,{sales.Collected}\r\nRefunded,{sales.Refunded}\r\nNet,{sales.Net}\r\n");
        foreach (var group in sales.Orders)
            csv.Append(CultureInfo.InvariantCulture, $"{group.Status},{group.Count}\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv; charset=utf-8", $"hkshop-seller-{sellerId}-sales.csv");
    }
}

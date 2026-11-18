using System.Globalization;
using System.Text;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/reports")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficeReportsController(
    IShopRequestContext request,
    ICommerceOperationsPort commerce,
    ICatalogOperationsPort catalog) : Controller
{
    [HttpGet("sales.csv")]
    public async Task<IActionResult> SalesCsv(DateOnly from, DateOnly to)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var sales = await commerce.SalesReportAsync(await request.Actor(), from, to);
        var csv = new StringBuilder("Chi so,Gia tri\r\n");
        CsvRow(csv, "Collected", sales.Collected.ToString(CultureInfo.InvariantCulture));
        CsvRow(csv, "Refunded", sales.Refunded.ToString(CultureInfo.InvariantCulture));
        CsvRow(csv, "Net", sales.Net.ToString(CultureInfo.InvariantCulture));
        foreach (var group in sales.Orders) CsvRow(csv, group.Status.ToString(), group.Count.ToString(CultureInfo.InvariantCulture));
        return CsvFile(csv, "hkshop-sales.csv");
    }

    [HttpGet("stock.csv")]
    public async Task<IActionResult> StockCsv()
    {
        var actor = await request.Actor();
        actor.RequireAdmin();
        var csv = new StringBuilder("SKU,San pham,Ton,Giu,Co the ban\r\n");
        var page = 1;
        Page<StockRow> stock;
        do
        {
            stock = await catalog.ListFirstPartyStockAsync(actor, low: true, page++);
            foreach (var row in stock.Items)
                CsvRow(csv, row.Code, row.Product, row.OnHand.ToString(), row.Reserved.ToString(), row.Available.ToString());
        } while (page <= stock.Last);
        return CsvFile(csv, "hkshop-low-stock.csv");
    }

    private FileContentResult CsvFile(StringBuilder csv, string name) =>
        File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", name);

    private static void CsvRow(StringBuilder csv, params string[] values) =>
        csv.AppendJoin(',', values.Select(value =>
        {
            if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r'))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        })).Append("\r\n");
}

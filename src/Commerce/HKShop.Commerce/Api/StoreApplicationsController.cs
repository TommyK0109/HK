using System.Globalization;
using System.Text;
using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.VisualBasic.FileIO;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceCustomer)]
[Route("api/commerce/seller/applications")]
public sealed class StoreApplicationsController(StoreApplications applications, IShopRequestContext request) : Controller
{
    private const string SettingsPath = "/account/store";
    private const int MaxFileBytes = 5_000_000;

    [HttpPost, RequestSizeLimit(4 * MaxFileBytes + 65_536), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Submit([FromForm] Guid id, [FromForm] string storeName, [FromForm] string nationalId,
        [FromForm] string businessLicenseNumber, [FromForm] string[] productNames, [FromForm] string[] prices,
        IFormFile? productsCsv, IFormFile? nationalIdFront, IFormFile? nationalIdBack, IFormFile? businessLicense)
    {
        var actor = await request.Actor();
        actor.RequireCustomer();
        if (!ModelState.IsValid) throw new ShopRuleException("Complete all required application fields.");
        var products = productsCsv is null ? ManualProducts(productNames, prices) : CsvProducts(productsCsv, productNames, prices);
        var documents = new Dictionary<StoreDocumentKind, StoreApplicationDocument>
        {
            [StoreDocumentKind.NationalIdFront] = await ReadDocument(nationalIdFront, "National ID front"),
            [StoreDocumentKind.NationalIdBack] = await ReadDocument(nationalIdBack, "National ID back"),
            [StoreDocumentKind.BusinessLicense] = await ReadDocument(businessLicense, "Business license", allowPdf: true)
        };
        await applications.Submit(actor, id, storeName, nationalId, businessLicenseNumber, products, documents);
        TempData["Notice"] = "Store application submitted for EOffice review.";
        return LocalRedirect(SettingsPath);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id)
    {
        await applications.MarkRead(await request.Actor(), id);
        return LocalRedirect(SettingsPath);
    }

    [HttpPost("/api/commerce/seller/{sellerId:long}/close"), Authorize(Policy = ShopPolicies.CommerceSeller)]
    public async Task<IActionResult> Close(long sellerId, [FromForm] bool confirm)
    {
        if (!confirm) throw new ShopRuleException("Confirm that you want to close this store.");
        await applications.Close(await request.Actor(), sellerId);
        TempData["Notice"] = "Store closed and verification documents removed.";
        return LocalRedirect($"/seller/{sellerId}");
    }

    private static List<StoreApplicationProduct> ManualProducts(string[] names, string[] prices)
    {
        if (names.Length != prices.Length) throw new ShopRuleException("Enter a price for each product.");
        var products = new List<StoreApplicationProduct>();
        for (var index = 0; index < names.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(names[index]) && string.IsNullOrWhiteSpace(prices[index])) continue;
            products.Add(ParseProduct(names[index], prices[index], $"Product {index + 1}"));
        }
        return products;
    }

    private static List<StoreApplicationProduct> CsvProducts(IFormFile file, string[] names, string[] prices)
    {
        if (names.Concat(prices).Any(value => !string.IsNullOrWhiteSpace(value)))
            throw new ShopRuleException("Enter products manually or upload a CSV. Choose one method.");
        if (file.Length is <= 0 or > MaxFileBytes || !Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new ShopRuleException("Upload a CSV file up to 5 MB.");

        using var stream = file.OpenReadStream();
        using var csv = new TextFieldParser(stream, new UTF8Encoding(false, true), detectEncoding: false);
        csv.SetDelimiters(",");
        var products = new List<StoreApplicationProduct>();
        try
        {
            var headers = csv.ReadFields();
            if (headers is not { Length: 2 } || headers[0].TrimStart('\uFEFF') != "product_name" || headers[1] != "price_vnd")
                throw new ShopRuleException("Use the CSV headers product_name,price_vnd.");

            while (!csv.EndOfData)
            {
                var line = csv.LineNumber;
                var row = csv.ReadFields();
                if (row is not { Length: 2 }) throw new ShopRuleException($"CSV line {line} needs a product name and price.");
                products.Add(ParseProduct(row[0], row[1], $"CSV line {line}"));
            }
        }
        catch (MalformedLineException)
        {
            throw new ShopRuleException($"Invalid CSV at line {csv.ErrorLineNumber}.");
        }
        catch (DecoderFallbackException)
        {
            throw new ShopRuleException("Save the CSV with UTF-8 encoding.");
        }
        return products;
    }

    private static StoreApplicationProduct ParseProduct(string? name, string? price, string label)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150 ||
            !decimal.TryParse(price?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value is <= 0 or > 999_999_999_999_999_999m)
            throw new ShopRuleException($"{label} requires a name up to 150 characters and a positive whole VND price.");
        return new StoreApplicationProduct(name, value);
    }

    private async Task<StoreApplicationDocument> ReadDocument(IFormFile? file, string label, bool allowPdf = false)
    {
        if (file is null || file.Length is <= 0 or > MaxFileBytes)
            throw new ShopRuleException($"{label} is required and must be at most 5 MB.");
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, HttpContext.RequestAborted);
        var data = buffer.ToArray();
        var type = data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : data.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : allowPdf && data.AsSpan().StartsWith("%PDF-"u8) ? "application/pdf" : null;
        if (type is null) throw new ShopRuleException($"{label} must be a JPEG, PNG{(allowPdf ? ", or PDF" : "")}.");
        return new StoreApplicationDocument(data, type);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HKShop.Application.Transportation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Shipping.Api;

[Route("payments/transport")]
public sealed class TransportBankController(TransportSettings settings, TransportService transport) : Controller
{
    private static readonly Regex Reference = new(@"(?<![A-Z0-9])HK[DR][0-9A-F]{32}(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [HttpPost("bank-webhook"), AllowAnonymous, IgnoreAntiforgeryToken, RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Receipt([FromBody] JsonElement body)
    {
        if (settings.BankWebhookKey.Length < 24 || settings.BankAccount.Length == 0 ||
            !Request.Headers.TryGetValue("Authorization", out var value) || value.Count != 1 ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value.ToString()),
                Encoding.UTF8.GetBytes($"Apikey {settings.BankWebhookKey}")))
            return Unauthorized(new { success = false });
        if (!ModelState.IsValid || body.ValueKind != JsonValueKind.Object) return BadRequest(new { success = false });
        if (String(body, "transferType") != "in" || String(body, "accountNumber") != settings.BankAccount)
            return Ok(new { success = true });
        if (!body.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var transaction) ||
            transaction <= 0 || !body.TryGetProperty("transferAmount", out var valueAmount) ||
            valueAmount.ValueKind != JsonValueKind.Number || !valueAmount.TryGetDecimal(out var amount) ||
            amount <= 0 || amount != decimal.Truncate(amount) || amount >= 1_000_000_000_000_000_000m)
            return BadRequest(new { success = false });

        var references = Reference.Matches(String(body, "code") + " " + String(body, "content"))
            .Select(match => match.Value.ToUpperInvariant()).Distinct().ToArray();
        await transport.RecordBankReceipt(transaction.ToString(CultureInfo.InvariantCulture),
            references.Length == 1 ? references[0] : "", amount);
        return Ok(new { success = true });
    }

    private static string String(JsonElement body, string name) =>
        body.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
}

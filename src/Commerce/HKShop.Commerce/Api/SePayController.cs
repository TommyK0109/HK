using System.Text.Json;
using HKShop.Application.Payments;
using HKShop.Contracts.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HKShop.Commerce.Api;

[Route("payments/sepay")]
public sealed class SePayController(ISePayWebhookHandler gateway, PaymentService payments, ILogger<SePayController> logger) : Controller
{
    [HttpPost("ipn"), AllowAnonymous, IgnoreAntiforgeryToken, SkipStatusCodePages, RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> Ipn([FromBody] JsonElement body)
    {
        if (!Request.Headers.TryGetValue("X-Secret-Key", out var secret) || secret.Count != 1 || !gateway.Authenticate(secret.ToString()))
            return Unauthorized();
        if (!ModelState.IsValid || body.ValueKind != JsonValueKind.Object) return BadRequest();
        var kind = gateway.ReadString(body, "notification_type");
        if (kind == "TRANSACTION_VOID")
        {
            if (body.TryGetProperty("order", out var order))
                await payments.FlagVoid(gateway.ReadString(order, "order_invoice_number"));
            return Ok(new { success = true });
        }
        if (kind != "ORDER_PAID") return Ok(new { success = true });
        var outcome = gateway.ParsePayment(body);
        if (outcome is null || !await payments.Apply(outcome))
        {
            logger.LogWarning("SePay payment notification could not be matched ({TraceId}).", HttpContext.TraceIdentifier);
            return BadRequest(new { success = false });
        }
        logger.LogInformation("SePay outcome processed for {OrderNumber} ({TraceId}).", outcome.Invoice, HttpContext.TraceIdentifier);
        return Ok(new { success = true });
    }

    [HttpGet("return/{id:guid}"), Authorize]
    public IActionResult Return(Guid id) => Redirect($"/orders/{id:D}?paymentReturn=true");
}

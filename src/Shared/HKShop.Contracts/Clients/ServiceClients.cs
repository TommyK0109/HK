using System.Net;
using System.Net.Http.Json;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Transportation;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.Contracts.Clients;

public sealed class ShippingClientOptions
{
    public bool Enabled { get; set; }
}

public static class ServiceClientDefaults
{
    internal static async Task EnsureSuccessOrThrowRuleAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            ProblemDetails? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: cancellationToken);
            }
            catch
            {
                // Fall back to raw text if the body is not ProblemDetails JSON.
            }

            if (!string.IsNullOrWhiteSpace(problem?.Detail))
                throw new ShopRuleException(problem.Detail);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body))
                throw new ShopRuleException(body);
        }

        response.EnsureSuccessStatusCode();
    }
}

public abstract class ServiceHttpClientBase(HttpClient http)
{
    protected HttpClient Http { get; } = http;

    protected async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(path, cancellationToken);
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken))!;
    }

    protected async Task<T?> GetOrDefaultAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    protected async Task PostAsync<TRequest>(string path, TRequest payload, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync(path, payload, cancellationToken);
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, cancellationToken);
    }

    protected async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsJsonAsync(path, payload, cancellationToken);
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken))!;
    }

    protected async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await Http.DeleteAsync(path, cancellationToken);
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, cancellationToken);
    }
}

// ---------------------------------------------------------------------------
// Shipping DTOs & Client
// ---------------------------------------------------------------------------
public sealed record QuoteRoutesRequest(
    TransportPoint Destination,
    IReadOnlyList<SellerRouteOrigin> Origins,
    IReadOnlyList<TransportRoute>? AcceptedRoutes,
    bool QuoteNewRoutes);

public sealed record RegisterParcelsRequest(IReadOnlyList<NewTransportParcel> Parcels);
public sealed record ParcelSummariesRequest(IReadOnlyCollection<Guid> SellerOrderIds, bool RedactDeliveryCode);
public sealed record SetSellerLocationRequest(string Address, double Latitude, double Longitude);
public sealed record SellerParcelCommandRequest(long SellerId);
public sealed record ConfirmHandoffRequest(long SellerId, string Code);
public sealed record CancelParcelRequest(DateTimeOffset Now);
public sealed record ConfigureShipperRequest(string UserId, string Email, bool Active);
public sealed record RecordShipperPayoutRequest(string Reference);
public sealed record ResolveBankReceiptRequest(string TransactionId, string? Reference, string? Resolution);
public sealed record ShipperPresenceRequest(bool OnDuty, double? Latitude, double? Longitude);
public sealed record ShipperRespondRequest(Guid SellerOrderId, bool Accept);
public sealed record ShipperDeliverRequest(string Code, DoorPayment? Method, decimal? Cash);
public sealed record ShipperReturnRequest(string Issue);
public sealed record OrderIdResult(Guid OrderId);
public sealed record BooleanResult(bool Value);

public sealed class ShippingServiceClient(HttpClient http) : ServiceHttpClientBase(http)
{
    public Task<IReadOnlyList<TransportRoute>> QuoteRoutesAsync(QuoteRoutesRequest request, CancellationToken ct = default) =>
        PostAsync<QuoteRoutesRequest, IReadOnlyList<TransportRoute>>("/internal/shipping/routes/quote", request, ct);

    public Task<Dictionary<Guid, TransportParcelSummary>> RegisterParcelsAsync(RegisterParcelsRequest request, CancellationToken ct = default) =>
        PostAsync<RegisterParcelsRequest, Dictionary<Guid, TransportParcelSummary>>("/internal/shipping/parcels/register", request, ct);

    public Task<Dictionary<Guid, TransportParcelSummary>> GetParcelSummariesAsync(ParcelSummariesRequest request, CancellationToken ct = default) =>
        PostAsync<ParcelSummariesRequest, Dictionary<Guid, TransportParcelSummary>>("/internal/shipping/parcels/summaries", request, ct);

    public Task<SellerLocationSummary?> GetSellerLocationAsync(long sellerId, CancellationToken ct = default) =>
        GetOrDefaultAsync<SellerLocationSummary>($"/internal/shipping/sellers/{sellerId}/location", ct);

    public Task SetSellerLocationAsync(long sellerId, SetSellerLocationRequest request, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/sellers/{sellerId}/location", request, ct);

    public async Task<Guid> MarkParcelPackedAsync(long sellerId, Guid sellerOrderId, CancellationToken ct = default) =>
        (await PostAsync<SellerParcelCommandRequest, OrderIdResult>($"/internal/shipping/parcels/{sellerOrderId:D}/pack", new(sellerId), ct)).OrderId;

    public async Task<Guid> RequestParcelPickupAsync(long sellerId, Guid sellerOrderId, CancellationToken ct = default) =>
        (await PostAsync<SellerParcelCommandRequest, OrderIdResult>($"/internal/shipping/parcels/{sellerOrderId:D}/request-pickup", new(sellerId), ct)).OrderId;

    public async Task<Guid> ConfirmParcelHandoffAsync(long sellerId, Guid sellerOrderId, string code, CancellationToken ct = default) =>
        (await PostAsync<ConfirmHandoffRequest, OrderIdResult>($"/internal/shipping/parcels/{sellerOrderId:D}/confirm-handoff", new(sellerId, code), ct)).OrderId;

    public Task CancelOrderParcelsAsync(Guid sellerOrderId, DateTimeOffset now, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/parcels/{sellerOrderId:D}/cancel", new CancelParcelRequest(now), ct);

    public async Task<bool> HasUnverifiedCashRemittanceAsync(Guid sellerOrderId, CancellationToken ct = default) =>
        (await GetAsync<BooleanResult>($"/internal/shipping/parcels/{sellerOrderId:D}/unverified-cash", ct)).Value;

    public Task CompleteReturnedParcelAsync(Guid sellerOrderId, DateTimeOffset now, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/parcels/{sellerOrderId:D}/complete-return", new CancelParcelRequest(now), ct);

    public Task ConfigureShipperAsync(ConfigureShipperRequest request, CancellationToken ct = default) =>
        PostAsync("/internal/shipping/shippers/configure", request, ct);

    public Task ReassignAsync(Guid sellerOrderId, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/parcels/{sellerOrderId:D}/reassign", new { }, ct);

    public Task ResetDeliveryConfirmationAsync(Guid sellerOrderId, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/parcels/{sellerOrderId:D}/reset-delivery-code", new { }, ct);

    public Task RecordShipperPayoutAsync(Guid sellerOrderId, string reference, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/parcels/{sellerOrderId:D}/payout", new RecordShipperPayoutRequest(reference), ct);

    public Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TransportOperationRow>>("/internal/shipping/operations", ct);

    public Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TransportBankExceptionRow>>("/internal/shipping/bank-exceptions", ct);

    public Task ResolveBankReceiptAsync(ResolveBankReceiptRequest request, CancellationToken ct = default) =>
        PostAsync("/internal/shipping/bank-exceptions/resolve", request, ct);

    public Task<ShipperStateDto> GetShipperStateAsync(CancellationToken ct = default) =>
        GetAsync<ShipperStateDto>("/internal/shipping/shipper/state", ct);

    public Task UpdateShipperPresenceAsync(ShipperPresenceRequest request, CancellationToken ct = default) =>
        PostAsync("/internal/shipping/shipper/presence", request, ct);

    public Task RespondAssignmentAsync(Guid assignmentId, ShipperRespondRequest request, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/shipper/assignments/{assignmentId:D}/respond", request, ct);

    public async Task<bool> DeliverParcelAsync(Guid sellerOrderId, ShipperDeliverRequest request, CancellationToken ct = default) =>
        (await PostAsync<ShipperDeliverRequest, BooleanResult>($"/internal/shipping/shipper/parcels/{sellerOrderId:D}/deliver", request, ct)).Value;

    public Task ReturnParcelToSenderAsync(Guid sellerOrderId, ShipperReturnRequest request, CancellationToken ct = default) =>
        PostAsync($"/internal/shipping/shipper/parcels/{sellerOrderId:D}/return", request, ct);

    public Task<IReadOnlyList<AuditEntryDto>> AuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuditEntryDto>>($"/internal/shipping/reports/audit?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct);
}

public sealed class HttpShippingGateway(ShippingServiceClient client, ShippingClientOptions options) : IShippingGateway, IShippingAdminPort, IShippingShipperPort
{
    public bool Enabled => options.Enabled;

    public Task<IReadOnlyList<TransportRoute>> QuoteRoutesAsync(
        TransportPoint destination,
        IReadOnlyList<SellerRouteOrigin> origins,
        IReadOnlyList<TransportRoute>? acceptedRoutes,
        bool quoteNewRoutes,
        CancellationToken cancellationToken = default) =>
        client.QuoteRoutesAsync(new(destination, origins, acceptedRoutes, quoteNewRoutes), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> RegisterParcelsAsync(
        IShopSession db,
        IReadOnlyList<NewTransportParcel> parcels,
        CancellationToken cancellationToken = default) =>
        await client.RegisterParcelsAsync(new(parcels), cancellationToken);

    public Task<SellerLocationSummary?> GetSellerLocationAsync(ShopActor actor, long sellerId) =>
        client.GetSellerLocationAsync(sellerId);

    public Task SetSellerLocationAsync(ShopActor actor, long sellerId, string address, double latitude, double longitude) =>
        client.SetSellerLocationAsync(sellerId, new(address, latitude, longitude));

    public Task<Guid> MarkParcelPackedAsync(ShopActor actor, long sellerId, Guid sellerOrderId) =>
        client.MarkParcelPackedAsync(sellerId, sellerOrderId);

    public Task<Guid> RequestParcelPickupAsync(ShopActor actor, long sellerId, Guid sellerOrderId) =>
        client.RequestParcelPickupAsync(sellerId, sellerOrderId);

    public Task<Guid> ConfirmParcelHandoffAsync(ShopActor actor, long sellerId, Guid sellerOrderId, string code) =>
        client.ConfirmParcelHandoffAsync(sellerId, sellerOrderId, code);

    public Task CancelOrderParcelsAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now) =>
        client.CancelOrderParcelsAsync(sellerOrderId, now);

    public async Task<IReadOnlyDictionary<Guid, TransportParcelSummary>> GetParcelSummariesAsync(
        IShopSession db,
        IReadOnlyCollection<Guid> sellerOrderIds,
        bool redactDeliveryCode = false) =>
        sellerOrderIds.Count == 0
            ? new Dictionary<Guid, TransportParcelSummary>()
            : await client.GetParcelSummariesAsync(new(sellerOrderIds, redactDeliveryCode));

    public Task<bool> HasUnverifiedCashRemittanceAsync(IShopSession db, Guid sellerOrderId) =>
        client.HasUnverifiedCashRemittanceAsync(sellerOrderId);

    public Task CompleteReturnedParcelAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now) =>
        client.CompleteReturnedParcelAsync(sellerOrderId, now);

    public Task ConfigureShipperAsync(IShopSession db, ShopActor actor, string userId, string email, bool active) =>
        client.ConfigureShipperAsync(new(userId, email, active));

    public Task ReassignAsync(ShopActor actor, Guid sellerOrderId) =>
        client.ReassignAsync(sellerOrderId);

    public Task ResetDeliveryConfirmationAsync(ShopActor actor, Guid sellerOrderId) =>
        client.ResetDeliveryConfirmationAsync(sellerOrderId);

    public Task RecordShipperPayoutAsync(ShopActor actor, Guid sellerOrderId, string reference) =>
        client.RecordShipperPayoutAsync(sellerOrderId, reference);

    public Task<IReadOnlyList<TransportOperationRow>> OperationsAsync(ShopActor actor) =>
        client.OperationsAsync();

    public Task<IReadOnlyList<TransportBankExceptionRow>> BankExceptionsAsync(ShopActor actor) =>
        client.BankExceptionsAsync();

    public Task ResolveBankReceiptAsync(ShopActor actor, string transactionId, string? reference, string? resolution) =>
        client.ResolveBankReceiptAsync(new(transactionId, reference, resolution));

    public Task<ShipperStateDto> ShipperStateAsync(ShopActor actor, CancellationToken cancellationToken = default) =>
        client.GetShipperStateAsync(cancellationToken);

    public Task UpdatePresenceAsync(ShopActor actor, bool onDuty, double? latitude, double? longitude, CancellationToken cancellationToken = default) =>
        client.UpdateShipperPresenceAsync(new(onDuty, latitude, longitude), cancellationToken);

    public Task RespondAsync(ShopActor actor, Guid sellerOrderId, Guid assignmentId, bool accept, CancellationToken cancellationToken = default) =>
        client.RespondAssignmentAsync(assignmentId, new(sellerOrderId, accept), cancellationToken);

    public Task<bool> DeliverAsync(ShopActor actor, Guid sellerOrderId, string code, DoorPayment? method, decimal? cash, CancellationToken cancellationToken = default) =>
        client.DeliverParcelAsync(sellerOrderId, new(code, method, cash), cancellationToken);

    public Task ReturnToSenderAsync(ShopActor actor, Guid sellerOrderId, string issue, CancellationToken cancellationToken = default) =>
        client.ReturnParcelToSenderAsync(sellerOrderId, new(issue), cancellationToken);
}

// ---------------------------------------------------------------------------
// Catalog DTOs & Client
// ---------------------------------------------------------------------------
public sealed record QuoteOffersRequest(IReadOnlyList<long> OfferIds);

public sealed record QuotedOfferDto(
    long OfferId,
    long SkuId,
    long SellerId,
    string SellerName,
    string SkuCode,
    string SellerSkuCode,
    string ProductSlug,
    string ProductName,
    string ProductAndVariantSnapshot,
    decimal PriceVnd,
    int Available,
    bool Active,
    long? LocationId,
    string? PickupAddress,
    double? PickupLatitude,
    double? PickupLongitude,
    int ProductId = 0);

public sealed record ReserveStockRequest(Guid OrderId, IReadOnlyList<StockReservationLine> Lines);
public sealed record ReserveStockResponse(bool Reserved, string? FailureReason);
public sealed record ReleaseStockRequest(Guid OrderId, Guid? SellerOrderId, string Reason);
public sealed record SaveCategoryRequest(long Id, string Name, bool Active);
public sealed record SaveProductRequest(int Id, string Name, string Slug, string Description, long? CategoryId);
public sealed record SaveSkuRequest(int ProductId, long SkuId, string Code, string Size, string color, decimal Price, bool Active);
public sealed record ChangePublicationRequest(bool Publish);
public sealed record AdjustStockRequest(long SkuId, int Delta, int Threshold, string Reason);
public sealed record IntIdResult(int Id);
public sealed record LongIdResult(long Id);

public sealed class CatalogServiceClient(HttpClient http) : ServiceHttpClientBase(http)
{
    public Task<CartOffersResponse> CartOffersAsync(CartOffersRequest request, CancellationToken ct = default) =>
        PostAsync<CartOffersRequest, CartOffersResponse>("/internal/catalog/offers/cart", request, ct);

    public Task<IReadOnlyList<CatalogCategoryDto>> CategoriesAsync(bool activeOnly = true, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CatalogCategoryDto>>($"/internal/catalog/categories?activeOnly={activeOnly}", ct);

    public Task<Page<CatalogProductDto>> ProductsAsync(int page, CancellationToken ct = default) =>
        GetAsync<Page<CatalogProductDto>>($"/internal/catalog/products?page={page}", ct);

    public Task<CatalogEditDetailsDto> EditDetailsAsync(int id, CancellationToken ct = default) =>
        GetAsync<CatalogEditDetailsDto>($"/internal/catalog/products/{id}", ct);

    public async Task<int> SaveCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default) =>
        (await PostAsync<SaveCategoryRequest, IntIdResult>("/internal/catalog/categories", request, ct)).Id;

    public async Task<int> SaveProductAsync(SaveProductRequest request, CancellationToken ct = default) =>
        (await PostAsync<SaveProductRequest, IntIdResult>("/internal/catalog/products", request, ct)).Id;

    public async Task<long> SaveSkuAsync(SaveSkuRequest request, CancellationToken ct = default) =>
        (await PostAsync<SaveSkuRequest, LongIdResult>("/internal/catalog/skus", request, ct)).Id;

    public async Task AddImageAsync(int productId, string alt, Stream file, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(alt), "alt");
        content.Add(new StreamContent(file), "file", "image.png");
        using var response = await Http.PostAsync($"/internal/catalog/products/{productId}/images", content, ct);
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, ct);
    }

    public async Task<int> ChangePublicationAsync(int id, bool publish, CancellationToken ct = default) =>
        (await PostAsync<ChangePublicationRequest, IntIdResult>($"/internal/catalog/products/{id}/publication", new(publish), ct)).Id;

    public Task<Page<StockRow>> ListFirstPartyStockAsync(bool low, int page = 1, CancellationToken ct = default) =>
        GetAsync<Page<StockRow>>($"/internal/catalog/stock/first-party?low={low}&page={page}", ct);

    public Task<IReadOnlyList<StockMovementDto>> FirstPartyHistoryAsync(long skuId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<StockMovementDto>>($"/internal/catalog/stock/first-party/{skuId}/history", ct);

    public async Task<int> AdjustFirstPartyStockAsync(AdjustStockRequest request, CancellationToken ct = default) =>
        (await PostAsync<AdjustStockRequest, IntIdResult>("/internal/catalog/stock/first-party/adjust", request, ct)).Id;

    public Task<IReadOnlyList<QuotedOfferDto>> QuoteOffersAsync(QuoteOffersRequest request, CancellationToken ct = default) =>
        PostAsync<QuoteOffersRequest, IReadOnlyList<QuotedOfferDto>>("/internal/catalog/offers/quote", request, ct);

    public Task<ReserveStockResponse> ReserveStockAsync(ReserveStockRequest request, CancellationToken ct = default) =>
        PostAsync<ReserveStockRequest, ReserveStockResponse>("/internal/catalog/stock/reserve", request, ct);

    public Task ReleaseStockAsync(ReleaseStockRequest request, CancellationToken ct = default) =>
        PostAsync("/internal/catalog/stock/release", request, ct);

    public Task DecideStoreApplicationAsync(Guid id, StoreDecisionRequest request, CancellationToken ct = default) =>
        PostAsync($"/internal/catalog/store-applications/{id:D}/decision", request, ct);

    public async Task<bool> SetSellerStatusAsync(long sellerId, SellerStatusRequest request, CancellationToken ct = default) =>
        (await PostAsync<SellerStatusRequest, BooleanResult>($"/internal/catalog/sellers/{sellerId}/status", request, ct)).Value;

    public async Task<bool> ReviewOfferAsync(long offerId, OfferReviewDecisionRequest request, CancellationToken ct = default) =>
        (await PostAsync<OfferReviewDecisionRequest, BooleanResult>($"/internal/catalog/moderation/offers/{offerId}", request, ct)).Value;

    public async Task<(byte[] Data, string ContentType)?> GetVerificationDocumentAsync(Guid id, StoreDocumentKind kind, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"/internal/catalog/store-applications/{id:D}/documents/{kind}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ServiceClientDefaults.EnsureSuccessOrThrowRuleAsync(response, ct);
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public Task<IReadOnlyList<AuditEntryDto>> AuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuditEntryDto>>($"/internal/catalog/reports/audit?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct);
}

public sealed record StoreDecisionRequest(bool Approve, string? DeclineReason);
public sealed record SellerStatusRequest(SellerStatus Status, string? Reason);
public sealed record OfferReviewDecisionRequest(bool Approve);

public sealed class HttpCatalogOperationsClient(CatalogServiceClient client) : ICatalogOperationsPort
{
    public Task<IReadOnlyList<CatalogCategoryDto>> CategoriesAsync(bool activeOnly = true) =>
        client.CategoriesAsync(activeOnly);

    public Task<Page<CatalogProductDto>> ProductsAsync(ShopActor actor, int page) =>
        client.ProductsAsync(page);

    public Task<CatalogEditDetailsDto> EditDetailsAsync(ShopActor actor, int id) =>
        client.EditDetailsAsync(id);

    public Task<int> SaveCategoryAsync(ShopActor actor, long id, string name, bool active) =>
        client.SaveCategoryAsync(new(id, name, active));

    public Task<int> SaveProductAsync(ShopActor actor, int id, string name, string slug, string description, long? categoryId) =>
        client.SaveProductAsync(new(id, name, slug, description, categoryId));

    public Task<long> SaveSkuAsync(ShopActor actor, int productId, long skuId, string code, string size, string color, decimal price, bool active) =>
        client.SaveSkuAsync(new(productId, skuId, code, size, color, price, active));

    public Task AddImageAsync(ShopActor actor, int productId, string alt, Stream file) =>
        client.AddImageAsync(productId, alt, file);

    public Task<int> ChangePublicationAsync(ShopActor actor, int id, bool publish) =>
        client.ChangePublicationAsync(id, publish);

    public Task<Page<StockRow>> ListFirstPartyStockAsync(ShopActor actor, bool low, int page = 1) =>
        client.ListFirstPartyStockAsync(low, page);

    public Task<IReadOnlyList<StockMovementDto>> FirstPartyHistoryAsync(ShopActor actor, long skuId) =>
        client.FirstPartyHistoryAsync(skuId);

    public Task<int> AdjustFirstPartyStockAsync(ShopActor actor, long skuId, int delta, int threshold, string reason) =>
        client.AdjustFirstPartyStockAsync(new(skuId, delta, threshold, reason));
}

// ---------------------------------------------------------------------------
// Payments DTOs & Client
// ---------------------------------------------------------------------------
public sealed record PaymentConfigDto(bool OnlineAvailable);
public sealed record OrderPaymentDetailsDto(
    EOfficePaymentDto? Payment,
    IReadOnlyList<EOfficePaymentAttemptDto> Attempts,
    IReadOnlyList<EOfficeRefundDto> Refunds);
public sealed record CreateRetryAttemptResponse(string ProviderInvoice);
public sealed record CompleteRefundRequest(string Reference);
public sealed record RequestPayoutRequest(Guid SubmissionKey, decimal AmountVnd);
public sealed record ReviewPayoutRequest(bool Approve, string Note);
public sealed record SellerLedgerBalanceDto(decimal PendingVnd, decimal AvailableVnd, decimal InTransitVnd);

public sealed class PaymentsServiceClient(HttpClient http) : ServiceHttpClientBase(http)
{
    public Task<PaymentConfigDto> GetConfigAsync(CancellationToken ct = default) =>
        GetAsync<PaymentConfigDto>("/internal/payments/config", ct);

    public Task<OrderPaymentDetailsDto?> GetOrderPaymentDetailsAsync(Guid orderId, CancellationToken ct = default) =>
        GetOrDefaultAsync<OrderPaymentDetailsDto>($"/internal/payments/orders/{orderId:D}", ct);

    public Task<HostedCheckout> GetCheckoutFormAsync(Guid orderId, CancellationToken ct = default) =>
        GetAsync<HostedCheckout>($"/internal/payments/orders/{orderId:D}/checkout-form", ct);

    public Task<CreateRetryAttemptResponse> CreateRetryAttemptAsync(Guid orderId, CancellationToken ct = default) =>
        PostAsync<object, CreateRetryAttemptResponse>($"/internal/payments/orders/{orderId:D}/retry", new { }, ct);

    public Task<IReadOnlyList<PaymentIssue>> GetIssuesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PaymentIssue>>("/internal/payments/issues", ct);

    public Task<IReadOnlyList<EOfficeRefundDto>> GetPendingRefundsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EOfficeRefundDto>>("/internal/payments/refunds/pending", ct);

    public Task CompleteRefundAsync(long refundId, string reference, CancellationToken ct = default) =>
        PostAsync($"/internal/payments/refunds/{refundId}/complete", new CompleteRefundRequest(reference), ct);

    public Task<SellerLedgerBalanceDto> GetSellerBalancesAsync(long sellerId, CancellationToken ct = default) =>
        GetAsync<SellerLedgerBalanceDto>($"/internal/payments/sellers/{sellerId}/balances", ct);

    public Task<Page<PayoutQueueItemDto>> GetPayoutQueueAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<Page<PayoutQueueItemDto>>($"/internal/payments/payouts?page={page}", ct);

    public Task<OrderIdResult> RequestSellerPayoutAsync(long sellerId, RequestPayoutRequest request, CancellationToken ct = default) =>
        PostAsync<RequestPayoutRequest, OrderIdResult>($"/internal/payments/sellers/{sellerId}/payouts", request, ct);

    public Task ReviewPayoutAsync(Guid payoutId, ReviewPayoutRequest request, CancellationToken ct = default) =>
        PostAsync($"/internal/payments/payouts/{payoutId:D}/review", request, ct);

    public Task<IReadOnlyList<AuditEntryDto>> AuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuditEntryDto>>($"/internal/payments/reports/audit?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct);
}

// ---------------------------------------------------------------------------
// Ordering DTOs & Client
// ---------------------------------------------------------------------------
public sealed record PlaceOrderRequest(CheckoutQuote Quote);
public sealed record PlaceOrderResponse(Guid OrderId, OrderStatus Status);
public sealed record OrderStatusResponse(Guid OrderId, string Number, OrderStatus Status, PaymentStatus PaymentStatus, decimal TotalVnd);
public sealed record CancelOrderBody(string Reason, long? SellerId = null);
public sealed record SellerScopeBody(long? SellerId);
public sealed record DispatchSellerOrderBody(string Carrier, string TrackingNumber, long? SellerId);
public sealed record InspectReturnBody(IReadOnlyList<InspectedItem> Inspection, string Note, long? SellerId);

public sealed record OrderDetailsResponse(
    Guid Id,
    long? SellerId,
    string Number,
    string CustomerId,
    string CustomerEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? PaymentDeadline,
    OrderStatus Status,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string Recipient,
    string Phone,
    string ShippingAddress,
    decimal ShippingVnd,
    decimal TotalVnd,
    IReadOnlyList<EOfficeSellerOrderDto> SellerOrders,
    IReadOnlyList<EOfficeOrderItemDto> Items,
    IReadOnlyList<EOfficeOrderEventDto> Events,
    IReadOnlyList<EOfficeReturnDto> Returns);

public sealed class OrderingServiceClient(HttpClient http) : ServiceHttpClientBase(http)
{
    public Task<CartView> CartAsync(string? guestHash, CancellationToken ct = default) =>
        PostAsync<CartReadRequest, CartView>("/internal/ordering/cart/read", new(guestHash), ct);

    public Task<int> ChangeCartAsync(CartChangeRequest request, CancellationToken ct = default) =>
        PostAsync<CartChangeRequest, int>("/internal/ordering/cart/change", request, ct);

    public Task<int> MergeCartAsync(string guestHash, CancellationToken ct = default) =>
        PostAsync<CartReadRequest, int>("/internal/ordering/cart/merge", new(guestHash), ct);

    public Task<int> BuyAgainAsync(Guid orderId, CancellationToken ct = default) =>
        PostAsync<object, int>($"/internal/ordering/cart/buy-again/{orderId:D}", new { }, ct);

    public Task<CheckoutPreviewDto> QuoteAsync(QuoteCheckoutRequest request, CancellationToken ct = default) =>
        PostAsync<QuoteCheckoutRequest, CheckoutPreviewDto>("/internal/ordering/checkout/quote", request, ct);

    public Task<PlaceOrderResponse> PlaceOrderAsync(CheckoutQuote quote, CancellationToken ct = default) =>
        PostAsync<PlaceOrderRequest, PlaceOrderResponse>("/internal/ordering/checkout/place", new(quote), ct);

    public Task<OrderStatusResponse?> GetOrderStatusAsync(Guid orderId, CancellationToken ct = default) =>
        GetOrDefaultAsync<OrderStatusResponse>($"/internal/ordering/orders/{orderId:D}", ct);

    public Task<OrderDetailsResponse?> GetOrderDetailsAsync(Guid orderId, CancellationToken ct = default) =>
        GetOrDefaultAsync<OrderDetailsResponse>($"/internal/ordering/orders/{orderId:D}/details", ct);

    public Task<Page<EOfficeOrderSummaryDto>> ListMyOrdersAsync(OrderStatus? status, int page, CancellationToken ct = default) =>
        GetAsync<Page<EOfficeOrderSummaryDto>>(status is { } s
            ? $"/internal/ordering/orders/mine?status={s}&page={page}"
            : $"/internal/ordering/orders/mine?page={page}", ct);

    public Task<Page<EOfficeOrderSummaryDto>> ListOrdersAsync(OrderStatus? status, int page, CancellationToken ct = default) =>
        GetAsync<Page<EOfficeOrderSummaryDto>>(status is { } s
            ? $"/internal/ordering/orders?status={s}&page={page}"
            : $"/internal/ordering/orders?page={page}", ct);

    public Task CancelOrderAsync(Guid orderId, string reason, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/orders/{orderId:D}/cancel", new CancelOrderBody(reason), ct);

    public Task CancelSellerOrderAsync(Guid sellerOrderId, string reason, long? sellerId = null, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/seller-orders/{sellerOrderId:D}/cancel", new CancelOrderBody(reason, sellerId), ct);

    public Task PackSellerOrderAsync(Guid sellerOrderId, long? sellerId = null, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/seller-orders/{sellerOrderId:D}/pack", new SellerScopeBody(sellerId), ct);

    public Task DispatchSellerOrderAsync(Guid sellerOrderId, string carrier, string trackingNumber, long? sellerId = null, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/seller-orders/{sellerOrderId:D}/dispatch", new DispatchSellerOrderBody(carrier, trackingNumber, sellerId), ct);

    public Task DeliverSellerOrderAsync(Guid sellerOrderId, long? sellerId = null, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/seller-orders/{sellerOrderId:D}/deliver", new SellerScopeBody(sellerId), ct);

    public Task InspectReturnAsync(Guid returnId, IReadOnlyList<InspectedItem> inspection, string note, long? sellerId = null, CancellationToken ct = default) =>
        PostAsync($"/internal/ordering/returns/{returnId:D}/inspect", new InspectReturnBody(inspection, note, sellerId), ct);

    public Task<SalesSummary> GetSalesSummaryAsync(DateOnly start, DateOnly end, CancellationToken ct = default) =>
        GetAsync<SalesSummary>($"/internal/ordering/reports/sales?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}", ct);

    public Task<IReadOnlyList<AuditEntryDto>> AuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuditEntryDto>>($"/internal/ordering/reports/audit?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct);
}

// ---------------------------------------------------------------------------
// Identity DTOs & Client
// ---------------------------------------------------------------------------
public sealed record IssueTokenRequest(string Email, string Password, string Platform, string? TwoFactorCode = null);
public sealed record RefreshTokenRequest(string RefreshToken);
public sealed record RevokeTokenRequest(string RefreshToken);

public sealed record TokenPairResponse(
    bool RequiresTwoFactor,
    bool RequiresMfaEnrollment,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? DefaultPath);

public sealed record CustomerAddressDto(
    long Id,
    string CustomerId,
    string Recipient,
    string Phone,
    string AddressText,
    double? Latitude,
    double? Longitude);

public sealed record RegisterUserBody(string Email, string DisplayName, string Password);
public sealed record SaveAddressBody(long Id, string Recipient, string Phone, string AddressText, double? Latitude, double? Longitude);
public sealed record DisableUserBody(bool Disabled);
public sealed record SetShipperBody(string Email, bool Active);

public sealed class IdentityServiceClient(HttpClient http) : ServiceHttpClientBase(http)
{
    public Task<TokenPairResponse> IssueTokenAsync(IssueTokenRequest request, CancellationToken ct = default) =>
        PostAsync<IssueTokenRequest, TokenPairResponse>("/internal/identity/tokens/issue", request, ct);

    public Task<TokenPairResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default) =>
        PostAsync<RefreshTokenRequest, TokenPairResponse>("/internal/identity/tokens/refresh", new(refreshToken), ct);

    public Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default) =>
        PostAsync("/internal/identity/tokens/revoke", new RevokeTokenRequest(refreshToken), ct);

    public Task RegisterAsync(RegisterUserBody request, CancellationToken ct = default) =>
        PostAsync("/internal/identity/register", request, ct);

    public Task<IReadOnlyList<CustomerAddressDto>> GetCustomerAddressesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CustomerAddressDto>>("/internal/identity/addresses", ct);

    public Task<CustomerAddressDto?> GetCustomerAddressAsync(long addressId, CancellationToken ct = default) =>
        GetOrDefaultAsync<CustomerAddressDto>($"/internal/identity/addresses/{addressId}", ct);

    public async Task<long> SaveCustomerAddressAsync(SaveAddressBody request, CancellationToken ct = default) =>
        (await PostAsync<SaveAddressBody, LongIdResult>("/internal/identity/addresses", request, ct)).Id;

    public Task DeleteCustomerAddressAsync(long addressId, CancellationToken ct = default) =>
        DeleteAsync($"/internal/identity/addresses/{addressId}", ct);

    public Task SetUserDisabledAsync(string userId, bool disabled, CancellationToken ct = default) =>
        PostAsync($"/internal/identity/users/{Uri.EscapeDataString(userId)}/disabled", new DisableUserBody(disabled), ct);

    public Task SetShipperRoleAsync(string email, bool active, CancellationToken ct = default) =>
        PostAsync("/internal/identity/shippers", new SetShipperBody(email, active), ct);

    public Task<IReadOnlyList<AuditEntryDto>> AuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuditEntryDto>>($"/internal/identity/reports/audit?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", ct);
}

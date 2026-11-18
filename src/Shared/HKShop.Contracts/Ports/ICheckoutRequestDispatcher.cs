using HKShop.Application;
using HKShop.Application.Checkout;

namespace HKShop.Infrastructure.Kafka
{
    public enum CheckoutRequestStatus { Pending, Placed, Rejected }

    public sealed record CheckoutResult(Guid RequestId, CheckoutRequestStatus Status, Guid? OrderId, string? Error);
}

namespace HKShop.Contracts.Ports
{
    using HKShop.Infrastructure.Kafka;

    public interface ICheckoutRequestDispatcher
    {
        Task<CheckoutResult> Submit(ShopActor actor, CheckoutQuote quote);
        Task<CheckoutResult?> Get(ShopActor actor, Guid id);
    }
}

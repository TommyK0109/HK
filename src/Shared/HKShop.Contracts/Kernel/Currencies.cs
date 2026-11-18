using HKShop.Domain;

namespace HKShop.Domain.Currencies
{
    public sealed record MoneyConversionResult(
        decimal OriginalAmount,
        string OriginalCurrency,
        string TargetCurrency,
        decimal ExchangeRate,
        decimal AmountVnd
    );

    public static class CurrencyConverter
    {
        public const string Vnd = "VND";
        public const string Usd = "USD";
        public const string Cny = "CNY";
        public const string Eur = "EUR";

        public static MoneyConversionResult ConvertToVnd(
            decimal amount,
            string currency,
            decimal exchangeRate,
            bool roundToNearestThousand = false)
        {
            if (amount < 0)
                throw new ShopRuleException("The amount cannot be negative.");

            currency = (currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length != 3)
                throw new ShopRuleException("The currency code must contain exactly 3 characters (ISO 4217).");

            if (exchangeRate <= 0)
                throw new ShopRuleException("The exchange rate must be greater than zero.");

            if (currency == Vnd)
            {
                var wholeVnd = ShopRules.Vnd(amount);
                return new MoneyConversionResult(amount, Vnd, Vnd, 1m, wholeVnd);
            }

            var rawConverted = amount * exchangeRate;
            var roundedVnd = roundToNearestThousand
                ? Math.Round(rawConverted / 1000m, MidpointRounding.AwayFromZero) * 1000m
                : Math.Round(rawConverted, MidpointRounding.AwayFromZero);

            var finalVnd = ShopRules.Vnd(roundedVnd);
            return new MoneyConversionResult(amount, currency, Vnd, exchangeRate, finalVnd);
        }
    }
}

namespace HKShop.Application.Currencies
{
    using HKShop.Domain.Currencies;

    public interface ICurrencyExchangeRateProvider
    {
        Task<decimal> GetRateAsync(string fromCurrency, string toCurrency = "VND", CancellationToken cancellationToken = default);
    }

    public sealed class CurrencyExchangeService(ICurrencyExchangeRateProvider rateProvider)
    {
        public async Task<MoneyConversionResult> ConvertToVndAsync(
            decimal amount,
            string currency,
            bool roundToNearestThousand = false,
            CancellationToken cancellationToken = default)
        {
            currency = (currency ?? "").Trim().ToUpperInvariant();
            if (currency == CurrencyConverter.Vnd)
            {
                return CurrencyConverter.ConvertToVnd(amount, currency, 1m, roundToNearestThousand);
            }

            var rate = await rateProvider.GetRateAsync(currency, CurrencyConverter.Vnd, cancellationToken);
            return CurrencyConverter.ConvertToVnd(amount, currency, rate, roundToNearestThousand);
        }
    }

    public sealed class DefaultExchangeRateProvider : ICurrencyExchangeRateProvider
    {
        private readonly Dictionary<string, decimal> _ratesToVnd = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VND"] = 1m,
            ["USD"] = 25_450m,
            ["CNY"] = 3_550m,
            ["EUR"] = 27_600m,
            ["JPY"] = 165m
        };

        public DefaultExchangeRateProvider() { }

        public DefaultExchangeRateProvider(IDictionary<string, decimal> customRates)
        {
            foreach (var (k, v) in customRates)
            {
                _ratesToVnd[k] = v;
            }
        }

        public Task<decimal> GetRateAsync(string fromCurrency, string toCurrency = "VND", CancellationToken cancellationToken = default)
        {
            if (string.Equals(toCurrency, CurrencyConverter.Vnd, StringComparison.OrdinalIgnoreCase))
            {
                if (_ratesToVnd.TryGetValue(fromCurrency, out var rate))
                {
                    return Task.FromResult(rate);
                }
                throw new ShopRuleException($"No exchange rate was found for '{fromCurrency}' to VND.");
            }

            throw new ShopRuleException($"Only conversion to VND is currently supported (requested: {toCurrency}).");
        }
    }
}

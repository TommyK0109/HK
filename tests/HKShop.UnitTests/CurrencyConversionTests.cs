using HKShop.Application.Currencies;
using HKShop.Domain;
using HKShop.Domain.Currencies;
using Xunit;

namespace HKShop.UnitTests;

public sealed class CurrencyConversionTests
{
    [Fact]
    public void ConvertToVnd_WithVnd_ReturnsExactAmountAndRateOne()
    {
        var result = CurrencyConverter.ConvertToVnd(50_000m, "VND", 1m);

        Assert.Equal(50_000m, result.AmountVnd);
        Assert.Equal(50_000m, result.OriginalAmount);
        Assert.Equal("VND", result.OriginalCurrency);
        Assert.Equal("VND", result.TargetCurrency);
        Assert.Equal(1m, result.ExchangeRate);
    }

    [Theory]
    [InlineData(9.99, 25_450.50, 254_250)] // 9.99 * 25,450.50 = 254,250.495 -> 254,250
    [InlineData(10.00, 25_450.50, 254_505)] // 10.00 * 25,450.50 = 254,505
    [InlineData(100.55, 3_550.25, 356_978)] // 100.55 * 3,550.25 = 356,977.6375 -> 356,978
    public void ConvertToVnd_ForeignCurrency_RoundsAwayFromZeroToWholeVnd(decimal amount, decimal rate, decimal expectedVnd)
    {
        var result = CurrencyConverter.ConvertToVnd(amount, "USD", rate);

        Assert.Equal(expectedVnd, result.AmountVnd);
        Assert.Equal(expectedVnd, ShopRules.Vnd(result.AmountVnd)); // Guaranteed to pass ShopRules.Vnd
        Assert.Equal(amount, result.OriginalAmount);
        Assert.Equal(rate, result.ExchangeRate);
    }

    [Theory]
    [InlineData(9.99, 25_450.50, 254_000)] // 254,250.495 -> 254,000 (nearest 1,000)
    [InlineData(10.00, 25_450.50, 255_000)] // 254,505 -> 255,000 (nearest 1,000)
    public void ConvertToVnd_WithRoundToNearestThousand_RoundsToThousand(decimal amount, decimal rate, decimal expectedVnd)
    {
        var result = CurrencyConverter.ConvertToVnd(amount, "USD", rate, roundToNearestThousand: true);

        Assert.Equal(expectedVnd, result.AmountVnd);
        Assert.Equal(0, result.AmountVnd % 1000m);
    }

    [Fact]
    public void ConvertToVnd_RejectsNegativeAmount()
    {
        Assert.Throws<ShopRuleException>(() => CurrencyConverter.ConvertToVnd(-10m, "USD", 25_000m));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("US")]
    [InlineData("USDT")]
    [InlineData(null)]
    public void ConvertToVnd_RejectsInvalidCurrencyCode(string? currency)
    {
        Assert.Throws<ShopRuleException>(() => CurrencyConverter.ConvertToVnd(10m, currency!, 25_000m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConvertToVnd_RejectsInvalidExchangeRate(decimal rate)
    {
        Assert.Throws<ShopRuleException>(() => CurrencyConverter.ConvertToVnd(10m, "USD", rate));
    }

    [Fact]
    public async Task CurrencyExchangeService_ConvertsUsingConfiguredProvider()
    {
        var provider = new DefaultExchangeRateProvider();
        var service = new CurrencyExchangeService(provider);

        var resultUsd = await service.ConvertToVndAsync(10m, "USD");
        Assert.Equal(254_500m, resultUsd.AmountVnd); // 10 * 25,450

        var resultCny = await service.ConvertToVndAsync(100m, "CNY");
        Assert.Equal(355_000m, resultCny.AmountVnd); // 100 * 3,550
    }

    [Fact]
    public async Task CurrencyExchangeService_ThrowsWhenCurrencyUnknown()
    {
        var provider = new DefaultExchangeRateProvider();
        var service = new CurrencyExchangeService(provider);

        await Assert.ThrowsAsync<ShopRuleException>(() => service.ConvertToVndAsync(10m, "ABC"));
    }
}

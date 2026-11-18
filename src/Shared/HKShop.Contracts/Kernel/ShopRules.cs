namespace HKShop.Domain;

public sealed class ShopRuleException(string message) : Exception(message);

public static class ShopRules
{
    public static string Text(string? value, int max, string label, bool required = true)
    {
        var text = value?.Trim() ?? "";
        if ((required && text.Length == 0) || text.Length > max)
            throw new ShopRuleException($"{label}: Required length is {max} .");
        return text;
    }

    public static decimal Vnd(decimal amount)
    {
        if (amount < 0 || amount > 999_999_999_999_999_999m || decimal.Truncate(amount) != amount)
            throw new ShopRuleException("Invalid value.");
        return amount;
    }

    public static int CommissionBps(int bps)
    {
        if (bps is < 0 or > 10_000)
            throw new ShopRuleException("Commission basis points must be between 0 and 10000.");
        return bps;
    }
}

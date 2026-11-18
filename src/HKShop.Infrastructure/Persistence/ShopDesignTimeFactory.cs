using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HKShop.Infrastructure.Persistence;

public sealed class ShopDesignTimeFactory : IDesignTimeDbContextFactory<ShopDbContext>
{
    public ShopDbContext CreateDbContext(string[] args)
    {
        // Model scaffolding works without a live database. Supply an explicit connection for migration commands.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__HKShop")
            ?? "Server=localhost,1433;Database=HKShop;Integrated Security=True;TrustServerCertificate=True";
        return new ShopDbContext(new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(connection).Options);
    }
}

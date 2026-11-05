using LifeOS.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LifeOS.PostgresMigrations;

public sealed class LifeOSDbContextFactory : IDesignTimeDbContextFactory<LifeOSDbContext>
{
    public LifeOSDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LifeOSDbContext>()
            .UseNpgsql("Host=localhost;Database=lifeos;Username=postgres;Password=design-time-only",
                postgres => postgres.MigrationsAssembly(typeof(LifeOSDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new LifeOSDbContext(options);
    }
}

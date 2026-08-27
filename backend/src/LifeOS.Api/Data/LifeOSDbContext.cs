using LifeOS.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Data;

public class LifeOSDbContext : DbContext
{
    public LifeOSDbContext(DbContextOptions<LifeOSDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
}
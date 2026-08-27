using LifeOS.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add database
builder.Services.AddDbContext<LifeOSDbContext>(options =>
    options.UseSqlite("Data Source=lifeos.db"));

// Add controllers
builder.Services.AddControllers();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapControllers();

app.MapGet("/", () => "LifeOS API is running!");

app.Run();
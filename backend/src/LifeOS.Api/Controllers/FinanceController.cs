using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/finance")]
public class FinanceController(LifeOSDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> GetTransactions([FromQuery] string? month = null)
    {
        if (month is not null && ParseMonth(month) is null)
            return BadRequest(new { message = "Month must use YYYY-MM format." });
        var selectedMonth = ParseMonth(month) ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = selectedMonth.AddMonths(1);
        var entries = await db.FinanceTransactions.AsNoTracking()
            .Where(x => x.UserId == UserId && x.OccurredAt >= selectedMonth && x.OccurredAt < end)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).ToListAsync();

        var income = entries.Where(x => x.Type == "income").Sum(x => x.AmountMinor);
        var expenses = entries.Where(x => x.Type == "expense").Sum(x => x.AmountMinor);
        var budgets = await db.FinanceBudgets.AsNoTracking().Where(x => x.UserId == UserId && x.Month == selectedMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .OrderBy(x => x.Category).ToListAsync();
        var categorySpending = entries.Where(x => x.Type == "expense").GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.AmountMinor), StringComparer.OrdinalIgnoreCase);
        return Ok(new FinanceMonthResponse(selectedMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture), "INR",
            income, expenses, income - expenses, entries.Select(ToResponse).ToList(),
            budgets.Select(x => new FinanceBudgetResponse(x.Id, x.Category, x.AmountMinor)).ToList(), categorySpending));
    }

    [HttpPost("budgets")]
    public async Task<IActionResult> SaveBudget(BudgetRequest request)
    {
        if (ParseMonth(request.Month) is null) return BadRequest(new { message = "Month must use YYYY-MM format." });
        if (!HasValidPrecision(request.Amount)) return BadRequest(new { message = "Amount can have at most two decimal places." });
        var category = request.Category.Trim();
        var monthBudgets = await db.FinanceBudgets.Where(x => x.UserId == UserId && x.Month == request.Month).ToListAsync();
        var budget = monthBudgets.FirstOrDefault(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase));
        if (budget is null)
        {
            budget = new FinanceBudget { UserId = UserId, Month = request.Month, Category = category };
            db.FinanceBudgets.Add(budget);
        }
        budget.AmountMinor = ToMinorUnits(request.Amount);
        await db.SaveChangesAsync();
        return Ok(new FinanceBudgetResponse(budget.Id, budget.Category, budget.AmountMinor));
    }

    [HttpDelete("budgets/{id:int}")]
    public async Task<IActionResult> DeleteBudget(int id)
    {
        var budget = await db.FinanceBudgets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (budget is null) return NotFound();
        db.FinanceBudgets.Remove(budget);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<FinanceTransactionResponse>> GetTransaction(int id)
    {
        var entry = await db.FinanceTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        return entry is null ? NotFound() : ToResponse(entry);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTransaction(TransactionRequest request)
    {
        if (!ValidType(request.Type)) return BadRequest(new { message = "Type must be income or expense." });
        if (!HasValidPrecision(request.Amount)) return BadRequest(new { message = "Amount can have at most two decimal places." });
        var entry = new FinanceTransaction
        {
            UserId = UserId, Type = request.Type, AmountMinor = ToMinorUnits(request.Amount),
            Category = request.Category.Trim(), Note = request.Note?.Trim(),
            OccurredAt = DateTime.SpecifyKind(request.OccurredAt.Date, DateTimeKind.Utc)
        };
        db.FinanceTransactions.Add(entry);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetTransaction), new { id = entry.Id }, ToResponse(entry));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateTransaction(int id, TransactionRequest request)
    {
        if (!ValidType(request.Type)) return BadRequest(new { message = "Type must be income or expense." });
        if (!HasValidPrecision(request.Amount)) return BadRequest(new { message = "Amount can have at most two decimal places." });
        var entry = await db.FinanceTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry is null) return NotFound();
        entry.Type = request.Type; entry.AmountMinor = ToMinorUnits(request.Amount);
        entry.Category = request.Category.Trim(); entry.Note = request.Note?.Trim();
        entry.OccurredAt = DateTime.SpecifyKind(request.OccurredAt.Date, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTransaction(int id)
    {
        var entry = await db.FinanceTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry is null) return NotFound();
        db.FinanceTransactions.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static bool ValidType(string type) => type is "income" or "expense";
    private static bool HasValidPrecision(decimal amount) => amount * 100m == decimal.Truncate(amount * 100m);
    private static long ToMinorUnits(decimal amount) => checked((long)(amount * 100m));
    private static DateTime? ParseMonth(string? month) =>
        DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            : null;
    private static FinanceTransactionResponse ToResponse(FinanceTransaction entry) => new(entry.Id, entry.Type, entry.AmountMinor, entry.Category, entry.Note, entry.OccurredAt);
}

public record TransactionRequest(
    [Required, RegularExpression("^(income|expense)$")] string Type,
    [Range(typeof(decimal), "0.01", "999999999.99")] decimal Amount,
    [Required, StringLength(60, MinimumLength = 1)] string Category,
    [StringLength(500)] string? Note,
    DateTime OccurredAt);

public record FinanceTransactionResponse(int Id, string Type, long AmountMinor, string Category, string? Note, DateTime OccurredAt);
public record FinanceBudgetResponse(int Id, string Category, long AmountMinor);
public record BudgetRequest([Required, RegularExpression("^[0-9]{4}-[0-9]{2}$")] string Month,
    [Required, StringLength(60, MinimumLength = 1)] string Category,
    [Range(typeof(decimal), "0.01", "999999999.99")] decimal Amount);
public record FinanceMonthResponse(string Month, string Currency, long IncomeMinor, long ExpenseMinor, long BalanceMinor,
    IReadOnlyList<FinanceTransactionResponse> Transactions, IReadOnlyList<FinanceBudgetResponse> Budgets,
    IReadOnlyDictionary<string, long> CategorySpending);

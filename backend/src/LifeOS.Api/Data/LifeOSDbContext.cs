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
    public DbSet<UserCredential> UserCredentials { get; set; }
    public DbSet<PasswordResetCode> PasswordResetCodes { get; set; }
    public DbSet<TaskItem> Tasks { get; set; }
    public DbSet<ApiSession> Sessions { get; set; }
    public DbSet<FinanceTransaction> FinanceTransactions { get; set; }
    public DbSet<FinanceBudget> FinanceBudgets { get; set; }
    public DbSet<WorkoutEntry> Workouts { get; set; }
    public DbSet<FitnessHabit> FitnessHabits { get; set; }
    public DbSet<HabitCheckIn> HabitCheckIns { get; set; }
    public DbSet<LifeDocument> Documents { get; set; }
    public DbSet<CareerApplication> CareerApplications { get; set; }
    public DbSet<CareerSkill> CareerSkills { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserCredential>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<PasswordResetCode>().HasIndex(x => x.UserId).IsUnique();
        modelBuilder.Entity<TaskItem>().HasIndex(x => new { x.UserId, x.DueAt });
        modelBuilder.Entity<TaskItem>().HasIndex(x => new { x.ReminderAt, x.ReminderSentAt });
        modelBuilder.Entity<TaskItem>().Property(x => x.Recurrence).HasDefaultValue("none");
        modelBuilder.Entity<ApiSession>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<FinanceTransaction>().HasIndex(x => new { x.UserId, x.OccurredAt });
        modelBuilder.Entity<FinanceBudget>().HasIndex(x => new { x.UserId, x.Month, x.Category }).IsUnique();
        modelBuilder.Entity<WorkoutEntry>().HasIndex(x => new { x.UserId, x.PerformedAt });
        modelBuilder.Entity<FitnessHabit>().HasIndex(x => new { x.UserId, x.IsActive });
        modelBuilder.Entity<HabitCheckIn>().HasIndex(x => new { x.UserId, x.HabitId, x.Date }).IsUnique();
        modelBuilder.Entity<LifeDocument>().HasIndex(x => new { x.UserId, x.Category });
        modelBuilder.Entity<CareerApplication>().HasIndex(x => new { x.UserId, x.Status, x.NextStepAt });
        modelBuilder.Entity<CareerSkill>().HasIndex(x => new { x.UserId, x.Category });
        modelBuilder.Entity<TaskItem>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserCredential>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PasswordResetCode>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ApiSession>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FinanceTransaction>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FinanceBudget>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<WorkoutEntry>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FitnessHabit>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<HabitCheckIn>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<HabitCheckIn>().HasOne<FitnessHabit>().WithMany().HasForeignKey(x => x.HabitId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LifeDocument>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CareerApplication>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CareerSkill>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

using CampCost.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace CampCost.Infrastructure.Data;

/// <summary>
/// EF Core context connecting to Supabase (PostgreSQL).
/// All writes use the service-role connection string which bypasses Supabase RLS.
/// This is intentional — equivalent to a Supabase Edge Function with service role.
/// RLS still protects direct client access; only this backend can write server-side data.
/// </summary>
public class CampCostDbContext : DbContext
{
    public CampCostDbContext(DbContextOptions<CampCostDbContext> options) : base(options) { }

    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<PlaidConnection> PlaidConnections => Set<PlaidConnection>();
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<TripStop> TripStops => Set<TripStop>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // trips
        modelBuilder.Entity<Trip>(e =>
        {
            e.ToTable("trips");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.UserId).HasColumnName("user_id");
            e.Property(t => t.Name).HasColumnName("name");
            e.Property(t => t.Destination).HasColumnName("destination");
            e.Property(t => t.StartDate).HasColumnName("start_date");
            e.Property(t => t.EndDate).HasColumnName("end_date");
            e.Property(t => t.TotalBudget).HasColumnName("total_budget");
            e.Property(t => t.Status).HasColumnName("status");
            e.Property(t => t.CreatedAt).HasColumnName("created_at");
        });

        // expenses
        modelBuilder.Entity<Expense>(e =>
        {
            e.ToTable("expenses");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TripId).HasColumnName("trip_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.PlaidTransactionId).HasColumnName("plaid_transaction_id");
            e.Property(x => x.MerchantName).HasColumnName("merchant_name");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Category)
             .HasColumnName("category")
             .HasColumnType("expense_category")
             .HasConversion(
                 v => Enum.Parse<ExpenseCategoryEnum>(v),
                 v => v.ToString());
            e.Property(x => x.ExpenseDate).HasColumnName("expense_date");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Reviewed).HasColumnName("reviewed");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");

            // Soft-deleted expenses (Plaid `removed` deltas) are hidden everywhere by
            // default — budgets, trip views, etc. Sync code uses IgnoreQueryFilters()
            // for dedup lookups so a removed transaction is never re-imported.
            e.HasQueryFilter(x => x.DeletedAt == null);

            // Per-user unique: same Plaid tx can't appear twice for the same user.
            // Global uniqueness is NOT required (different users could theoretically share
            // a transaction ID in edge cases with test/sandbox data).
            e.HasIndex(x => new { x.UserId, x.PlaidTransactionId })
             .IsUnique()
             .HasFilter("plaid_transaction_id IS NOT NULL");
        });

        // plaid_connections
        modelBuilder.Entity<PlaidConnection>(e =>
        {
            e.ToTable("plaid_connections");
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.UserId).HasColumnName("user_id");
            e.Property(p => p.AccessToken).HasColumnName("access_token");
            e.Property(p => p.ItemId).HasColumnName("item_id");
            e.Property(p => p.InstitutionName).HasColumnName("institution_name");
            e.Property(p => p.LastSyncAt).HasColumnName("last_sync_at");
            e.Property(p => p.SyncCursor).HasColumnName("sync_cursor");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
        });

        // budget_categories — owned by trip (no user_id column; RLS via trip join)
        modelBuilder.Entity<BudgetCategory>(e =>
        {
            e.ToTable("budget_categories");
            e.Property(b => b.Id).HasColumnName("id");
            e.Property(b => b.TripId).HasColumnName("trip_id");
            e.Property(b => b.Category)
             .HasColumnName("category")
             .HasColumnType("expense_category")
             .HasConversion(
                 v => Enum.Parse<ExpenseCategoryEnum>(v),
                 v => v.ToString());
            e.Property(b => b.PlannedAmount).HasColumnName("planned_amount");
            e.Property(b => b.CreatedAt).HasColumnName("created_at");
        });

        // trip_stops — owned by trip (no user_id column; RLS via trip join)
        modelBuilder.Entity<TripStop>(e =>
        {
            e.ToTable("trip_stops");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.TripId).HasColumnName("trip_id");
            e.Property(s => s.Location).HasColumnName("location");
            e.Property(s => s.ArrivalDate).HasColumnName("arrival_date");
            e.Property(s => s.DepartureDate).HasColumnName("departure_date");
            e.Property(s => s.Notes).HasColumnName("notes");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
        });

        // subscriptions
        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.Plan).HasColumnName("plan");
            e.Property(s => s.Status).HasColumnName("status");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
        });
    }
}

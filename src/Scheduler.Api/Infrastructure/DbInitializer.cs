using Microsoft.EntityFrameworkCore;

namespace Scheduler.Api.Infrastructure;

/// <summary>
/// Creates the schema and the PostgreSQL exclusion constraints that make double-booking
/// impossible at the database level, regardless of how many API instances are running.
/// For a production system this would be a versioned EF migration / DbUp script run by the
/// deployment pipeline; it lives here as idempotent SQL to keep the assessment self-contained.
/// </summary>
public static class DbInitializer
{
    // Half-open ranges '[)' so back-to-back appointments (10:00 end / 10:00 start) are allowed.
    // Constraints only apply to Confirmed rows, so cancelling an appointment frees the slot.
    private const string ConstraintsSql = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_appointments_end_after_start') THEN
                ALTER TABLE appointments
                    ADD CONSTRAINT ck_appointments_end_after_start CHECK (end_utc > start_utc);
            END IF;

            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_appointments_bay_no_overlap') THEN
                ALTER TABLE appointments
                    ADD CONSTRAINT ex_appointments_bay_no_overlap
                    EXCLUDE USING gist (service_bay_id WITH =, tstzrange(start_utc, end_utc, '[)') WITH &&)
                    WHERE (status = 'Confirmed');
            END IF;

            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_appointments_technician_no_overlap') THEN
                ALTER TABLE appointments
                    ADD CONSTRAINT ex_appointments_technician_no_overlap
                    EXCLUDE USING gist (technician_id WITH =, tstzrange(start_utc, end_utc, '[)') WITH &&)
                    WHERE (status = 'Confirmed');
            END IF;

            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ex_appointments_vehicle_no_overlap') THEN
                ALTER TABLE appointments
                    ADD CONSTRAINT ex_appointments_vehicle_no_overlap
                    EXCLUDE USING gist (vehicle_id WITH =, tstzrange(start_utc, end_utc, '[)') WITH &&)
                    WHERE (status = 'Confirmed');
            END IF;
        END
        $$;
        """;

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct = default)
    {
        if (!configuration.GetValue("Database:Initialize", true))
            return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        // The database container may still be starting; retry with a short back-off.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync(ct);
                await db.Database.ExecuteSqlRawAsync(ConstraintsSql, ct);
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}/10); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        if (configuration.GetValue("Seed:Enabled", false))
            await SeedData.SeedAsync(db, ct);

        logger.LogInformation("Database initialised");
    }
}

using Microsoft.EntityFrameworkCore;
using Scheduler.Api.Domain;

namespace Scheduler.Api.Infrastructure;

public class SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : DbContext(options)
{
    public DbSet<Dealership> Dealerships => Set<Dealership>();
    public DbSet<ServiceBay> ServiceBays => Set<ServiceBay>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<TechnicianSkill> TechnicianSkills => Set<TechnicianSkill>();
    public DbSet<ServiceType> ServiceTypes => Set<ServiceType>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("btree_gist");

        b.Entity<Dealership>(e =>
        {
            e.ToTable("dealerships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
            e.HasMany(x => x.Bays).WithOne().HasForeignKey(x => x.DealershipId);
            e.HasMany(x => x.Technicians).WithOne().HasForeignKey(x => x.DealershipId);
        });

        b.Entity<ServiceBay>(e =>
        {
            e.ToTable("service_bays");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        });

        b.Entity<Technician>(e =>
        {
            e.ToTable("technicians");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasMany(x => x.Skills).WithOne().HasForeignKey(x => x.TechnicianId);
        });

        b.Entity<TechnicianSkill>(e =>
        {
            e.ToTable("technician_skills");
            e.HasKey(x => new { x.TechnicianId, x.Skill });
            e.Property(x => x.Skill).HasMaxLength(100);
        });

        b.Entity<ServiceType>(e =>
        {
            e.ToTable("service_types");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequiredSkill).HasMaxLength(100).IsRequired();
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
        });

        b.Entity<Vehicle>(e =>
        {
            e.ToTable("vehicles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Vin).HasMaxLength(17).IsRequired();
            e.Property(x => x.Make).HasMaxLength(100).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Vin).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
        });

        // Column names are explicit (snake_case) because the exclusion constraints in
        // DbInitializer reference them in raw SQL.
        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.VehicleId).HasColumnName("vehicle_id");
            e.Property(x => x.DealershipId).HasColumnName("dealership_id");
            e.Property(x => x.ServiceTypeId).HasColumnName("service_type_id");
            e.Property(x => x.ServiceBayId).HasColumnName("service_bay_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.StartUtc).HasColumnName("start_utc");
            e.Property(x => x.EndUtc).HasColumnName("end_utc");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
            e.Property(x => x.CancelledUtc).HasColumnName("cancelled_utc");

            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId);
            e.HasOne<Dealership>().WithMany().HasForeignKey(x => x.DealershipId);
            e.HasOne<ServiceType>().WithMany().HasForeignKey(x => x.ServiceTypeId);
            e.HasOne<ServiceBay>().WithMany().HasForeignKey(x => x.ServiceBayId);
            e.HasOne<Technician>().WithMany().HasForeignKey(x => x.TechnicianId);

            // Supports the "what is busy in this window at this dealership" query.
            e.HasIndex(x => new { x.DealershipId, x.StartUtc });
            e.HasIndex(x => x.CustomerId);

            // One idempotency key per customer; NULL keys are unconstrained.
            e.HasIndex(x => new { x.CustomerId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");
        });
    }
}

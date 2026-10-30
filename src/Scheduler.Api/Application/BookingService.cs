using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Scheduler.Api.Domain;
using Scheduler.Api.Infrastructure;
using Scheduler.Api.Observability;

namespace Scheduler.Api.Application;

public sealed record BookAppointmentCommand(
    Guid CustomerId,
    Guid VehicleId,
    Guid DealershipId,
    Guid ServiceTypeId,
    DateTime StartUtc,
    string? IdempotencyKey);

public enum BookingOutcome
{
    Created,
    AlreadyExists,        // idempotent replay
    Cancelled,
    ValidationFailed,
    NotFound,
    NoCapacity,           // no free bay + qualified technician for the whole duration
    VehicleBusy,          // the vehicle already has an overlapping appointment
    IdempotencyKeyReused  // same key, different request
}

public sealed record BookingResult(
    BookingOutcome Outcome,
    Appointment? Appointment = null,
    IReadOnlyList<string>? Errors = null);

public sealed record AvailabilityResult(string TimeZoneId, IReadOnlyList<TimeInterval> Slots);

/// <summary>
/// Orchestrates booking: validate -> load resources -> allocate (pure) -> insert.
/// The allocation is computed optimistically; the database exclusion constraints guarantee that two
/// concurrent requests can never both win the same bay / technician / vehicle window. When the
/// database rejects an insert (SQLSTATE 23P01) we recompute against fresh data and retry.
/// </summary>
public sealed class BookingService(
    SchedulerDbContext db,
    TimeProvider clock,
    IOptions<BookingRules> rulesOptions,
    BookingMetrics metrics,
    ILogger<BookingService> logger)
{
    private const int MinAttempts = 3;
    private readonly BookingRules _rules = rulesOptions.Value;

    public async Task<BookingResult> BookAsync(BookAppointmentCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var activity = SchedulerTelemetry.Source.StartActivity("booking.create");
        activity?.SetTag("scheduler.dealership_id", cmd.DealershipId);
        activity?.SetTag("scheduler.service_type_id", cmd.ServiceTypeId);

        BookingResult result;
        try
        {
            result = await BookCoreAsync(cmd, ct);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.Record("Error", sw.Elapsed);
            throw;
        }

        activity?.SetTag("scheduler.outcome", result.Outcome.ToString());
        metrics.Record(result.Outcome.ToString(), sw.Elapsed);
        logger.LogInformation(
            "Booking {Outcome}: dealership {DealershipId}, vehicle {VehicleId}, start {StartUtc:o}, appointment {AppointmentId}",
            result.Outcome, cmd.DealershipId, cmd.VehicleId, cmd.StartUtc, result.Appointment?.Id);
        return result;
    }

    private async Task<BookingResult> BookCoreAsync(BookAppointmentCommand cmd, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(cmd.IdempotencyKey) ? null : cmd.IdempotencyKey.Trim();

        if (key is not null)
        {
            var existing = await db.Appointments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.CustomerId == cmd.CustomerId && a.IdempotencyKey == key, ct);
            if (existing is not null)
                return Replay(existing, cmd);
        }

        var dealership = await db.Dealerships.AsNoTracking().FirstOrDefaultAsync(d => d.Id == cmd.DealershipId, ct);
        if (dealership is null) return NotFound($"Dealership {cmd.DealershipId} was not found.");

        var serviceType = await db.ServiceTypes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == cmd.ServiceTypeId, ct);
        if (serviceType is null) return NotFound($"Service type {cmd.ServiceTypeId} was not found.");

        if (!await db.Customers.AnyAsync(c => c.Id == cmd.CustomerId, ct))
            return NotFound($"Customer {cmd.CustomerId} was not found.");

        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == cmd.VehicleId, ct);
        if (vehicle is null) return NotFound($"Vehicle {cmd.VehicleId} was not found.");
        if (vehicle.CustomerId != cmd.CustomerId)
            return Invalid("The vehicle does not belong to the customer.");

        var zone = ResolveZone(dealership);
        var errors = BookingPolicy.Validate(
            cmd.StartUtc, serviceType.DurationMinutes, clock.GetUtcNow().UtcDateTime,
            zone, dealership.OpenTime, dealership.CloseTime, _rules);
        if (errors.Count > 0)
            return new BookingResult(BookingOutcome.ValidationFailed, Errors: errors);

        var wanted = new TimeInterval(cmd.StartUtc, cmd.StartUtc.AddMinutes(serviceType.DurationMinutes));

        var bayIds = await db.ServiceBays.AsNoTracking()
            .Where(b => b.DealershipId == dealership.Id && b.IsActive)
            .Select(b => b.Id)
            .ToListAsync(ct);

        var techRows = await db.Technicians.AsNoTracking()
            .Where(t => t.DealershipId == dealership.Id && t.IsActive)
            .Select(t => new { t.Id, t.Name, Skills = t.Skills.Select(s => s.Skill).ToList() })
            .ToListAsync(ct);
        var technicians = techRows.Select(t => new TechnicianCandidate(t.Id, t.Name, t.Skills)).ToList();

        // Every lost race means another request just took a bay/technician, so the number of retries
        // that can ever be useful is bounded by the number of resources at this dealership.
        var maxAttempts = Math.Max(MinAttempts, Math.Max(bayIds.Count, technicians.Count) + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await VehicleIsBusyAsync(vehicle.Id, wanted, ct))
                return new BookingResult(BookingOutcome.VehicleBusy,
                    Errors: ["The vehicle already has an appointment overlapping the requested time."]);

            var busy = await LoadBusyAsync(dealership.Id, wanted, ct);
            var allocation = ResourceAllocator.Allocate(wanted, serviceType.RequiredSkill, bayIds, technicians, busy);
            if (allocation is null)
                return new BookingResult(BookingOutcome.NoCapacity,
                    Errors: ["No service bay and qualified technician are both available for the entire service duration."]);

            var appointment = new Appointment
            {
                Id = Guid.NewGuid(),
                CustomerId = cmd.CustomerId,
                VehicleId = cmd.VehicleId,
                DealershipId = cmd.DealershipId,
                ServiceTypeId = cmd.ServiceTypeId,
                ServiceBayId = allocation.BayId,
                TechnicianId = allocation.TechnicianId,
                StartUtc = wanted.StartUtc,
                EndUtc = wanted.EndUtc,
                Status = AppointmentStatus.Confirmed,
                IdempotencyKey = key,
                CreatedUtc = clock.GetUtcNow().UtcDateTime
            };

            db.Appointments.Add(appointment);
            try
            {
                await db.SaveChangesAsync(ct);
                return new BookingResult(BookingOutcome.Created, appointment);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23P01" })
            {
                // Lost a race: another request took the same bay/technician/vehicle window between our
                // read and our insert. Forget the failed entity, re-read the busy set and try again.
                db.Entry(appointment).State = EntityState.Detached;
                metrics.ConflictRetry();
                logger.LogWarning("Exclusion constraint hit on attempt {Attempt}/{Max}; recomputing allocation", attempt, maxAttempts);
            }
            catch (DbUpdateException ex) when (key is not null && ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Two requests with the same idempotency key raced; the other one won.
                db.Entry(appointment).State = EntityState.Detached;
                var existing = await db.Appointments.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.CustomerId == cmd.CustomerId && a.IdempotencyKey == key, ct);
                if (existing is not null) return Replay(existing, cmd);
                throw;
            }
        }

        return new BookingResult(BookingOutcome.NoCapacity,
            Errors: ["The requested slot was taken by concurrent bookings. Please retry or choose another time."]);
    }

    public async Task<BookingResult> CancelAsync(Guid appointmentId, CancellationToken ct)
    {
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
            return NotFound($"Appointment {appointmentId} was not found.");

        if (appointment.Status == AppointmentStatus.Cancelled)
            return new BookingResult(BookingOutcome.Cancelled, appointment); // idempotent

        var now = clock.GetUtcNow().UtcDateTime;
        if (appointment.StartUtc <= now)
            return Invalid("Appointments that have already started cannot be cancelled.");

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledUtc = now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Appointment {AppointmentId} cancelled", appointmentId);
        return new BookingResult(BookingOutcome.Cancelled, appointment);
    }

    public Task<Appointment?> GetAsync(Guid id, CancellationToken ct)
        => db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<Appointment>> ListAsync(
        Guid? customerId, Guid? dealershipId, DateTime? fromUtc, DateTime? toUtc, int take, CancellationToken ct)
    {
        var query = db.Appointments.AsNoTracking().AsQueryable();
        if (customerId is not null) query = query.Where(a => a.CustomerId == customerId);
        if (dealershipId is not null) query = query.Where(a => a.DealershipId == dealershipId);
        if (fromUtc is not null) query = query.Where(a => a.EndUtc > fromUtc);
        if (toUtc is not null) query = query.Where(a => a.StartUtc < toUtc);

        return await query.OrderBy(a => a.StartUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    public async Task<AvailabilityResult?> GetAvailabilityAsync(
        Guid dealershipId, Guid serviceTypeId, DateOnly localDate, CancellationToken ct)
    {
        var dealership = await db.Dealerships.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealershipId, ct);
        var serviceType = await db.ServiceTypes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceTypeId, ct);
        if (dealership is null || serviceType is null) return null;

        var zone = ResolveZone(dealership);
        var window = new TimeInterval(
            AvailabilityCalculator.LocalToUtc(localDate, dealership.OpenTime, zone),
            AvailabilityCalculator.LocalToUtc(localDate, dealership.CloseTime, zone));

        var bayIds = await db.ServiceBays.AsNoTracking()
            .Where(b => b.DealershipId == dealershipId && b.IsActive)
            .Select(b => b.Id).ToListAsync(ct);

        var techRows = await db.Technicians.AsNoTracking()
            .Where(t => t.DealershipId == dealershipId && t.IsActive)
            .Select(t => new { t.Id, t.Name, Skills = t.Skills.Select(s => s.Skill).ToList() })
            .ToListAsync(ct);
        var technicians = techRows.Select(t => new TechnicianCandidate(t.Id, t.Name, t.Skills)).ToList();

        var busy = await LoadBusyAsync(dealershipId, window, ct);

        var slots = AvailabilityCalculator.FreeSlots(
            localDate, serviceType.DurationMinutes, serviceType.RequiredSkill, zone,
            dealership.OpenTime, dealership.CloseTime, clock.GetUtcNow().UtcDateTime, _rules,
            bayIds, technicians, busy);

        return new AvailabilityResult(dealership.TimeZoneId, slots);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<List<BusySlot>> LoadBusyAsync(Guid dealershipId, TimeInterval window, CancellationToken ct)
        => (await db.Appointments.AsNoTracking()
                .Where(a => a.DealershipId == dealershipId
                            && a.Status == AppointmentStatus.Confirmed
                            && a.StartUtc < window.EndUtc
                            && a.EndUtc > window.StartUtc)
                .Select(a => new { a.ServiceBayId, a.TechnicianId, a.VehicleId, a.StartUtc, a.EndUtc })
                .ToListAsync(ct))
            .Select(a => new BusySlot(
                a.ServiceBayId, a.TechnicianId, a.VehicleId,
                new TimeInterval(DateTime.SpecifyKind(a.StartUtc, DateTimeKind.Utc), DateTime.SpecifyKind(a.EndUtc, DateTimeKind.Utc))))
            .ToList();

    private Task<bool> VehicleIsBusyAsync(Guid vehicleId, TimeInterval wanted, CancellationToken ct)
        => db.Appointments.AnyAsync(a => a.VehicleId == vehicleId
                                         && a.Status == AppointmentStatus.Confirmed
                                         && a.StartUtc < wanted.EndUtc
                                         && a.EndUtc > wanted.StartUtc, ct);

    private static BookingResult Replay(Appointment existing, BookAppointmentCommand cmd)
        => existing.VehicleId == cmd.VehicleId
           && existing.DealershipId == cmd.DealershipId
           && existing.ServiceTypeId == cmd.ServiceTypeId
           && existing.StartUtc == cmd.StartUtc
            ? new BookingResult(BookingOutcome.AlreadyExists, existing)
            : new BookingResult(BookingOutcome.IdempotencyKeyReused,
                Errors: ["The Idempotency-Key was already used with a different request."]);

    private static BookingResult NotFound(string message)
        => new(BookingOutcome.NotFound, Errors: [message]);

    private static BookingResult Invalid(string message)
        => new(BookingOutcome.ValidationFailed, Errors: [message]);

    private static TimeZoneInfo ResolveZone(Dealership dealership)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(dealership.TimeZoneId); }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException($"Dealership {dealership.Id} has an unknown time zone '{dealership.TimeZoneId}'.", ex);
        }
    }
}

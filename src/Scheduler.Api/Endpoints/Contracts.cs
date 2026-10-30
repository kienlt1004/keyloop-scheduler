using Scheduler.Api.Domain;

namespace Scheduler.Api.Endpoints;

public sealed record CreateAppointmentRequest(
    Guid CustomerId,
    Guid VehicleId,
    Guid DealershipId,
    Guid ServiceTypeId,
    DateTimeOffset StartTime);

public sealed record AppointmentResponse(
    Guid Id,
    Guid CustomerId,
    Guid VehicleId,
    Guid DealershipId,
    Guid ServiceTypeId,
    Guid ServiceBayId,
    Guid TechnicianId,
    DateTime StartUtc,
    DateTime EndUtc,
    string Status,
    DateTime CreatedUtc)
{
    public static AppointmentResponse From(Appointment a) => new(
        a.Id, a.CustomerId, a.VehicleId, a.DealershipId, a.ServiceTypeId, a.ServiceBayId, a.TechnicianId,
        DateTime.SpecifyKind(a.StartUtc, DateTimeKind.Utc),
        DateTime.SpecifyKind(a.EndUtc, DateTimeKind.Utc),
        a.Status.ToString(),
        DateTime.SpecifyKind(a.CreatedUtc, DateTimeKind.Utc));
}

public sealed record SlotResponse(DateTime StartUtc, DateTime EndUtc);

public sealed record AvailabilityResponse(
    Guid DealershipId,
    Guid ServiceTypeId,
    DateOnly Date,
    string TimeZoneId,
    IReadOnlyList<SlotResponse> Slots);

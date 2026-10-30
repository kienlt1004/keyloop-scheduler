using Microsoft.AspNetCore.Mvc;
using Scheduler.Api.Application;

namespace Scheduler.Api.Endpoints;

public static class AppointmentEndpoints
{
    private const string ProblemBase = "urn:keyloop:scheduler:";

    public static IEndpointRouteBuilder MapSchedulerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        var appointments = api.MapGroup("/appointments").WithTags("Appointments");
        appointments.MapPost("/", CreateAsync)
            .WithSummary("Book a service appointment")
            .WithDescription("Allocates a free service bay and a qualified technician for the whole service duration. " +
                             "Send an Idempotency-Key header to make retries safe.")
            .Produces<AppointmentResponse>(StatusCodes.Status201Created)
            .Produces<AppointmentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        appointments.MapGet("/{id:guid}", GetAsync)
            .WithSummary("Get an appointment")
            .Produces<AppointmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        appointments.MapGet("/", ListAsync)
            .WithSummary("List appointments (filter by customer, dealership and time range)")
            .Produces<IReadOnlyList<AppointmentResponse>>();

        appointments.MapDelete("/{id:guid}", CancelAsync)
            .WithSummary("Cancel an appointment (frees its bay and technician)")
            .Produces<AppointmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        api.MapGet("/dealerships/{dealershipId:guid}/availability", AvailabilityAsync)
            .WithTags("Availability")
            .WithSummary("List bookable start times for a service on a dealership-local date")
            .Produces<AvailabilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateAppointmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        BookingService service,
        HttpContext http,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.CustomerId == Guid.Empty) errors["customerId"] = ["customerId is required."];
        if (request.VehicleId == Guid.Empty) errors["vehicleId"] = ["vehicleId is required."];
        if (request.DealershipId == Guid.Empty) errors["dealershipId"] = ["dealershipId is required."];
        if (request.ServiceTypeId == Guid.Empty) errors["serviceTypeId"] = ["serviceTypeId is required."];
        if (idempotencyKey is { Length: > 128 }) errors["Idempotency-Key"] = ["Idempotency-Key must be at most 128 characters."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);

        var command = new BookAppointmentCommand(
            request.CustomerId, request.VehicleId, request.DealershipId, request.ServiceTypeId,
            request.StartTime.UtcDateTime, idempotencyKey);

        var result = await service.BookAsync(command, ct);

        switch (result.Outcome)
        {
            case BookingOutcome.Created:
                return Results.Created($"/api/v1/appointments/{result.Appointment!.Id}", AppointmentResponse.From(result.Appointment));
            case BookingOutcome.AlreadyExists:
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Ok(AppointmentResponse.From(result.Appointment!));
            default:
                return ToProblem(result);
        }
    }

    private static async Task<IResult> GetAsync(Guid id, BookingService service, CancellationToken ct)
    {
        var appointment = await service.GetAsync(id, ct);
        return appointment is null
            ? Problem(StatusCodes.Status404NotFound, "not-found", "Appointment not found", $"Appointment {id} was not found.")
            : Results.Ok(AppointmentResponse.From(appointment));
    }

    private static async Task<IResult> ListAsync(
        BookingService service,
        Guid? customerId,
        Guid? dealershipId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? take,
        CancellationToken ct)
    {
        var items = await service.ListAsync(customerId, dealershipId, from?.UtcDateTime, to?.UtcDateTime, take ?? 50, ct);
        return Results.Ok(items.Select(AppointmentResponse.From).ToList());
    }

    private static async Task<IResult> CancelAsync(Guid id, BookingService service, CancellationToken ct)
    {
        var result = await service.CancelAsync(id, ct);
        return result.Outcome == BookingOutcome.Cancelled
            ? Results.Ok(AppointmentResponse.From(result.Appointment!))
            : ToProblem(result);
    }

    private static async Task<IResult> AvailabilityAsync(
        Guid dealershipId,
        [FromQuery] Guid serviceTypeId,
        [FromQuery] DateOnly date,
        BookingService service,
        CancellationToken ct)
    {
        var result = await service.GetAvailabilityAsync(dealershipId, serviceTypeId, date, ct);
        if (result is null)
            return Problem(StatusCodes.Status404NotFound, "not-found", "Not found", "Dealership or service type was not found.");

        return Results.Ok(new AvailabilityResponse(
            dealershipId, serviceTypeId, date, result.TimeZoneId,
            result.Slots.Select(s => new SlotResponse(s.StartUtc, s.EndUtc)).ToList()));
    }

    private static IResult ToProblem(BookingResult result)
    {
        var messages = result.Errors?.ToArray() ?? Array.Empty<string>();
        var detail = string.Join(" ", messages);
        return result.Outcome switch
        {
            BookingOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "not-found", "Resource not found", detail),
            BookingOutcome.NoCapacity => Problem(StatusCodes.Status409Conflict, "no-capacity", "No capacity", detail),
            BookingOutcome.VehicleBusy => Problem(StatusCodes.Status409Conflict, "vehicle-busy", "Vehicle already booked", detail),
            BookingOutcome.IdempotencyKeyReused => Problem(StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Idempotency key reused", detail),
            _ => Results.ValidationProblem(
                new Dictionary<string, string[]> { ["request"] = messages },
                statusCode: StatusCodes.Status422UnprocessableEntity)
        };
    }

    private static IResult Problem(int status, string type, string title, string detail)
        => Results.Problem(statusCode: status, type: ProblemBase + type, title: title, detail: detail);
}

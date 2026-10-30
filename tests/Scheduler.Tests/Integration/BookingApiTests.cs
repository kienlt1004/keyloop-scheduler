using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scheduler.Api.Domain;
using Scheduler.Api.Endpoints;
using Scheduler.Api.Infrastructure;

namespace Scheduler.Tests.Integration;

[Collection(ApiCollection.Name)]
public class BookingApiTests(ApiFixture fixture)
{
    private HttpClient Client => fixture.Client;

    // Tuesday 3 Nov 2026 (UTC == London time in winter).
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 3, hour, minute, 0, TimeSpan.Zero);

    private static object Request(Guid customer, Guid vehicle, TestDealership d, DateTimeOffset start) => new
    {
        customerId = customer,
        vehicleId = vehicle,
        dealershipId = d.DealershipId,
        serviceTypeId = d.ServiceTypeId,
        startTime = start
    };

    private Task<HttpResponseMessage> Book(Guid customer, Guid vehicle, TestDealership d, DateTimeOffset start, string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/appointments")
        {
            Content = JsonContent.Create(Request(customer, vehicle, d, start))
        };
        if (idempotencyKey is not null) message.Headers.Add("Idempotency-Key", idempotencyKey);
        return Client.SendAsync(message);
    }

    [Fact]
    public async Task Booking_succeeds_and_persists_an_appointment_linking_customer_vehicle_technician_and_bay()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, vehicle, d, At(9));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AppointmentResponse>())!;
        Assert.Equal(customer, body.CustomerId);
        Assert.Equal(vehicle, body.VehicleId);
        Assert.Equal(d.BayIds[0], body.ServiceBayId);
        Assert.Equal(d.TechnicianIds[0], body.TechnicianId);
        Assert.Equal(At(9).UtcDateTime, body.StartUtc);
        Assert.Equal(At(10).UtcDateTime, body.EndUtc); // 60-minute service
        Assert.Equal("Confirmed", body.Status);
        Assert.Equal($"/api/v1/appointments/{body.Id}", response.Headers.Location!.ToString());

        // Persisted: visible through a fresh DbContext and through the GET endpoint.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();
        Assert.True(await db.Appointments.AnyAsync(a => a.Id == body.Id));

        var fetched = await Client.GetFromJsonAsync<AppointmentResponse>($"/api/v1/appointments/{body.Id}");
        Assert.Equal(body.Id, fetched!.Id);
    }

    [Fact]
    public async Task Booking_fails_with_409_when_no_technician_has_the_required_skill()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["Brakes"]], requiredSkill: "MOT");
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, vehicle, d, At(9));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Overlapping_booking_is_rejected_when_the_only_bay_and_technician_are_taken()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (c1, v1) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (c2, v2) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        Assert.Equal(HttpStatusCode.Created, (await Book(c1, v1, d, At(9))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(c2, v2, d, At(9, 30))).StatusCode); // partial overlap
    }

    [Fact]
    public async Task Overlapping_booking_uses_a_different_bay_and_technician_when_available()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 2, [["MOT"], ["MOT"]]);
        var (c1, v1) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (c2, v2) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (c3, v3) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var first = (await (await Book(c1, v1, d, At(9))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        var secondResponse = await Book(c2, v2, d, At(9));
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<AppointmentResponse>())!;

        Assert.NotEqual(first.ServiceBayId, second.ServiceBayId);
        Assert.NotEqual(first.TechnicianId, second.TechnicianId);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(c3, v3, d, At(9))).StatusCode); // capacity exhausted
    }

    [Fact]
    public async Task Back_to_back_bookings_on_the_same_resources_are_allowed()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (c1, v1) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (c2, v2) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        Assert.Equal(HttpStatusCode.Created, (await Book(c1, v1, d, At(9))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Book(c2, v2, d, At(10))).StatusCode);
    }

    [Fact]
    public async Task Booking_outside_opening_hours_returns_422()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, vehicle, d, At(7));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Booking_in_the_past_returns_422()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, vehicle, d, new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Booking_with_a_vehicle_that_belongs_to_another_customer_returns_422()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, _) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (_, someoneElsesVehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, someoneElsesVehicle, d, At(9));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Booking_for_an_unknown_dealership_returns_404()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var response = await Book(customer, vehicle, d with { DealershipId = Guid.NewGuid() }, At(9));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_same_vehicle_cannot_be_booked_twice_at_overlapping_times()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 2, [["MOT"], ["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        Assert.Equal(HttpStatusCode.Created, (await Book(customer, vehicle, d, At(9))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Book(customer, vehicle, d, At(9, 30))).StatusCode);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_returns_the_original_appointment()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 2, [["MOT"], ["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var key = Guid.NewGuid().ToString();

        var first = await Book(customer, vehicle, d, At(9), key);
        var retry = await Book(customer, vehicle, d, At(9), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        var a = (await first.Content.ReadFromJsonAsync<AppointmentResponse>())!;
        var b = (await retry.Content.ReadFromJsonAsync<AppointmentResponse>())!;
        Assert.Equal(a.Id, b.Id);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();
        Assert.Equal(1, await db.Appointments.CountAsync(x => x.CustomerId == customer));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_returns_422()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 2, [["MOT"], ["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Created, (await Book(customer, vehicle, d, At(9), key)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Book(customer, vehicle, d, At(13), key)).StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_appointment_frees_its_bay_and_technician()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (c1, v1) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        var (c2, v2) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);

        var created = (await (await Book(c1, v1, d, At(9))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await Book(c2, v2, d, At(9))).StatusCode);

        var cancel = await Client.DeleteAsync($"/api/v1/appointments/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Cancelled", (await cancel.Content.ReadFromJsonAsync<AppointmentResponse>())!.Status);

        Assert.Equal(HttpStatusCode.Created, (await Book(c2, v2, d, At(9))).StatusCode);
    }

    [Fact]
    public async Task Availability_excludes_start_times_that_would_overlap_an_existing_booking()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);
        var (customer, vehicle) = await TestData.CreateCustomerWithVehicleAsync(fixture.Services);
        Assert.Equal(HttpStatusCode.Created, (await Book(customer, vehicle, d, At(9))).StatusCode);

        var json = await Client.GetStringAsync(
            $"/api/v1/dealerships/{d.DealershipId}/availability?serviceTypeId={d.ServiceTypeId}&date=2026-11-03");
        using var doc = JsonDocument.Parse(json);
        var starts = doc.RootElement.GetProperty("slots").EnumerateArray()
            .Select(s => s.GetProperty("startUtc").GetDateTime().ToUniversalTime())
            .ToHashSet();

        Assert.Contains(At(8).UtcDateTime, starts);
        Assert.DoesNotContain(At(8, 15).UtcDateTime, starts);
        Assert.DoesNotContain(At(9).UtcDateTime, starts);
        Assert.Contains(At(10).UtcDateTime, starts);
    }

    [Fact]
    public async Task Health_endpoints_report_ready()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/ready")).StatusCode);
    }
}

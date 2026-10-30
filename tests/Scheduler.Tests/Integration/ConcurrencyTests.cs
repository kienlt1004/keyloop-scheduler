using System.Net;
using System.Net.Http.Json;

namespace Scheduler.Tests.Integration;

/// <summary>
/// The core correctness property of the system: no matter how many requests race for the same
/// window, the number of confirmed appointments never exceeds the capacity of bays and technicians.
/// </summary>
[Collection(ApiCollection.Name)]
public class ConcurrencyTests(ApiFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private async Task<HttpStatusCode[]> RaceAsync(TestDealership d, int requests)
    {
        var customers = new List<(Guid Customer, Guid Vehicle)>();
        for (var i = 0; i < requests; i++)
            customers.Add(await TestData.CreateCustomerWithVehicleAsync(fixture.Services));

        using var gate = new ManualResetEventSlim(false);
        var tasks = customers.Select(c => Task.Run(async () =>
        {
            gate.Wait();
            var response = await fixture.Client.PostAsJsonAsync("/api/v1/appointments", new
            {
                customerId = c.Customer,
                vehicleId = c.Vehicle,
                dealershipId = d.DealershipId,
                serviceTypeId = d.ServiceTypeId,
                startTime = Start
            });
            return response.StatusCode;
        })).ToArray();

        gate.Set(); // release every request at once
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Ten_simultaneous_requests_for_one_bay_and_one_technician_yield_exactly_one_booking()
    {
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 1, [["MOT"]]);

        var results = await RaceAsync(d, requests: 10);

        Assert.Equal(1, results.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Capacity_is_bounded_by_the_scarcer_resource()
    {
        // 3 bays but only 2 qualified technicians => at most 2 concurrent MOTs.
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 3, [["MOT"], ["MOT"], ["Brakes"]]);

        var results = await RaceAsync(d, requests: 8);

        Assert.Equal(2, results.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(6, results.Count(r => r == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Losers_of_a_race_are_reallocated_to_free_resources_instead_of_failing()
    {
        // 4 bays / 4 technicians and 4 racers: every request must succeed even though they
        // all initially pick the same "first free" resources.
        var d = await TestData.CreateDealershipAsync(fixture.Services, bays: 4, [["MOT"], ["MOT"], ["MOT"], ["MOT"]]);

        var results = await RaceAsync(d, requests: 4);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r));
    }
}

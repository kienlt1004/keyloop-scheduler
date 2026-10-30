using Microsoft.Extensions.DependencyInjection;
using Scheduler.Api.Domain;
using Scheduler.Api.Infrastructure;

namespace Scheduler.Tests.Integration;

public sealed record TestDealership(Guid DealershipId, Guid ServiceTypeId, IReadOnlyList<Guid> BayIds, IReadOnlyList<Guid> TechnicianIds);

/// <summary>Creates isolated reference data per test so tests never interfere with each other.</summary>
public static class TestData
{
    public static async Task<TestDealership> CreateDealershipAsync(
        IServiceProvider services,
        int bays,
        string[][] technicianSkills,
        string requiredSkill = "MOT",
        int durationMinutes = 60)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();

        var dealership = new Dealership
        {
            Id = Guid.NewGuid(),
            Name = $"Test Dealership {Guid.NewGuid():N}",
            TimeZoneId = "Europe/London",
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(17, 0)
        };

        for (var i = 0; i < bays; i++)
            dealership.Bays.Add(new ServiceBay { Id = Guid.NewGuid(), Name = $"Bay {i + 1}" });

        for (var i = 0; i < technicianSkills.Length; i++)
        {
            var tech = new Technician { Id = Guid.NewGuid(), Name = $"Tech {i + 1}" };
            foreach (var skill in technicianSkills[i])
                tech.Skills.Add(new TechnicianSkill { Skill = skill });
            dealership.Technicians.Add(tech);
        }

        var serviceType = new ServiceType
        {
            Id = Guid.NewGuid(),
            Name = $"{requiredSkill} service",
            DurationMinutes = durationMinutes,
            RequiredSkill = requiredSkill
        };

        db.ServiceTypes.Add(serviceType);
        db.Dealerships.Add(dealership);
        await db.SaveChangesAsync();

        return new TestDealership(
            dealership.Id,
            serviceType.Id,
            dealership.Bays.Select(b => b.Id).ToList(),
            dealership.Technicians.Select(t => t.Id).ToList());
    }

    public static async Task<(Guid CustomerId, Guid VehicleId)> CreateCustomerWithVehicleAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();

        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Test Customer", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var vehicle = new Vehicle
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Vin = Guid.NewGuid().ToString("N")[..17].ToUpperInvariant(),
            Make = "Test",
            Model = "Car",
            Year = 2024
        };
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();

        return (customer.Id, vehicle.Id);
    }
}

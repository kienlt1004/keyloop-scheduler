using Microsoft.EntityFrameworkCore;
using Scheduler.Api.Domain;

namespace Scheduler.Api.Infrastructure;

/// <summary>Deterministic reference data so README / cURL examples work out of the box.</summary>
public static class SeedData
{
    public static readonly Guid LondonDealership = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ManchesterDealership = new("22222222-2222-2222-2222-222222222222");

    public static readonly Guid OilChange = new("cccccccc-0000-0000-0000-000000000001");
    public static readonly Guid Mot = new("cccccccc-0000-0000-0000-000000000002");
    public static readonly Guid BrakeService = new("cccccccc-0000-0000-0000-000000000003");
    public static readonly Guid FullService = new("cccccccc-0000-0000-0000-000000000004");
    public static readonly Guid EvBatteryCheck = new("cccccccc-0000-0000-0000-000000000005");

    public static readonly Guid CustomerJane = new("dddddddd-0000-0000-0000-000000000001");
    public static readonly Guid CustomerOmar = new("dddddddd-0000-0000-0000-000000000002");
    public static readonly Guid VehicleJane1 = new("eeeeeeee-0000-0000-0000-000000000001");
    public static readonly Guid VehicleJane2 = new("eeeeeeee-0000-0000-0000-000000000002");
    public static readonly Guid VehicleOmar1 = new("eeeeeeee-0000-0000-0000-000000000003");

    public static async Task SeedAsync(SchedulerDbContext db, CancellationToken ct)
    {
        if (await db.Dealerships.AnyAsync(ct))
            return;

        db.ServiceTypes.AddRange(
            new ServiceType { Id = OilChange, Name = "Oil Change & Filter", DurationMinutes = 60, RequiredSkill = "General" },
            new ServiceType { Id = Mot, Name = "MOT Test", DurationMinutes = 60, RequiredSkill = "MOT" },
            new ServiceType { Id = BrakeService, Name = "Brake Service", DurationMinutes = 120, RequiredSkill = "Brakes" },
            new ServiceType { Id = FullService, Name = "Full Service", DurationMinutes = 180, RequiredSkill = "General" },
            new ServiceType { Id = EvBatteryCheck, Name = "EV Battery Check", DurationMinutes = 90, RequiredSkill = "EV" });

        var london = new Dealership
        {
            Id = LondonDealership,
            Name = "Keyloop Motors London",
            TimeZoneId = "Europe/London",
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(17, 0)
        };
        for (var i = 1; i <= 3; i++)
            london.Bays.Add(new ServiceBay { Id = new Guid($"aaaaaaaa-0000-0000-0000-00000000000{i}"), Name = $"Bay {i}" });
        london.Technicians.Add(Tech("bbbbbbbb-0000-0000-0000-000000000001", "Alice", "General", "MOT"));
        london.Technicians.Add(Tech("bbbbbbbb-0000-0000-0000-000000000002", "Bob", "General", "Brakes"));
        london.Technicians.Add(Tech("bbbbbbbb-0000-0000-0000-000000000003", "Carol", "General", "EV", "MOT"));

        // A deliberately tiny dealership (1 bay, 1 technician) that makes conflicts easy to demo.
        var manchester = new Dealership
        {
            Id = ManchesterDealership,
            Name = "Keyloop Motors Manchester",
            TimeZoneId = "Europe/London",
            OpenTime = new TimeOnly(9, 0),
            CloseTime = new TimeOnly(17, 30)
        };
        manchester.Bays.Add(new ServiceBay { Id = new Guid("aaaaaaaa-0000-0000-0000-000000000101"), Name = "Bay 1" });
        manchester.Technicians.Add(Tech("bbbbbbbb-0000-0000-0000-000000000101", "Dave", "General", "MOT"));

        db.Dealerships.AddRange(london, manchester);

        db.Customers.AddRange(
            new Customer { Id = CustomerJane, FullName = "Jane Doe", Email = "jane.doe@example.com" },
            new Customer { Id = CustomerOmar, FullName = "Omar Haddad", Email = "omar.haddad@example.com" });
        await db.SaveChangesAsync(ct);

        db.Vehicles.AddRange(
            new Vehicle { Id = VehicleJane1, CustomerId = CustomerJane, Vin = "WVWZZZ1KZ6W000001", Make = "Volkswagen", Model = "Golf", Year = 2019 },
            new Vehicle { Id = VehicleJane2, CustomerId = CustomerJane, Vin = "WVWZZZ1KZ6W000002", Make = "Tesla", Model = "Model 3", Year = 2022 },
            new Vehicle { Id = VehicleOmar1, CustomerId = CustomerOmar, Vin = "SALLAAA146A000003", Make = "Land Rover", Model = "Defender", Year = 2021 });
        await db.SaveChangesAsync(ct);
    }

    private static Technician Tech(string id, string name, params string[] skills)
    {
        var t = new Technician { Id = new Guid(id), Name = name };
        foreach (var s in skills)
            t.Skills.Add(new TechnicianSkill { Skill = s });
        return t;
    }
}

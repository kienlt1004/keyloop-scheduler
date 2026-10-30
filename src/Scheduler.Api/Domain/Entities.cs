namespace Scheduler.Api.Domain;

public enum AppointmentStatus
{
    Confirmed,
    Cancelled
}

public class Dealership
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>IANA time zone id (e.g. "Europe/London"). All stored instants are UTC.</summary>
    public string TimeZoneId { get; set; } = "Europe/London";

    /// <summary>Local opening time (inclusive).</summary>
    public TimeOnly OpenTime { get; set; } = new(8, 0);

    /// <summary>Local closing time. A service must finish at or before this time.</summary>
    public TimeOnly CloseTime { get; set; } = new(17, 0);

    public List<ServiceBay> Bays { get; set; } = new();
    public List<Technician> Technicians { get; set; } = new();
}

public class ServiceBay
{
    public Guid Id { get; set; }
    public Guid DealershipId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Technician
{
    public Guid Id { get; set; }
    public Guid DealershipId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<TechnicianSkill> Skills { get; set; } = new();
}

public class TechnicianSkill
{
    public Guid TechnicianId { get; set; }
    public string Skill { get; set; } = "";
}

public class ServiceType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int DurationMinutes { get; set; }

    /// <summary>A technician must hold this skill to perform the service.</summary>
    public string RequiredSkill { get; set; } = "";
}

public class Customer
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

public class Vehicle
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Vin { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
}

public class Appointment
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid VehicleId { get; set; }
    public Guid DealershipId { get; set; }
    public Guid ServiceTypeId { get; set; }
    public Guid ServiceBayId { get; set; }
    public Guid TechnicianId { get; set; }

    /// <summary>Start of the booked window, UTC (inclusive).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>End of the booked window, UTC (exclusive).</summary>
    public DateTime EndUtc { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }
}

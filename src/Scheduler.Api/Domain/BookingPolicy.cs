using System.Globalization;

namespace Scheduler.Api.Domain;

public sealed class BookingRules
{
    /// <summary>Appointments must start on a multiple of this many minutes (must divide 60).</summary>
    public int SlotMinutes { get; set; } = 15;

    /// <summary>How far ahead a customer may book.</summary>
    public int MaxDaysAhead { get; set; } = 180;
}

/// <summary>A half-open UTC interval [StartUtc, EndUtc). Back-to-back intervals do not overlap.</summary>
public readonly record struct TimeInterval(DateTime StartUtc, DateTime EndUtc)
{
    public bool Overlaps(TimeInterval other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;
}

/// <summary>Pure, side-effect-free business rules about *when* an appointment may be booked.</summary>
public static class BookingPolicy
{
    public static IReadOnlyList<string> Validate(
        DateTime startUtc,
        int durationMinutes,
        DateTime nowUtc,
        TimeZoneInfo zone,
        TimeOnly open,
        TimeOnly close,
        BookingRules rules)
    {
        var errors = new List<string>();

        if (startUtc.Kind != DateTimeKind.Utc)
            errors.Add("Start time must be expressed in UTC.");
        if (durationMinutes <= 0)
            errors.Add("Service duration must be positive.");
        if (errors.Count > 0)
            return errors;

        if (startUtc <= nowUtc)
            errors.Add("Start time must be in the future.");
        else if (startUtc > nowUtc.AddDays(rules.MaxDaysAhead))
            errors.Add($"Appointments can be booked at most {rules.MaxDaysAhead} days ahead.");

        if (startUtc.Ticks % TimeSpan.FromMinutes(rules.SlotMinutes).Ticks != 0)
            errors.Add($"Start time must be aligned to {rules.SlotMinutes}-minute slots.");

        var endUtc = startUtc.AddMinutes(durationMinutes);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone);

        if (localStart.Date != localEnd.Date)
        {
            errors.Add("Appointment must start and end on the same day (dealership local time).");
        }
        else
        {
            if (localStart.DayOfWeek == DayOfWeek.Sunday)
                errors.Add("The dealership is closed on Sundays.");

            var s = TimeOnly.FromDateTime(localStart);
            var e = TimeOnly.FromDateTime(localEnd);
            if (s < open || e > close)
                errors.Add(
                    $"Appointment must fall within opening hours " +
                    $"{open.ToString("HH:mm", CultureInfo.InvariantCulture)}-" +
                    $"{close.ToString("HH:mm", CultureInfo.InvariantCulture)} ({zone.Id} local time).");
        }

        return errors;
    }
}

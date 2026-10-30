namespace Scheduler.Api.Domain;

/// <summary>Computes bookable start times for one dealership-local day.</summary>
public static class AvailabilityCalculator
{
    public static DateTime LocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone);

    public static IReadOnlyList<TimeInterval> FreeSlots(
        DateOnly localDate,
        int durationMinutes,
        string requiredSkill,
        TimeZoneInfo zone,
        TimeOnly open,
        TimeOnly close,
        DateTime nowUtc,
        BookingRules rules,
        IReadOnlyCollection<Guid> activeBayIds,
        IReadOnlyCollection<TechnicianCandidate> technicians,
        IReadOnlyCollection<BusySlot> busy)
    {
        var result = new List<TimeInterval>();
        if (durationMinutes <= 0 || localDate.DayOfWeek == DayOfWeek.Sunday)
            return result;

        var openUtc = LocalToUtc(localDate, open, zone);
        var closeUtc = LocalToUtc(localDate, close, zone);

        for (var start = openUtc; start.AddMinutes(durationMinutes) <= closeUtc; start = start.AddMinutes(rules.SlotMinutes))
        {
            var interval = new TimeInterval(start, start.AddMinutes(durationMinutes));

            if (BookingPolicy.Validate(start, durationMinutes, nowUtc, zone, open, close, rules).Count > 0)
                continue;

            if (ResourceAllocator.Allocate(interval, requiredSkill, activeBayIds, technicians, busy) is not null)
                result.Add(interval);
        }

        return result;
    }
}

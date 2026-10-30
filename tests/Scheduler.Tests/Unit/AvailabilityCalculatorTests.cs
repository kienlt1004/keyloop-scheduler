using Scheduler.Api.Domain;

namespace Scheduler.Tests.Unit;

public class AvailabilityCalculatorTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeOnly Open = new(8, 0);
    private static readonly TimeOnly Close = new(17, 0);
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Bay = Guid.NewGuid();
    private static readonly TechnicianCandidate Tech = new(Guid.NewGuid(), "Alice", ["MOT"]);

    private static DateTime Utc(int hour, int minute = 0) => new(2026, 11, 3, hour, minute, 0, DateTimeKind.Utc);

    private static IReadOnlyList<TimeInterval> Slots(DateOnly date, params BusySlot[] busy)
        => AvailabilityCalculator.FreeSlots(date, 60, "MOT", London, Open, Close, Now, new BookingRules(), [Bay], [Tech], busy);

    [Fact]
    public void Empty_day_offers_every_slot_until_the_last_service_that_still_finishes_by_closing()
    {
        var slots = Slots(new DateOnly(2026, 11, 3));
        Assert.Equal(Utc(8), slots.First().StartUtc);
        Assert.Equal(Utc(16), slots.Last().StartUtc); // 16:00 + 60 min = 17:00 (closing)
        Assert.Equal(33, slots.Count);               // 08:00..16:00 every 15 minutes
    }

    [Fact]
    public void A_booking_blocks_every_start_time_that_would_overlap_it()
    {
        var busy = new BusySlot(Bay, Tech.Id, Guid.NewGuid(), new TimeInterval(Utc(9), Utc(10)));
        var starts = Slots(new DateOnly(2026, 11, 3), busy).Select(s => s.StartUtc).ToHashSet();

        Assert.Contains(Utc(8), starts);            // ends exactly when the booking starts
        Assert.DoesNotContain(Utc(8, 15), starts);  // overlaps the first 15 minutes
        Assert.DoesNotContain(Utc(9), starts);
        Assert.DoesNotContain(Utc(9, 45), starts);
        Assert.Contains(Utc(10), starts);           // starts exactly when the booking ends
    }

    [Fact]
    public void Sunday_has_no_availability()
        => Assert.Empty(Slots(new DateOnly(2026, 11, 8)));
}

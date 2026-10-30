using Scheduler.Api.Domain;

namespace Scheduler.Tests.Unit;

public class BookingPolicyTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeOnly Open = new(8, 0);
    private static readonly TimeOnly Close = new(17, 0);
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc); // Monday
    private static readonly BookingRules Rules = new();

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    private static IReadOnlyList<string> Validate(DateTime start, int minutes = 60, DateTime? now = null)
        => BookingPolicy.Validate(start, minutes, now ?? Now, London, Open, Close, Rules);

    [Fact]
    public void Weekday_slot_inside_opening_hours_is_valid()
        => Assert.Empty(Validate(Utc(3, 9)));

    [Fact]
    public void Start_in_the_past_is_rejected()
        => Assert.Contains(Validate(Utc(2, 7)), e => e.Contains("future"));

    [Fact]
    public void Start_not_aligned_to_slot_is_rejected()
        => Assert.Contains(Validate(Utc(3, 9, 10)), e => e.Contains("aligned"));

    [Fact]
    public void Start_before_opening_is_rejected()
        => Assert.Contains(Validate(Utc(3, 7)), e => e.Contains("opening hours"));

    [Fact]
    public void Service_may_finish_exactly_at_closing_time()
        => Assert.Empty(Validate(Utc(3, 16)));

    [Fact]
    public void Service_overrunning_closing_time_is_rejected()
        => Assert.Contains(Validate(Utc(3, 16, 30)), e => e.Contains("opening hours"));

    [Fact]
    public void Sunday_is_rejected()
        => Assert.Contains(Validate(Utc(8, 9)), e => e.Contains("Sunday"));

    [Fact]
    public void Booking_too_far_ahead_is_rejected()
        => Assert.Contains(Validate(Now.AddDays(181)), e => e.Contains("ahead"));

    [Fact]
    public void Non_utc_start_is_rejected()
        => Assert.NotEmpty(Validate(DateTime.SpecifyKind(Utc(3, 9), DateTimeKind.Unspecified)));

    [Fact]
    public void Opening_hours_follow_local_time_during_daylight_saving()
    {
        var summerNow = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        // 07:00Z is 08:00 BST (open); 06:45Z is 07:45 BST (closed).
        Assert.Empty(Validate(new DateTime(2026, 7, 1, 7, 0, 0, DateTimeKind.Utc), now: summerNow));
        Assert.NotEmpty(Validate(new DateTime(2026, 7, 1, 6, 45, 0, DateTimeKind.Utc), now: summerNow));
    }

    [Fact]
    public void Intervals_are_half_open_so_back_to_back_slots_do_not_overlap()
    {
        var first = new TimeInterval(Utc(3, 9), Utc(3, 10));
        Assert.False(first.Overlaps(new TimeInterval(Utc(3, 10), Utc(3, 11))));
        Assert.True(first.Overlaps(new TimeInterval(Utc(3, 9, 30), Utc(3, 10, 30))));
        Assert.True(first.Overlaps(new TimeInterval(Utc(3, 9, 15), Utc(3, 9, 45))));
    }
}

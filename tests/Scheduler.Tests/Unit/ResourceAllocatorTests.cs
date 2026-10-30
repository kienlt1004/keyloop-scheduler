using Scheduler.Api.Domain;

namespace Scheduler.Tests.Unit;

public class ResourceAllocatorTests
{
    private static readonly Guid Bay1 = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Bay2 = new("00000000-0000-0000-0000-000000000002");
    private static readonly TechnicianCandidate Alice = new(new("10000000-0000-0000-0000-000000000001"), "Alice", ["MOT"]);
    private static readonly TechnicianCandidate Bob = new(new("10000000-0000-0000-0000-000000000002"), "Bob", ["Brakes"]);

    private static DateTime Utc(int hour, int minute = 0) => new(2026, 11, 3, hour, minute, 0, DateTimeKind.Utc);
    private static readonly TimeInterval Wanted = new(Utc(9), Utc(10));

    private static BusySlot Busy(Guid bay, Guid tech, DateTime start, DateTime end)
        => new(bay, tech, Guid.NewGuid(), new TimeInterval(start, end));

    [Fact]
    public void Allocates_first_free_bay_and_qualified_technician()
    {
        var result = ResourceAllocator.Allocate(Wanted, "MOT", [Bay1, Bay2], [Alice, Bob], []);
        Assert.Equal(new Allocation(Bay1, Alice.Id), result);
    }

    [Fact]
    public void Returns_null_when_no_technician_has_the_required_skill()
        => Assert.Null(ResourceAllocator.Allocate(Wanted, "EV", [Bay1], [Alice, Bob], []));

    [Fact]
    public void Skill_matching_is_case_insensitive()
        => Assert.NotNull(ResourceAllocator.Allocate(Wanted, "mot", [Bay1], [Alice], []));

    [Fact]
    public void Skips_a_bay_that_is_busy_for_part_of_the_window()
    {
        var busy = new[] { Busy(Bay1, Bob.Id, Utc(9, 30), Utc(10, 30)) };
        var result = ResourceAllocator.Allocate(Wanted, "MOT", [Bay1, Bay2], [Alice, Bob], busy);
        Assert.Equal(Bay2, result!.BayId);
    }

    [Fact]
    public void Returns_null_when_the_only_qualified_technician_is_busy_even_if_a_bay_is_free()
    {
        var busy = new[] { Busy(Bay2, Alice.Id, Utc(8, 30), Utc(9, 30)) };
        Assert.Null(ResourceAllocator.Allocate(Wanted, "MOT", [Bay1, Bay2], [Alice, Bob], busy));
    }

    [Fact]
    public void Returns_null_when_every_bay_is_busy_even_if_a_technician_is_free()
    {
        var busy = new[] { Busy(Bay1, Bob.Id, Utc(9), Utc(10)) };
        Assert.Null(ResourceAllocator.Allocate(Wanted, "MOT", [Bay1], [Alice], busy));
    }

    [Fact]
    public void Resources_are_reusable_immediately_after_a_previous_appointment_ends()
    {
        var busy = new[] { Busy(Bay1, Alice.Id, Utc(8), Utc(9)) };
        Assert.NotNull(ResourceAllocator.Allocate(Wanted, "MOT", [Bay1], [Alice], busy));
    }

    [Fact]
    public void Returns_null_when_dealership_has_no_bays()
        => Assert.Null(ResourceAllocator.Allocate(Wanted, "MOT", [], [Alice], []));

    [Fact]
    public void Allocation_is_deterministic_regardless_of_input_order()
    {
        var a = ResourceAllocator.Allocate(Wanted, "MOT", [Bay2, Bay1], [Bob, Alice], []);
        var b = ResourceAllocator.Allocate(Wanted, "MOT", [Bay1, Bay2], [Alice, Bob], []);
        Assert.Equal(a, b);
    }
}

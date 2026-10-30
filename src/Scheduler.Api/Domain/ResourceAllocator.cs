namespace Scheduler.Api.Domain;

public sealed record TechnicianCandidate(Guid Id, string Name, IReadOnlyCollection<string> Skills);

/// <summary>An existing confirmed appointment, reduced to the resources it occupies.</summary>
public sealed record BusySlot(Guid BayId, Guid TechnicianId, Guid VehicleId, TimeInterval Interval);

public sealed record Allocation(Guid BayId, Guid TechnicianId);

/// <summary>
/// Pure allocation logic: given what is already booked, find a free bay AND a free, qualified
/// technician for the whole interval. Deterministic ordering makes behaviour reproducible and testable.
/// The database exclusion constraints remain the final arbiter under concurrency.
/// </summary>
public static class ResourceAllocator
{
    public static IReadOnlyList<Guid> FreeBays(
        TimeInterval wanted,
        IEnumerable<Guid> activeBayIds,
        IReadOnlyCollection<BusySlot> busy)
        => activeBayIds
            .Where(id => !busy.Any(b => b.BayId == id && b.Interval.Overlaps(wanted)))
            .OrderBy(id => id)
            .ToList();

    public static IReadOnlyList<TechnicianCandidate> FreeQualifiedTechnicians(
        TimeInterval wanted,
        string requiredSkill,
        IEnumerable<TechnicianCandidate> technicians,
        IReadOnlyCollection<BusySlot> busy)
        => technicians
            .Where(t => t.Skills.Any(s => string.Equals(s, requiredSkill, StringComparison.OrdinalIgnoreCase)))
            .Where(t => !busy.Any(b => b.TechnicianId == t.Id && b.Interval.Overlaps(wanted)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Id)
            .ToList();

    public static Allocation? Allocate(
        TimeInterval wanted,
        string requiredSkill,
        IEnumerable<Guid> activeBayIds,
        IEnumerable<TechnicianCandidate> technicians,
        IReadOnlyCollection<BusySlot> busy)
    {
        var bays = FreeBays(wanted, activeBayIds, busy);
        if (bays.Count == 0) return null;

        var techs = FreeQualifiedTechnicians(wanted, requiredSkill, technicians, busy);
        if (techs.Count == 0) return null;

        return new Allocation(bays[0], techs[0].Id);
    }
}

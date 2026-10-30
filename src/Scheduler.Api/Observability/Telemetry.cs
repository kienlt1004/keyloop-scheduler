using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Scheduler.Api.Observability;

public static class SchedulerTelemetry
{
    public const string Name = "Keyloop.Scheduler";
    public static readonly ActivitySource Source = new(Name);
}

/// <summary>Business-level metrics (technical HTTP/runtime metrics come from OpenTelemetry instrumentation).</summary>
public sealed class BookingMetrics : IDisposable
{
    private readonly Meter _meter = new(SchedulerTelemetry.Name);
    private readonly Counter<long> _bookings;
    private readonly Counter<long> _conflictRetries;
    private readonly Histogram<double> _duration;

    public BookingMetrics()
    {
        _bookings = _meter.CreateCounter<long>("scheduler.bookings", description: "Booking attempts by outcome");
        _conflictRetries = _meter.CreateCounter<long>("scheduler.booking.conflict_retries",
            description: "Times the DB exclusion constraint rejected an allocation and the booking was retried");
        _duration = _meter.CreateHistogram<double>("scheduler.booking.duration", unit: "ms",
            description: "End-to-end booking latency");
    }

    public void Record(string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "outcome", outcome } };
        _bookings.Add(1, tags);
        _duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void ConflictRetry() => _conflictRetries.Add(1);

    public void Dispose() => _meter.Dispose();
}

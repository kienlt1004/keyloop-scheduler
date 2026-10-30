using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scheduler.Api.Application;
using Scheduler.Api.Domain;
using Scheduler.Api.Endpoints;
using Scheduler.Api.Infrastructure;
using Scheduler.Api.Observability;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---- Logging: structured JSON to stdout, enriched with trace/span ids ---------------------------
builder.Host.UseSerilog((context, services, cfg) => cfg
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "keyloop-scheduler-api")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// ---- Persistence --------------------------------------------------------------------------------
// The connection string is resolved lazily so test hosts can override configuration.
builder.Services.AddDbContext<SchedulerDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Scheduler")));

// ---- Application services -----------------------------------------------------------------------
builder.Services.Configure<BookingRules>(builder.Configuration.GetSection("Booking"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BookingMetrics>();
builder.Services.AddScoped<BookingService>();

// ---- Observability: traces + metrics via OpenTelemetry (OTLP export only when configured) -------
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("keyloop-scheduler-api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddNpgsql().AddSource(SchedulerTelemetry.Name);
        if (exportOtlp) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMeter(SchedulerTelemetry.Name);
        if (exportOtlp) m.AddOtlpExporter();
    });

// ---- HTTP plumbing ------------------------------------------------------------------------------
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Keyloop Unified Service Scheduler", Version = "v1" }));
builder.Services.AddHealthChecks().AddDbContextCheck<SchedulerDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services, app.Configuration);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI();

// Liveness: process is up. Readiness: dependencies (PostgreSQL) are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapSchedulerEndpoints();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;

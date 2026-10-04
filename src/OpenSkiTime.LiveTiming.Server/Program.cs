using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Server;

var builder = WebApplication.CreateBuilder(args);
var maxBodyBytes = Math.Clamp(builder.Configuration.GetValue("LiveTiming:MaxBodyBytes", 2 * 1024 * 1024), 65536, 8 * 1024 * 1024);
var creationPerMinute = Math.Clamp(builder.Configuration.GetValue("LiveTiming:CreationPerMinute", 5), 1, 100);
var requestsPerMinute = Math.Clamp(builder.Configuration.GetValue("LiveTiming:RequestsPerMinute", 3000), 60, 100000);
var maxConnections = Math.Clamp(builder.Configuration.GetValue("LiveTiming:MaxConnections", 1000), 10, 10000);
var connectionsPerClient = Math.Clamp(builder.Configuration.GetValue("LiveTiming:ConnectionsPerClient", 200), 1, maxConnections);
var trustForwardedFor = builder.Configuration.GetValue("LiveTiming:TrustForwardedFor", false);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = maxBodyBytes;
    o.Limits.MaxConcurrentConnections = maxConnections; o.Limits.MaxConcurrentUpgradedConnections = maxConnections;
});
builder.Logging.AddFilter(PublisherAudit.Category, LogLevel.Information);
builder.Services.Configure<JsonOptions>(o => { foreach (var c in LiveJson.Options.Converters) { o.SerializerOptions.Converters.Add(c); } });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PublisherKeys>();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 4096; o.MaximumParallelInvocationsPerClient = 1; })
    .AddJsonProtocol(o => { foreach (var c in LiveJson.Options.Converters) { o.PayloadSerializerOptions.Converters.Add(c); } });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("creation", context => RateLimitPartition.GetFixedWindowLimiter(Client(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = creationPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Long-lived viewer sockets are bounded per client so one source cannot take every connection slot.
    o.AddPolicy("viewers", context => RateLimitPartition.GetConcurrencyLimiter(Client(context),
        _ => new ConcurrencyLimiterOptions { PermitLimit = connectionsPerClient, QueueLimit = 0 }));
    // Partitioned per client: one flooding source exhausts only its own budget, not the shared service.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(Client(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
if (trustForwardedFor)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        // Behind exactly one trusted ingress proxy, which appends the real client address as the last entry.
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor; o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
    });
}
var app = builder.Build();
_ = app.Services.GetRequiredService<SessionStore>(); // Fail before listening if signing configuration is invalid.
_ = app.Services.GetRequiredService<PublisherKeys>(); // Fail before listening if publisher keys are missing or invalid.
var audit = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(PublisherAudit.Category);
if (trustForwardedFor) { app.UseForwardedHeaders(); }
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try { await next(context); }
    catch (LiveValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Invalid live timing payload."); }
    catch (LiveConflictException) { context.Response.StatusCode = 409; await context.Response.WriteAsync("Full snapshot required."); }
    catch (LiveCapacityException) { context.Response.StatusCode = 503; await context.Response.WriteAsync("Session capacity reached."); }
});
app.UseRateLimiter();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "Running", processId = Environment.ProcessId }));
app.MapGet("/.well-known/security.txt", (HttpContext context, IConfiguration config, TimeProvider time) =>
    Results.Text(SecurityTxt.Create(PublicBase(context, config), config, time.GetUtcNow()), "text/plain; charset=utf-8"));
app.MapGet("/api/sessions", (SessionStore store) => Results.Ok(store.List()));
app.MapPost("/api/sessions", (HttpContext context, SessionStore store, PublisherKeys keys, IConfiguration config) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    if (keys.Authenticate(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null) is not { } publisher)
    { return Results.Unauthorized(); }
    var session = store.Create(PublicBase(context, config));
    PublisherAudit.SessionCreated(audit, session.SessionId, publisher);
    return Results.Ok(session);
}).RequireRateLimiting("creation");
app.MapGet("/api/sessions/{id:guid}/state", (Guid id, SessionStore store) => store.Read(id) is { } state ? Results.Ok(state) : Results.NotFound());
app.MapPut("/api/sessions/{id:guid}/state", async (Guid id, LiveSnapshot state, HttpContext context, SessionStore store, IHubContext<LiveHub> hub) =>
{
    if (Authorize(context, store, id) is not { } expires) { return Results.Unauthorized(); }
    store.Replace(id, expires, state);
    await hub.Clients.Group(id.ToString()).SendAsync("State", state);
    return Results.Ok();
});
app.MapPost("/api/sessions/{id:guid}/events", async (Guid id, LiveEvent update, HttpContext context, SessionStore store, IHubContext<LiveHub> hub) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    var state = store.Apply(id, update);
    await hub.Clients.Group(id.ToString()).SendAsync("State", state);
    return Results.Ok();
});
app.MapPost("/api/sessions/{id:guid}/pause", async (Guid id, HttpContext context, SessionStore store, IHubContext<LiveHub> hub) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    await hub.Clients.Group(id.ToString()).SendAsync("State", store.Pause(id));
    return Results.Ok();
});
app.MapDelete("/api/sessions/{id:guid}", Delete);
app.MapDelete("/api/sessions/{id:guid}/data", Delete);
app.MapHub<LiveHub>("/live").RequireRateLimiting("viewers");
app.MapGet("/r/{id:guid}", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "index.html"), "text/html"));
app.Run();

static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
static string PublicBase(HttpContext context, IConfiguration config)
    => config["LiveTiming:PublicBaseUrl"] ?? $"{context.Request.Scheme}://{context.Request.Host}";
static DateTimeOffset? Authorize(HttpContext context, SessionStore store, Guid id)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Authorize(id, header[7..]) : null;
}
static async Task<IResult> Delete(Guid id, HttpContext context, SessionStore store, IHubContext<LiveHub> hub)
{
    if (Authorize(context, store, id) is not { } expires) { return Results.Unauthorized(); }
    store.Delete(id, expires);
    await hub.Clients.Group(id.ToString()).SendAsync("State", (LiveSnapshot?)null);
    return Results.NoContent();
}

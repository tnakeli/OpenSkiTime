using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Server;

var builder = WebApplication.CreateBuilder(args);
var maxBodyBytes = Math.Clamp(builder.Configuration.GetValue("LiveTiming:MaxBodyBytes", 2 * 1024 * 1024), 65536, 8 * 1024 * 1024);
var creationPerMinute = Math.Clamp(builder.Configuration.GetValue("LiveTiming:CreationPerMinute", 5), 1, 100);
var requestsPerMinute = Math.Clamp(builder.Configuration.GetValue("LiveTiming:RequestsPerMinute", 3000), 60, 100000);
var maxConnections = Math.Clamp(builder.Configuration.GetValue("LiveTiming:MaxConnections", 1000), 10, 10000);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = maxBodyBytes;
    o.Limits.MaxConcurrentConnections = maxConnections; o.Limits.MaxConcurrentUpgradedConnections = maxConnections;
});
builder.Services.Configure<JsonOptions>(o => { foreach (var c in LiveJson.Options.Converters) { o.SerializerOptions.Converters.Add(c); } });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 4096; o.MaximumParallelInvocationsPerClient = 1; })
    .AddJsonProtocol(o => { foreach (var c in LiveJson.Options.Converters) { o.PayloadSerializerOptions.Converters.Add(c); } });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("creation", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = creationPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("global",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
_ = app.Services.GetRequiredService<SessionStore>(); // Fail before listening if signing configuration is invalid.
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
app.MapPost("/api/sessions", (HttpContext context, SessionStore store, IConfiguration config) =>
{
    var publicBase = config["LiveTiming:PublicBaseUrl"] ?? $"{context.Request.Scheme}://{context.Request.Host}";
    return Results.Ok(store.Create(publicBase));
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
app.MapHub<LiveHub>("/live");
app.MapGet("/r/{id:guid}", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "index.html"), "text/html"));
app.Run();

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

using System.Globalization;
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
builder.Services.AddSingleton<RadioBroadcaster>();
builder.Services.AddHostedService<RadioMonitor>();
builder.Services.AddSingleton<ServerStats>();
builder.Services.AddHostedService<ServerStatsSampler>();
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
var serverStats = app.Services.GetRequiredService<ServerStats>();
var clock = app.Services.GetRequiredService<TimeProvider>();
app.Use(async (context, next) =>
{
    // Response times of requests that answer; a viewer's socket (or long poll) on the hub lasts as long as the viewer.
    if (context.Request.Path.Equals("/live", StringComparison.OrdinalIgnoreCase)) { await next(context); return; }
    var started = clock.GetTimestamp();
    try { await next(context); }
    finally { serverStats.Response(clock.GetElapsedTime(started)); }
});
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers[LiveProtocol.Header] = LiveProtocol.Version.ToString(CultureInfo.InvariantCulture);
    // A publisher declaring another protocol is refused before any mutation; requests without the header are treated as current.
    if (context.Request.Headers.TryGetValue(LiveProtocol.Header, out var declared)
        && declared.ToString() != LiveProtocol.Version.ToString(CultureInfo.InvariantCulture))
    { context.Response.StatusCode = 426; await context.Response.WriteAsync("Unsupported live timing protocol version."); return; }
    try { await next(context); }
    catch (LiveValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Invalid live timing payload."); }
    catch (LiveConflictException) { context.Response.StatusCode = 409; await context.Response.WriteAsync("Full snapshot required."); }
    catch (LiveCapacityException) { context.Response.StatusCode = 503; await context.Response.WriteAsync("Session capacity reached."); }
});
app.UseRateLimiter();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "Running", processId = Environment.ProcessId, protocol = LiveProtocol.Version }));
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
app.MapPut("/api/sessions/{id:guid}/state", async (Guid id, LiveSnapshot state, HttpContext context, SessionStore store, IHubContext<LiveHub> hub, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is not { } expires) { return Results.Unauthorized(); }
    store.Replace(id, expires, state);
    await hub.Clients.Group(id.ToString()).SendAsync("State", state);
    await radio.SendAsync(id); // A new state can make queued radio clips stale.
    return Results.Ok();
});
// Operator load report for the publisher of this session; never public, as it describes the whole server.
app.MapGet("/api/sessions/{id:guid}/stats", (Guid id, HttpContext context, SessionStore store, ServerStats stats) =>
    Authorize(context, store, id) is null ? Results.Unauthorized() : Results.Ok(stats.Read(id, store.Read(id) is not null)));
app.MapPost("/api/sessions/{id:guid}/events", async (Guid id, LiveEvent update, HttpContext context, SessionStore store, IHubContext<LiveHub> hub, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    var state = store.Apply(id, update);
    await hub.Clients.Group(id.ToString()).SendAsync("State", state);
    await radio.SendAsync(id);
    return Results.Ok();
});
app.MapPost("/api/sessions/{id:guid}/pause", async (Guid id, HttpContext context, SessionStore store, IHubContext<LiveHub> hub, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    await hub.Clients.Group(id.ToString()).SendAsync("State", store.Pause(id));
    await radio.SendAsync(id);
    return Results.Ok();
});
// Optional race radio. Uploads and status need the session's publisher token; clip audio is public like the race view.
app.MapPut("/api/sessions/{id:guid}/radio/status", async (Guid id, RadioStatusUpdate update, HttpContext context, SessionStore store, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    var reply = store.RadioStatus(id, update);
    await radio.SendAsync(id);
    return Results.Ok(reply);
});
app.MapPut("/api/sessions/{id:guid}/radio/music", async (Guid id, RadioMusicPlaylist playlist, HttpContext context, SessionStore store, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    var reply = store.SetMusic(id, playlist);
    await radio.SendAsync(id);
    return Results.Ok(reply);
});
// One MP3 track as the raw request body. Only an authorized publisher may send the larger body this endpoint allows.
app.MapPut("/api/sessions/{id:guid}/radio/music/{trackId}", async (Guid id, string trackId, HttpContext context, SessionStore store, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    if (!RadioMusic.IsId(trackId) || context.Request.ContentType != RadioMusic.ContentType) { return Results.BadRequest(); }
    if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
    { limit.MaxRequestBodySize = RadioMusic.MaxTrackBytes; }
    using var body = new MemoryStream();
    var buffer = new byte[81920]; int count;
    while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        if (body.Length + count > RadioMusic.MaxTrackBytes) { return Results.StatusCode(413); }
        body.Write(buffer, 0, count);
    }
    var reply = store.AddMusic(id, trackId, body.ToArray());
    await radio.SendAsync(id);
    return Results.Ok(reply);
});
// Content addressed and immutable: viewers and proxies may cache a track.
app.MapGet("/api/sessions/{id:guid}/radio/music/{trackId}", (Guid id, string trackId, HttpContext context, SessionStore store) =>
{
    if (!RadioMusic.IsId(trackId) || store.MusicAudio(id, trackId) is not { } music) { return Results.NotFound(); }
    context.Response.Headers.CacheControl = "public, max-age=86400, immutable";
    return Results.Bytes(music, RadioMusic.ContentType, enableRangeProcessing: true);
});
app.MapPost("/api/sessions/{id:guid}/radio/clips", async (Guid id, RadioClipUpload clip, HttpContext context, SessionStore store, RadioBroadcaster radio) =>
{
    if (Authorize(context, store, id) is null) { return Results.Unauthorized(); }
    store.AddClip(id, clip);
    await radio.SendAsync(id);
    return Results.Ok();
});
app.MapGet("/api/sessions/{id:guid}/radio/clips/{clipId:guid}", (Guid id, Guid clipId, SessionStore store) =>
    store.ClipAudio(id, clipId) is { } clip ? Results.Bytes(clip.Audio, clip.ContentType) : Results.NotFound());
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
static async Task<IResult> Delete(Guid id, HttpContext context, SessionStore store, IHubContext<LiveHub> hub, RadioBroadcaster radio)
{
    if (Authorize(context, store, id) is not { } expires) { return Results.Unauthorized(); }
    store.Delete(id, expires); // Radio clips are part of the session and are removed with it.
    await hub.Clients.Group(id.ToString()).SendAsync("State", (LiveSnapshot?)null);
    await radio.SendAsync(id, force: true);
    return Results.NoContent();
}

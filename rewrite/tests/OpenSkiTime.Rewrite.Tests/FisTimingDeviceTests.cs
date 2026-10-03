using System.Net;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class FisTimingDeviceTests
{
    [Fact]
    public async Task CatalogueUsesSharedKeyHeaderIncludesHistoricalDevicesAndKeepsPrecision()
    {
        var expired = new FisTimingDevice(1, "Synthetic Timer", "SYN.001T.20", 2025, 1, "SYNTHETIC", 1, "Timer", 10000, null, false);
        using var handler = new Handler(request =>
        {
            Assert.Equal("https://api.fis-ski.com/homologation/timing-devices?includeExpired=true", request.RequestUri!.ToString());
            Assert.Equal("synthetic-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { expired }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var device = Assert.Single(await new FisTimingDeviceClient(http).GetAsync("synthetic-key"));
        Assert.Equal(expired, device); Assert.False(device.Valid); Assert.Equal(10000, device.Precision);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[{\"id\":1}]")]
    [InlineData("not json")]
    public async Task InvalidCatalogueCannotBecomeEquipment(string body)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<DomainValidationException>(() => new FisTimingDeviceClient(http).GetAsync("synthetic-key"));
    }

    [Fact]
    public async Task PermissionFailureDoesNotExposeServerBodyOrKey()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("synthetic-secret") });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new FisTimingDeviceClient(http).GetAsync("synthetic-secret"));
        Assert.Contains("403", error.Message); Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}

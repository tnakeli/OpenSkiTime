# Contract — FIS Placeholder

**Feature**: 001-event-series-management
**Module**: `OpenSkiTime.Fis.Placeholder`

## Purpose

Reserve the eventual seam for "Update from FIS API" without performing any
network operation in this feature. Maps to FR-011 and SC-007.

## Interface

```csharp
namespace OpenSkiTime.Fis;

public interface IFisCompetitionUpdater
{
    /// <summary>
    /// Updates a Competition's basic data from the FIS API.
    /// In feature 001 this implementation MUST throw or return
    /// <see cref="FisUpdateResult.NotImplemented"/> without performing
    /// any network I/O.
    /// </summary>
    Task<FisUpdateResult> UpdateAsync(Guid competitionId, CancellationToken ct = default);
}

public abstract record FisUpdateResult
{
    public sealed record NotImplemented(string UserMessage) : FisUpdateResult;
    public sealed record Updated(int FieldsChanged) : FisUpdateResult;          // future feature
    public sealed record Failed(string Reason) : FisUpdateResult;                // future feature
}
```

## Implementation in this feature

```csharp
internal sealed class NotImplementedFisUpdater : IFisCompetitionUpdater
{
    public Task<FisUpdateResult> UpdateAsync(Guid competitionId, CancellationToken ct = default)
        => Task.FromResult<FisUpdateResult>(
            new FisUpdateResult.NotImplemented(
                "FIS API update is not yet available in this release."));
}
```

## DI registration

```csharp
services.AddSingleton<IFisCompetitionUpdater, NotImplementedFisUpdater>();
```

No `HttpClient`, no `IHttpClientFactory`, no `System.Net.Http` reference is
introduced by this module's csproj. The csproj has zero NuGet dependencies.

## UI behavior

The Competition Editor's "Update from FIS API" button is **enabled** but
visually marked (subdued style + tooltip + status banner). Clicking it
calls `IFisCompetitionUpdater.UpdateAsync`, gets `NotImplemented`, and
shows the `UserMessage` in a non-blocking inline notification.

## Test contract

- `OpenSkiTime.Desktop.Tests/FisPlaceholderButtonTests`:
  - registers a tracking `IHttpClientFactory` whose `CreateClient` throws.
  - simulates the click on the FIS button via Avalonia.Headless.
  - asserts the inline notification shows the configured `UserMessage`.
  - asserts no `HttpClient` was constructed (the throwing factory is
    never invoked).

This single test pins SC-007 ("zero outgoing network requests") at the UI
seam.

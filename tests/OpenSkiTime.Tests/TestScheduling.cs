using Xunit;

// These boundary tests run OCR and durable SQLite capture concurrently inside
// each scenario. Avoid contention between unrelated scenarios on CI disks;
// production drain deadlines and in-scenario concurrency remain unchanged.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

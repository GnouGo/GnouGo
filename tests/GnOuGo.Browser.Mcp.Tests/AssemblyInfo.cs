using Xunit;

// Parallel cold Chromium launches compete with the navigation and reload deadlines under test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

using Xunit.Sdk;
using Xunit.v3;

// Parallel cold Chromium launches compete with the navigation and reload deadlines under test.
[assembly: Parallelization(Mode = ParallelMode.None)]

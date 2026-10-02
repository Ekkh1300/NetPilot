using Xunit;

// Every test in this project talks to the same two process-wide singletons
// (MobileVpnApi.Instance and MobileVpnService.Instance) and to the same machine state
// (a listener port, the interface snapshot file, WinHTTP). Parallel classes would race
// each other and produce failures that say nothing about the product, so the suite runs
// sequentially - it is small enough that this costs a few seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

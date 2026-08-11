using System.Runtime.Versioning;
using Xunit;

// Everything under test is macOS-only.
[assembly: SupportedOSPlatform("macos")]

// These tests touch process-global state (the Objective-C delegate class, the registered notification
// categories) and one of them measures CPU time, so they must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

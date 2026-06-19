using Xunit;

namespace PinballWizard.Cli.Tests;

// Tests that redirect the global Console.Out/Error must not run in parallel
// with each other or with any other test, or the shared static Console.Out
// races (one class disposes its StringWriter while another writes) →
// ObjectDisposedException. This collection serializes them.
//
// The marker class name deliberately avoids the "Collection" suffix to satisfy
// CA1711; the collection name is carried by the string argument, not the class name.
[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public sealed class ConsoleCaptureMarker { }

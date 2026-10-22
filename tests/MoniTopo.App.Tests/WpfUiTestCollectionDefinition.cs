namespace MoniTopo.App.Tests;

// WPF/BAML resources use process-wide caches. These tests deliberately create
// short-lived STA dispatchers, but must not initialize those caches concurrently.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiTestCollectionDefinition
{
    public const string Name = "WPF UI";
}

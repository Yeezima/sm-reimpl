using Xunit;

namespace SmMapmaker.Tests;

/// <summary>The test classes that set the process-wide <c>SM_MAPS_DIR</c> run in this one collection, so xunit never runs them in parallel (a pre-existing flake).</summary>
[CollectionDefinition(Name)]
public sealed class MapsDirCollection
{
    public const string Name = "SM_MAPS_DIR";
}

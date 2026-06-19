using Xunit;

// Several tests redirect Console.Error and mutate the static Log.MinimumLevel, so the
// whole assembly runs sequentially to keep that shared state deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

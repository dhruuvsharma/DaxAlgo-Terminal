using Xunit;

// This assembly realizes real WPF trees, and WPF objects are thread-affine: the Application, its resource
// dictionaries and every control belong to the thread that made them (see WpfTestApp, which owns the one
// STA thread the Application-dependent tests share). Running classes in parallel puts unrelated WPF tests
// on overlapping threads for no gain — the whole suite runs in about a second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

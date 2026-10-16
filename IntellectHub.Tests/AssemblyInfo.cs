// AppLog is process-global, so a test that captures log lines would otherwise
// collect lines emitted by tests running at the same time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
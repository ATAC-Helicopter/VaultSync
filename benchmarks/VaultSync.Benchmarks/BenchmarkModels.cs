namespace VaultSync.Benchmarks;

internal sealed record BenchmarkOptions(
    int HistoryEvents,
    int FileCount,
    int Iterations,
    bool Enforce,
    string? OutputPath)
{
    private const int DefaultHistoryEvents = 10_000;
    private const int DefaultFileCount = 100_000;
    private const int DefaultIterations = 7;

    public static BenchmarkOptions Parse(string[] arguments)
    {
        int historyEvents = DefaultHistoryEvents;
        int fileCount = DefaultFileCount;
        int iterations = DefaultIterations;
        bool enforce = false;
        string? outputPath = null;

        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--history-events":
                    historyEvents = ReadPositiveInt(arguments, ref index, argument);
                    break;
                case "--file-count":
                    fileCount = ReadPositiveInt(arguments, ref index, argument);
                    break;
                case "--iterations":
                    iterations = ReadPositiveInt(arguments, ref index, argument);
                    break;
                case "--output":
                    outputPath = ReadValue(arguments, ref index, argument);
                    break;
                case "--enforce":
                    enforce = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option '{argument}'.");
            }
        }

        return new BenchmarkOptions(historyEvents, fileCount, iterations, enforce, outputPath);
    }

    private static int ReadPositiveInt(string[] arguments, ref int index, string option)
    {
        string value = ReadValue(arguments, ref index, option);
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{option} requires a positive integer.");
        return parsed;
    }

    private static string ReadValue(string[] arguments, ref int index, string option)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }
}

internal sealed record BenchmarkReport(
    int SchemaVersion,
    DateTime RecordedUtc,
    string SourceCommit,
    string Configuration,
    MachineProfile Machine,
    FixtureProfile Fixture,
    IReadOnlyList<BenchmarkMeasurement> Measurements);

internal sealed record MachineProfile(
    string OperatingSystem,
    string Architecture,
    string Runtime,
    int LogicalProcessors,
    bool ServerGc);

internal sealed record FixtureProfile(int HistoryEvents, int FileCount, int Iterations);

internal sealed record BenchmarkMeasurement(
    string Name,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    long P95AllocatedBytes,
    double BudgetP95Milliseconds,
    long BudgetP95AllocatedBytes,
    bool Passed);

namespace Post.DeviceSimulator;

public enum SimulatorScenario
{
    Auth,
    Full,
    Load
}

public sealed class SimulatorOptions
{
    private SimulatorOptions(
        Uri hubUrl,
        string deviceId,
        string deviceToken,
        SimulatorScenario scenario,
        int count,
        TimeSpan timeout,
        bool showHelp)
    {
        HubUrl = hubUrl;
        DeviceId = deviceId;
        DeviceToken = deviceToken;
        Scenario = scenario;
        Count = count;
        Timeout = timeout;
        ShowHelp = showHelp;
    }

    public Uri HubUrl { get; }
    public string DeviceId { get; }
    public string DeviceToken { get; }
    public SimulatorScenario Scenario { get; }
    public int Count { get; }
    public TimeSpan Timeout { get; }
    public bool ShowHelp { get; }

    public static SimulatorOptions Parse(
        IReadOnlyList<string> arguments,
        Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var showHelp = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (argument is not ("--url" or "--device" or "--token" or "--scenario" or "--count" or "--timeout-seconds"))
                throw new SimulatorUsageException("An unsupported simulator argument was supplied.");
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new SimulatorUsageException($"A value is required for {argument}.");
            if (!values.TryAdd(argument, arguments[++index]))
                throw new SimulatorUsageException($"Argument {argument} can be specified only once.");
        }

        if (showHelp)
        {
            return new SimulatorOptions(
                new Uri("http://localhost:5244/ws/device"),
                string.Empty,
                string.Empty,
                SimulatorScenario.Full,
                1,
                TimeSpan.FromSeconds(30),
                true);
        }

        var urlText = Get(values, "--url") ?? readEnvironment("POST_DEVICE_URL")
            ?? "http://localhost:5244/ws/device";
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var hubUrl) ||
            hubUrl.Scheme is not ("http" or "https"))
            throw new SimulatorUsageException("The simulator URL must be an absolute HTTP or HTTPS URL.");
        var deviceId = Get(values, "--device") ?? readEnvironment("POST_DEVICE_ID") ?? "mock-device";
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new SimulatorUsageException("A non-empty device id is required.");
        var token = Get(values, "--token") ?? readEnvironment("POST_DEVICE_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new SimulatorUsageException("A device token is required through --token or POST_DEVICE_TOKEN.");

        var scenario = (Get(values, "--scenario") ?? "full").ToUpperInvariant() switch
        {
            "AUTH" => SimulatorScenario.Auth,
            "FULL" => SimulatorScenario.Full,
            "LOAD" => SimulatorScenario.Load,
            _ => throw new SimulatorUsageException("Scenario must be auth, full, or load.")
        };
        var countText = Get(values, "--count") ?? (scenario == SimulatorScenario.Load ? "100" : "1");
        if (!int.TryParse(countText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count) ||
            count is < 1 or > 1_000)
            throw new SimulatorUsageException("Count must be between 1 and 1000.");
        var timeoutText = Get(values, "--timeout-seconds") ?? "30";
        if (!int.TryParse(timeoutText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var timeoutSeconds) ||
            timeoutSeconds is < 1 or > 300)
            throw new SimulatorUsageException("Timeout must be between 1 and 300 seconds.");

        return new SimulatorOptions(
            hubUrl,
            deviceId.Trim(),
            token,
            scenario,
            count,
            TimeSpan.FromSeconds(timeoutSeconds),
            false);
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;
}

public sealed class SimulatorUsageException(string message) : Exception(message);

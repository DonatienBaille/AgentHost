namespace AgentHost.Api.Domain;

/// <summary>
/// Resource envelope applied to a run's container (spec 6.2 `spec.runtime` + 7.1 HostConfig).
/// Derived from the agent manifest at run-creation time and stored/serialized on the Run
/// so that a run remains reproducible even if the agent manifest changes later.
/// </summary>
public class RuntimeProfile
{
    public string Profile { get; set; } = "standard"; // nano, small, standard, large
    public long Cpu { get; set; } = 2;
    public string Memory { get; set; } = "2Gi";
    public string Disk { get; set; } = "10Gi";
    public int MaxDurationSeconds { get; set; } = 3600;

    /// <summary>Parsed memory limit in bytes (e.g. "16Gi" -> 17179869184).</summary>
    public long MemoryBytes => ParseByteSize(Memory);

    public static RuntimeProfile Default() => new();

    internal static long ParseByteSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 2L * 1024 * 1024 * 1024;

        value = value.Trim();
        var numberPart = new string(value.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var unitPart = value[numberPart.Length..].Trim().ToLowerInvariant();

        if (!double.TryParse(numberPart, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return 2L * 1024 * 1024 * 1024;

        double multiplier = unitPart switch
        {
            "ki" => 1024d,
            "mi" => 1024d * 1024,
            "gi" => 1024d * 1024 * 1024,
            "ti" => 1024d * 1024 * 1024 * 1024,
            "k" or "kb" => 1000d,
            "m" or "mb" => 1000d * 1000,
            "g" or "gb" => 1000d * 1000 * 1000,
            "t" or "tb" => 1000d * 1000 * 1000 * 1000,
            "" => 1d,
            _ => 1024d * 1024 * 1024, // default to Gi-like scaling for unknown units
        };

        return (long)(number * multiplier);
    }
}

namespace DaxAlgo.Strategy.Bundle;

public sealed record StrategyBundleLimitOptions
{
    public static StrategyBundleLimitOptions Default { get; } = new();

    public long MaxCompressedBundleBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxCompressedEntryBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxEntryCount { get; init; } = 128;
    public long MaxEntryExpandedBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxTotalExpandedBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxManifestBytes { get; init; } = 1024 * 1024;
    public long MaxSignatureEnvelopeBytes { get; init; } = 2L * 1024 * 1024;
    public double MaxCompressionRatio { get; init; } = 100d;
    public int MaxPathLength { get; init; } = 240;
    public int MaxPathDepth { get; init; } = 8;
    public int MaxCapabilities { get; init; } = 64;

    internal StrategyBundleLimitOptions Checked()
    {
        Positive(MaxCompressedBundleBytes, nameof(MaxCompressedBundleBytes));
        Positive(MaxCompressedEntryBytes, nameof(MaxCompressedEntryBytes));
        Positive(MaxEntryCount, nameof(MaxEntryCount));
        Positive(MaxEntryExpandedBytes, nameof(MaxEntryExpandedBytes));
        Positive(MaxTotalExpandedBytes, nameof(MaxTotalExpandedBytes));
        Positive(MaxManifestBytes, nameof(MaxManifestBytes));
        Positive(MaxSignatureEnvelopeBytes, nameof(MaxSignatureEnvelopeBytes));
        if (!double.IsFinite(MaxCompressionRatio) || MaxCompressionRatio < 1d)
            throw new ArgumentOutOfRangeException(nameof(MaxCompressionRatio));
        Positive(MaxPathLength, nameof(MaxPathLength));
        Positive(MaxPathDepth, nameof(MaxPathDepth));
        Positive(MaxCapabilities, nameof(MaxCapabilities));

        if (MaxCompressedBundleBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxCompressedBundleBytes), "The in-memory verifier limit cannot exceed Int32.MaxValue.");

        if (MaxCompressedEntryBytes > MaxCompressedBundleBytes)
            throw new ArgumentException("The compressed entry limit cannot exceed the bundle limit.");
        if (MaxEntryExpandedBytes > MaxTotalExpandedBytes)
            throw new ArgumentException("The expanded entry limit cannot exceed the total expanded limit.");
        if (MaxManifestBytes > MaxEntryExpandedBytes)
            throw new ArgumentException("The manifest limit cannot exceed the expanded entry limit.");
        if (MaxSignatureEnvelopeBytes > MaxEntryExpandedBytes)
            throw new ArgumentException("The signature limit cannot exceed the expanded entry limit.");

        var maximumEncodedManifestBytes = checked(4L * ((MaxManifestBytes + 2L) / 3L));
        if (MaxSignatureEnvelopeBytes < maximumEncodedManifestBytes + 4096L)
        {
            throw new ArgumentException(
                "The signature envelope limit must accommodate a base64-encoded maximum-size manifest.");
        }

        return this;
    }

    private static void Positive(long value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}

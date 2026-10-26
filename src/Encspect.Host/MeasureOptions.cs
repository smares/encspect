namespace Encspect.Host;

// What a front end asks the host to measure of the decoded audio (DecodeOptions.Measure; docs/design/spectral-analysis.md).
public sealed class MeasureOptions
{
    // Which measurements run; null (the default) means every one the library has when the code runs. (An `All` member of the flags
    // enum would be a constant compiled into the caller, and a caller built against an older library would keep asking for the kinds
    // that library had.)
    public MeasurementKinds? Include { get; init; }

    // The measurements' work budget in work units, counted apart from the decoders' (DecodeWork); null for the default (the larger
    // of 2^35 and the file's size times the largest allowance among the decoders). Tests and the fuzzer lower it. An option of the
    // call, not a setting.
    public long? MaxWork { get; init; }

    // For tests: runs before each block is measured, so a test can raise the exception a bug in a measurement would (measure.failed).
    internal Action<long>? BeforeBlock { get; init; }
}

[Flags]
public enum MeasurementKinds
{
    None = 0,

    // The average spectrum, the spectral cutoff, and the spectrogram and waveform grids.
    Spectrum = 1,
}

// The kinds the library has, as a property so that it is read when it runs (never a const or a field initializer in a caller).
public static class MeasurementSets
{
    public static MeasurementKinds All => MeasurementKinds.Spectrum;
}

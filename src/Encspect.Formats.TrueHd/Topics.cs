namespace Encspect.Formats.TrueHd;

// The IDs of the background articles the nodes point to (Node.Topic); the host serves them from src/Encspect.Host/Topics.
internal static class Topics
{
    public const string AccessUnits = "truehd-access-units";
    public const string Substreams = "truehd-substreams";
    public const string RestartHeader = "truehd-restart-header";
    public const string DecodingParameters = "truehd-decoding-parameters";
    public const string ResidualCoding = "truehd-residual-coding";
    public const string PredictionFilters = "truehd-prediction-filters";
    public const string Rematrixing = "truehd-rematrixing";
    public const string LosslessCheck = "truehd-lossless-check";
    public const string Crc = "crc";
    public const string Verification = "verification";
    public const string ChannelLayouts = "channel-layouts";
}

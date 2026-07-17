using System.Text.Json.Serialization;

namespace POE2Radar.Core.Tracks;

public sealed record TrackSample(
    [property: JsonPropertyName("t")] long T,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y);

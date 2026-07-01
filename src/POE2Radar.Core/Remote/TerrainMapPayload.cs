using System.Text.Json;

namespace POE2Radar.Core.Remote;

/// <summary>Serializes a walkable terrain grid into the JSON the /api/map endpoint returns: dimensions,
/// the area hash it belongs to, and the grid itself base64-encoded (one byte per cell, 0/1). Pure.</summary>
public static class TerrainMapPayload
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ToJson(byte[] walkable, int width, int height, uint areaHash)
        => JsonSerializer.Serialize(new
        {
            ready = true,
            areaHash,
            width,
            height,
            walkable = System.Convert.ToBase64String(walkable),
        }, Json);
}

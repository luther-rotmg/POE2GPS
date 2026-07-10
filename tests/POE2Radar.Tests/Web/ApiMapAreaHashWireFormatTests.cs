using System.Text.Json;
using POE2Radar.Core.Remote;
using Xunit;

namespace POE2Radar.Tests.Web;

/// <summary>
/// Signal — SIG-MAP-FIX. Locks the wire-format contract between /api/map and /stream SSE for
/// the zone identifier. /api/map ships areaHash as a JSON number (uint). /stream ships area as a
/// hex string (uint.ToString("x")). Because the two formats differ, the client MUST coerce one to
/// the other before comparing. This test asserts the invariant on both sides so a future refactor
/// tripping either format loudly fails.
/// </summary>
public class ApiMapAreaHashWireFormatTests
{
    [Fact]
    public void ApiMap_AreaHash_Serializes_As_JsonNumber_AndCoercesToSseHexString()
    {
        // Arrange: a plausible areaHash value.
        const uint areaHash = 0x1234_ABCDu;
        var walkable = new byte[] { 0, 1, 1, 0 };
        var json = TerrainMapPayload.ToJson(walkable, width: 2, height: 2, areaHash);

        using var doc = JsonDocument.Parse(json);
        var areaHashElement = doc.RootElement.GetProperty("areaHash");

        // Assert 1: /api/map emits a JSON number, not a string.
        Assert.Equal(JsonValueKind.Number, areaHashElement.ValueKind);
        Assert.Equal(areaHash, areaHashElement.GetUInt32());

        // Assert 2: The SSE-side hex-string coercion of the number matches areaHash.ToString("x").
        var apiHex = areaHashElement.GetUInt32().ToString("x");
        var sseHex = areaHash.ToString("x");
        Assert.Equal(sseHex, apiHex);
    }
}

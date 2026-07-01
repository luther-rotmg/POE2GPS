using POE2Radar.Core.Remote;

public class TerrainMapPayloadTests
{
    [Fact] public void ToJson_includes_ready_dims_hash_and_base64_walkable()
    {
        var walk = new byte[] { 0, 1, 1, 0 };                 // 2x2 grid
        var json = TerrainMapPayload.ToJson(walk, 2, 2, 0xABCDu);
        Assert.Contains("\"ready\":true", json);
        Assert.Contains("\"width\":2", json);
        Assert.Contains("\"height\":2", json);
        Assert.Contains("\"areaHash\":43981", json);          // 0xABCD == 43981
        Assert.Contains("\"walkable\":\"" + System.Convert.ToBase64String(walk) + "\"", json);
    }
}

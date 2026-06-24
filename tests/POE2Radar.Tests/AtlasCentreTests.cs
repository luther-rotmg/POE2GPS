using POE2Radar.Core;

public class AtlasCentreTests
{
    [Fact]
    public void AtlasCentre_AddsHalfSize()
    {
        Assert.Equal(30f, AtlasGeometry.AtlasCentre(10f, 40f));        // 10 + 40/2 = 30
        Assert.Equal(30f, AtlasGeometry.AtlasCentre(10f, 0f));         // fallback: 10 + 40/2 = 30
        Assert.Equal(30f, AtlasGeometry.AtlasCentre(10f, 1f));         // size <= 1 triggers fallback
        Assert.Equal(30f, AtlasGeometry.AtlasCentre(10f, float.NaN)); // NaN > 1 is false -> fallback
        Assert.Equal(25f, AtlasGeometry.AtlasCentre(5f,  40f));        // 5 + 40/2 = 25
        Assert.Equal(25f, AtlasGeometry.AtlasCentre(5f,  0f));         // fallback path
    }
}

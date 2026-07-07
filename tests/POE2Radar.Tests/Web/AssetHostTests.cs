using System.IO;
using System.Text;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class AssetHostTests
{
    [Fact]
    public void Loads_embedded_map_html()
    {
        var host = new AssetHost();
        var bytes = host.LoadForTest("map.html");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<canvas id=\"c\"></canvas>", text);
    }

    [Fact]
    public void ServeObs_injects_body_class_obs()
    {
        var host = new AssetHost();
        var raw = Encoding.UTF8.GetString(host.LoadForTest("map.html"));
        var withObs = host.ObsHtmlForTest();
        Assert.Contains("<body class=\"obs\">", withObs);
        Assert.DoesNotContain("<body>\n", raw); // sanity: template body is what we substitute against
    }

    [Fact]
    public void Unknown_asset_returns_null()
    {
        var host = new AssetHost();
        Assert.Null(host.LoadForTest("does-not-exist.txt"));
    }
}

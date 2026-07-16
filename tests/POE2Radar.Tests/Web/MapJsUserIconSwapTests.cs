using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class MapJsUserIconSwapTests
{
    // Helper to read the embedded map.js resource
    private static string ReadMapJs()
    {
        var asm = typeof(AssetHost).Assembly;
        using var s = asm.GetManifestResourceStream("POE2Radar.Overlay.Web.Assets.map.js")!;
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    [Fact]
    public void MapJs_DeclaresUserIconsMap()
    {
        var content = ReadMapJs();
        Assert.Contains("userIcons: new Map()", content);
    }

    [Fact]
    public void MapJs_HasLoadUserIconsFetchingApiUserIcons()
    {
        var content = ReadMapJs();
        Assert.Contains("loadUserIcons", content);
        Assert.Contains("fetch('/api/user-icons')", content);
    }

    [Fact]
    public void MapJs_PreloadsImagesViaOnload()
    {
        var content = ReadMapJs();
        Assert.Contains("new Image()", content);
        Assert.Contains("img.onload", content);
        Assert.Matches(@"img\.src\s*=\s*\w+\.dataUri", content);
    }

    [Fact]
    public void MapJs_HasResolveEntityIconWithPrecedence()
    {
        var content = ReadMapJs();
        Assert.Contains("function resolveEntityIcon", content);
        Assert.Contains("metadataGlob", content);
        Assert.Contains("e.cat + '.' + e.rar", content);
        Assert.Contains("e.cat", content);
    }

    [Fact]
    public void MapJs_DrawEntitiesUsesDrawImageWithArcFallback()
    {
        var content = ReadMapJs();
        Assert.Contains("resolveEntityIcon(e)", content);
        Assert.Contains("c.drawImage(icon", content);
        Assert.Contains("icon.width > 0", content);
        Assert.Contains("c.arc(drawX, drawY, r, 0, Math.PI * 2)", content);
    }

    [Fact]
    public void MapJs_PoiCrosshairPreservedAfterIconBranch()
    {
        var content = ReadMapJs();
        Assert.Contains("c.moveTo(drawX - 3, drawY); c.lineTo(drawX + 3, drawY);", content);
        Assert.Contains("c.moveTo(drawX, drawY - 3); c.lineTo(drawX, drawY + 3);", content);
    }

    [Fact]
    public void MapJs_OffScreenArrowBranchPreserved()
    {
        var content = ReadMapJs();
        Assert.Contains("drawOffScreenArrow(c, drawX, drawY, cx, cy, cw, ch, col.fill)", content);
    }

    [Fact]
    public void MapJs_WindowLoadInvokesLoadUserIcons()
    {
        var content = ReadMapJs();
        Assert.Contains("loadUserIcons().catch(() => {})", content);
    }

    [Fact]
    public void MapJs_NoNewImageInsideDrawEntities()
    {
        var content = ReadMapJs();
        // Find the drawEntities function body
        var drawEntitiesStart = content.IndexOf("function drawEntities");
        Assert.True(drawEntitiesStart >= 0, "drawEntities function not found");
        
        // Find the closing brace of drawEntities (find matching brace)
        int braceCount = 0;
        int i = drawEntitiesStart;
        while (i < content.Length)
        {
            if (content[i] == '{') braceCount++;
            if (content[i] == '}') braceCount--;
            if (braceCount == 0) break;
            i++;
        }
        
        var drawEntitiesBody = content.Substring(drawEntitiesStart, i - drawEntitiesStart + 1);
        Assert.DoesNotContain("new Image(", drawEntitiesBody);
    }
}

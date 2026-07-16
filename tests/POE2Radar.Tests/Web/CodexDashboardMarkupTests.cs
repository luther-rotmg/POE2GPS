using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class CodexDashboardMarkupTests
{
    [Fact]
    public void Dashboard_html_contains_codex_tab_button()
    {
        Assert.Contains("data-tab=\"codex\"", DashboardHtml.Page);
    }

    [Fact]
    public void Dashboard_html_contains_codex_panel()
    {
        Assert.Contains("data-panel=\"codex\"", DashboardHtml.Page);
    }

    [Fact]
    public void Dashboard_html_contains_all_four_filter_chips()
    {
        var page = DashboardHtml.Page;
        Assert.Contains("data-codex-filter=\"level\"", page);
        Assert.Contains("data-codex-filter=\"boss\"", page);
        Assert.Contains("data-codex-filter=\"death\"", page);
        Assert.Contains("data-codex-filter=\"drop\"", page);
    }

    [Fact]
    public void Dashboard_html_contains_codex_character_input()
    {
        Assert.Contains("id=\"codex-character-input\"", DashboardHtml.Page);
    }

    [Fact]
    public void Dashboard_html_contains_codex_jump_to_date()
    {
        Assert.Contains("id=\"codex-jump-date\"", DashboardHtml.Page);
    }

    [Fact]
    public void Dashboard_html_fetches_api_codex()
    {
        Assert.Contains("/api/codex?character=", DashboardHtml.Page);
    }

    [Fact]
    public void Dashboard_html_codex_has_no_emoji()
    {
        var page = DashboardHtml.Page;
        var start = page.IndexOf("// CODEX-JS-START");
        var end = page.IndexOf("// CODEX-JS-END");
        Assert.True(start >= 0, "CODEX-JS-START sentinel not found");
        Assert.True(end > start, "CODEX-JS-END sentinel not found after START");
        var codexJs = page.Substring(start, end - start);

        // No emoji ranges: U+1F300..U+1FAFF or U+2600..U+27BF
        for (var i = 0; i < codexJs.Length; i++)
        {
            var c = (int)codexJs[i];
            if (char.IsSurrogate(codexJs, i))
            {
                // Surrogate pair: decode the full codepoint
                var cp = char.ConvertToUtf32(codexJs, i);
                Assert.False(cp >= 0x1F300 && cp <= 0x1FAFF, $"Emoji U+{cp:X4} found in codex JS at index {i}");
                Assert.False(cp >= 0x2600 && cp <= 0x27BF, $"Emoji U+{cp:X4} found in codex JS at index {i}");
                i++; // skip the low surrogate
            }
            else
            {
                Assert.False(c >= 0x2600 && c <= 0x27BF, $"Emoji U+{c:X4} found in codex JS at index {i}");
            }
        }
    }
}
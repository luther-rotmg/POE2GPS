using POE2Radar.Core.Remote;

public class ApiPrefixTests
{
    [Fact] public void Build_localhost_when_lan_disabled()
        => Assert.Equal("http://localhost:7777/", ApiPrefix.Build(false, 7777));

    [Fact] public void Build_wildcard_when_lan_enabled()
        => Assert.Equal("http://+:7777/", ApiPrefix.Build(true, 7777));

    [Fact] public void Build_uses_given_port()
        => Assert.Equal("http://+:8080/", ApiPrefix.Build(true, 8080));
}

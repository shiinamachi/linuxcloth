namespace LinuxCloth.Wsb.Tests;

public sealed class WsbParserTests
{
    [Fact]
    public void SupportedSettingsRoundTripWithoutLosingEscapedText()
    {
        const string command = "cmd.exe /c echo \"<tag>&value\"";
        var original = new WsbConfiguration(
            networking: WsbFeatureState.Disable,
            virtualGpu: WsbFeatureState.Enable,
            memoryInMiB: 8192,
            clipboardRedirection: WsbFeatureState.Disable,
            logonCommand: command,
            mappedFolders: [new WsbMappedFolder("/home/user/A&B", "C:\\A<B", readOnly: true)]);

        var xml = WsbSerializer.Serialize(original);
        var parsed = WsbParser.Parse(xml, WsbParseMode.AdvancedInspection);

        Assert.Contains("&lt;tag&gt;&amp;value", xml, StringComparison.Ordinal);
        Assert.Contains("A&amp;B", xml, StringComparison.Ordinal);
        Assert.Equal(original.Networking, parsed.Networking);
        Assert.Equal(original.VirtualGpu, parsed.VirtualGpu);
        Assert.Equal(original.MemoryInMiB, parsed.MemoryInMiB);
        Assert.Equal(original.ClipboardRedirection, parsed.ClipboardRedirection);
        Assert.Equal(command, parsed.LogonCommand);
        var folder = Assert.Single(parsed.MappedFolders);
        Assert.Equal("/home/user/A&B", folder.HostFolder);
        Assert.Equal("C:\\A<B", folder.SandboxFolder);
        Assert.True(folder.ReadOnly);
    }

    [Fact]
    public void DtdAndExternalEntitiesAreProhibited()
    {
        const string xml = """
            <!DOCTYPE Configuration [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <Configuration>
              <LogonCommand><Command>&secret;</Command></LogonCommand>
            </Configuration>
            """;

        Assert.Throws<WsbValidationException>(() =>
            WsbParser.Parse(xml, WsbParseMode.AdvancedInspection));
    }

    [Fact]
    public void NormalModeRejectsMappedFoldersEvenWhenReadOnly()
    {
        const string xml = """
            <Configuration>
              <MappedFolders>
                <MappedFolder>
                  <HostFolder>/home/user</HostFolder>
                  <ReadOnly>true</ReadOnly>
                </MappedFolder>
              </MappedFolders>
            </Configuration>
            """;

        var exception = Assert.Throws<WsbValidationException>(() => WsbParser.Parse(xml));

        Assert.Contains("prohibited", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalModeRejectsArbitraryLogonCommands()
    {
        const string xml = """
            <Configuration>
              <LogonCommand>
                <Command>powershell.exe -Command Remove-Item -Recurse C:\</Command>
              </LogonCommand>
            </Configuration>
            """;

        Assert.Throws<WsbValidationException>(() => WsbParser.Parse(xml));
    }

    [Theory]
    [InlineData("<Configuration><Networking>Enable</Networking><Networking>Disable</Networking></Configuration>")]
    [InlineData("<Configuration><AudioInput>Enable</AudioInput></Configuration>")]
    [InlineData("<Configuration xmlns='urn:confused'><Networking>Enable</Networking></Configuration>")]
    [InlineData("<Configuration><MemoryInMB>999999999</MemoryInMB></Configuration>")]
    public void AmbiguousOrUnsupportedDocumentsAreRejected(string xml)
    {
        Assert.Throws<WsbValidationException>(() => WsbParser.Parse(xml));
    }
}

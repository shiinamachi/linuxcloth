using System.Text;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task DecodesStandardOutputWithTheRequestedEncoding()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessSpec(
                "/usr/bin/printf",
                [@"\377\376<\000W\000I\000M\000>\000"],
                standardOutputEncoding: Encoding.Unicode));

        Assert.True(result.IsSuccess);
        Assert.Equal("<WIM>", result.StandardOutput);
    }
}

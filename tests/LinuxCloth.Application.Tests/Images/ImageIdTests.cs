using LinuxCloth.Application.Images;

namespace LinuxCloth.Application.Tests.Images;

public sealed class ImageIdTests
{
    [Theory]
    [InlineData("windows-11")]
    [InlineData("w11")]
    [InlineData("1")]
    [InlineData("a-b-c")]
    public void AcceptsCanonicalLowercaseIdentifiers(string value)
    {
        var imageId = ImageId.Parse(value);

        Assert.Equal(value, imageId.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Windows-11")]
    [InlineData("windows_11")]
    [InlineData("windows.11")]
    [InlineData("-windows")]
    [InlineData("windows-")]
    [InlineData("../windows")]
    [InlineData("windows/11")]
    public void RejectsNonCanonicalOrPathLikeIdentifiers(string value)
    {
        Assert.False(ImageId.TryParse(value, out _));
        Assert.Throws<FormatException>(() => ImageId.Parse(value));
    }

    [Fact]
    public void RejectsIdentifiersLongerThanSixtyThreeCharacters()
    {
        Assert.False(ImageId.TryParse(new string('a', ImageId.MaximumLength + 1), out _));
    }

    [Fact]
    public void DefaultIdentifierCannotBeUsed()
    {
        using var fixture = new ImageRegistryFixture();

        Assert.Throws<ArgumentException>(() => fixture.Registry.CreateStaging(default));
    }
}

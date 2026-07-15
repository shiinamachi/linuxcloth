namespace LinuxCloth.Core;

/// <summary>
/// Immutable supply-chain inputs for the Spork release accepted by linuxcloth.
/// Updating this contract requires reviewing and replacing every digest together.
/// </summary>
public static class PinnedSporkRelease
{
    public const string Version = "1.20.5";

    public const string BootstrapUrl =
        "https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/" +
        "SporkBootstrap_1.20.5.0_Release_x64.exe";

    public const long BootstrapSizeBytes = 5_185_888;

    public const string BootstrapSha256 =
        "AD953BBBECE1D2E72898164DA2E5D152A15D2E1EBBAF330A089AA1E8775CC498";

    public const string BootstrapSignerCertificateSha256 =
        "892C4996A8E6AD504275B228C04269B708D98455BBBB86202BEF073E9A8D320A";

    public const string SporkZipUrlTemplate =
        "https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/" +
        "Spork_1.20.5.0_Release_{arch}_Portable.zip";

    public const string SporkX64Sha256 =
        "F8E8FE7DFDCCB7CFFD971CF153C5C83C848A6B1ECC39F13BDE5895702CF156AF";

    public const string SporkArm64Sha256 =
        "D61B2BF93D11711E592C4ADF7528C0CE4D690A4F561783E26884577F98C60351";

    public const string SporkSha256Map =
        "x64=" + SporkX64Sha256 + ";arm64=" + SporkArm64Sha256;
}

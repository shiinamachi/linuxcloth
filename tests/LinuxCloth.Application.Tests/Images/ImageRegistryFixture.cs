using LinuxCloth.Application.Images;
using LinuxCloth.Application.Storage;

namespace LinuxCloth.Application.Tests.Images;

internal sealed class ImageRegistryFixture : IDisposable
{
    public ImageRegistryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"lc-images-{Guid.NewGuid():N}");
        Paths = new LinuxClothPaths(
            Path.Combine(Root, "config"),
            Path.Combine(Root, "data"),
            Path.Combine(Root, "cache"),
            Path.Combine(Root, "runtime"));
        Paths.CreateBaseDirectories();
        Registry = new ManagedImageRegistry(Paths);

        var firmwareDirectory = Path.Combine(Root, "firmware");
        Directory.CreateDirectory(firmwareDirectory);
        OvmfCodePath = Path.Combine(firmwareDirectory, "OVMF_CODE.secboot.fd");
        File.WriteAllBytes(OvmfCodePath, "ovmf-code"u8.ToArray());
    }

    public string Root { get; }

    public LinuxClothPaths Paths { get; }

    public ManagedImageRegistry Registry { get; }

    public string OvmfCodePath { get; }

    public ImageRegistrationStaging CreateReadyStaging(
        string imageId,
        IReadOnlyList<(string Path, string Contents)>? tpmFiles = null)
    {
        var staging = Registry.CreateStaging(ImageId.Parse(imageId));
        using (var baseImage = new FileStream(
                   staging.BaseImagePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            baseImage.Write("QFI\xfb"u8);
            baseImage.SetLength(8 * 1024 * 1024);
        }

        File.WriteAllBytes(staging.OvmfVariablesTemplatePath, "ovmf-vars"u8.ToArray());
        foreach (var (relativePath, contents) in tpmFiles ??
                 [("tpm2-00.permall", "sealed-state")])
        {
            var path = Path.Combine(staging.SwtpmStateTemplateDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        return staging;
    }

    public async Task<ManagedWindowsImage> PromoteReadyAsync(string imageId)
    {
        var staging = CreateReadyStaging(imageId);
        return await Registry.PromoteAsync(staging, Guid.NewGuid(), OvmfCodePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            MakeWritable(Root);
            Directory.Delete(Root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    public static void MakeWritable(string root)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

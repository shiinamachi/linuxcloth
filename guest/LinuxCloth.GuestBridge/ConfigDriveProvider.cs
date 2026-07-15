namespace LinuxCloth.GuestBridge;

internal interface IConfigDriveProvider
{
    IReadOnlyList<string> GetReadyDriveRoots();
}

internal sealed class SystemConfigDriveProvider : IConfigDriveProvider
{
    public IReadOnlyList<string> GetReadyDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (IOException)
            {
                // A drive can disappear while Windows enumerates it.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible drive cannot contain a usable config.
            }
        }

        return roots;
    }
}

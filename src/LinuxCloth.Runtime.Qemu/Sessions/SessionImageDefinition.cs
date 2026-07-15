namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed record SessionImageDefinition(
    string ImageId,
    Guid MachineId,
    string BaseImagePath,
    string OvmfCodePath,
    string OvmfVariablesTemplatePath,
    string SwtpmStateTemplateDirectory);


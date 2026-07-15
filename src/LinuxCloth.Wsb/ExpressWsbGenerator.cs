using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

public static class ExpressWsbGenerator
{
    public static WsbConfiguration CreateConfiguration(
        IReadOnlyList<ServiceId> serviceIds,
        bool networkEnabled = true,
        bool clipboardEnabled = false,
        int? memoryInMiB = null)
    {
        var validated = ServiceIdSet.ValidateAndCopy(serviceIds, allowEmpty: false, nameof(serviceIds));

        return new WsbConfiguration(
            networking: networkEnabled ? WsbFeatureState.Enable : WsbFeatureState.Disable,
            virtualGpu: WsbFeatureState.Disable,
            memoryInMiB,
            clipboardRedirection: clipboardEnabled ? WsbFeatureState.Enable : WsbFeatureState.Disable,
            logonCommand: ExpressWsbCommand.Create(validated),
            mappedFolders: [],
            expressServiceIds: validated);
    }

    public static string Generate(
        IReadOnlyList<ServiceId> serviceIds,
        bool networkEnabled = true,
        bool clipboardEnabled = false,
        int? memoryInMiB = null) =>
        WsbSerializer.Serialize(CreateConfiguration(serviceIds, networkEnabled, clipboardEnabled, memoryInMiB));
}

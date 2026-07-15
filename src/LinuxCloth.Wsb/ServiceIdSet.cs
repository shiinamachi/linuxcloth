using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

internal static class ServiceIdSet
{
    public static ServiceId[] ValidateAndCopy(
        IReadOnlyList<ServiceId> serviceIds,
        bool allowEmpty,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(serviceIds, parameterName);

        if (!allowEmpty && serviceIds.Count == 0)
        {
            throw new ArgumentException("At least one service identifier is required.", parameterName);
        }

        if (serviceIds.Count > 32)
        {
            throw new ArgumentOutOfRangeException(parameterName, "At most 32 service identifiers are allowed.");
        }

        var result = new ServiceId[serviceIds.Count];
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < serviceIds.Count; index++)
        {
            var value = serviceIds[index].Value;
            if (!ServiceId.TryCreate(value, out var validated))
            {
                throw new ArgumentException("Every service identifier must be a validated Core ServiceId.", parameterName);
            }

            if (!uniqueValues.Add(validated.Value))
            {
                throw new ArgumentException("Service identifiers must be unique.", parameterName);
            }

            result[index] = validated;
        }

        return result;
    }
}

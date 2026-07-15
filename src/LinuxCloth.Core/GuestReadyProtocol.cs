using System.Text;

namespace LinuxCloth.Core;

public static class GuestReadyProtocol
{
    public const string Prefix = "linuxcloth-ready-v1 ";
    public const int MaximumMessageBytes = 64;

    public static byte[] CreateMessage(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("The guest-ready session identifier cannot be empty.", nameof(sessionId));
        }

        return Encoding.ASCII.GetBytes($"{Prefix}{sessionId:N}\n");
    }

    public static bool TryParse(ReadOnlySpan<byte> message, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (message.Length is 0 or > MaximumMessageBytes || message[^1] != (byte)'\n')
        {
            return false;
        }

        var prefix = Prefix.AsSpan();
        var payload = message[..^1];
        if (payload.Length != prefix.Length + 32)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (payload[index] != (byte)prefix[index])
            {
                return false;
            }
        }

        Span<char> identifier = stackalloc char[32];
        for (var index = 0; index < identifier.Length; index++)
        {
            var value = payload[prefix.Length + index];
            if (value is not (>= (byte)'0' and <= (byte)'9') and
                not (>= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }

            identifier[index] = (char)value;
        }

        return Guid.TryParseExact(identifier, "N", out sessionId) && sessionId != Guid.Empty;
    }
}

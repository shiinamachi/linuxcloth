using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LinuxCloth.GuestBridge;

internal enum ExecutableSignatureVerificationResult
{
    Trusted,
    InvalidSignature,
    SignerMismatch,
}

internal interface IExecutableSignatureVerifier
{
    ExecutableSignatureVerificationResult Verify(
        string executablePath,
        string expectedSignerCertificateSha256);
}

internal sealed partial class WindowsAuthenticodeVerifier : IExecutableSignatureVerifier
{
    private static readonly Guid GenericVerifyV2Action =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public ExecutableSignatureVerificationResult Verify(
        string executablePath,
        string expectedSignerCertificateSha256)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        var expectedFingerprint = ParseSha256(expectedSignerCertificateSha256);
        var actualFingerprint = VerifyTrustWithoutUi(fullPath);
        if (actualFingerprint is null)
        {
            return ExecutableSignatureVerificationResult.InvalidSignature;
        }

        return CryptographicOperations.FixedTimeEquals(actualFingerprint, expectedFingerprint)
            ? ExecutableSignatureVerificationResult.Trusted
            : ExecutableSignatureVerificationResult.SignerMismatch;
    }

    private static byte[]? VerifyTrustWithoutUi(string executablePath)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(executablePath);
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        var verifyAttempted = false;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustDataUiChoice.None,
                RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
                UnionChoice = WinTrustDataUnionChoice.File,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustDataStateAction.Verify,
                ProviderFlags = WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
                                WinTrustProviderFlags.Safer,
            };
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);

            var action = GenericVerifyV2Action;
            verifyAttempted = true;
            var trustResult = WinTrustNative.WinVerifyTrust(
                new IntPtr(-1),
                ref action,
                trustDataPointer);
            if (trustResult != 0)
            {
                return null;
            }

            trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
            return ReadSignerCertificateSha256(trustData.StateData);
        }
        finally
        {
            if (verifyAttempted && trustDataPointer != IntPtr.Zero)
            {
                var trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                trustData.StateAction = WinTrustDataStateAction.Close;
                Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
                var action = GenericVerifyV2Action;
                _ = WinTrustNative.WinVerifyTrust(
                    new IntPtr(-1),
                    ref action,
                    trustDataPointer);
            }

            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static byte[]? ReadSignerCertificateSha256(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
        {
            return null;
        }

        var providerData = WinTrustNative.WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
        {
            return null;
        }

        var providerSigner = WinTrustNative.WTHelperGetProvSignerFromChain(
            providerData,
            signerIndex: 0,
            counterSigner: false,
            counterSignerIndex: 0);
        if (providerSigner == IntPtr.Zero)
        {
            return null;
        }

        var signer = Marshal.PtrToStructure<CryptProviderSigner>(providerSigner);
        if (signer.CertificateChainCount == 0 || signer.CertificateChain == IntPtr.Zero)
        {
            return null;
        }

        var providerCertificate = Marshal.PtrToStructure<CryptProviderCertificate>(
            signer.CertificateChain);
        if (providerCertificate.CertificateContext == IntPtr.Zero)
        {
            return null;
        }

        var certificateContext = Marshal.PtrToStructure<CertificateContext>(
            providerCertificate.CertificateContext);
        const int maximumCertificateBytes = 1024 * 1024;
        if (certificateContext.EncodedCertificate == IntPtr.Zero ||
            certificateContext.EncodedCertificateBytes is <= 0 or > maximumCertificateBytes)
        {
            return null;
        }

        var encodedCertificate = new byte[certificateContext.EncodedCertificateBytes];
        Marshal.Copy(
            certificateContext.EncodedCertificate,
            encodedCertificate,
            startIndex: 0,
            encodedCertificate.Length);
        return SHA256.HashData(encodedCertificate);
    }

    private static byte[] ParseSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            var fingerprint = Convert.FromHexString(value);
            return fingerprint.Length == SHA256.HashSizeInBytes
                ? fingerprint
                : throw new ArgumentException(
                    "A signer fingerprint must contain 32 bytes.",
                    nameof(value));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A signer fingerprint must be hexadecimal.",
                nameof(value),
                exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;
        internal IntPtr FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructureSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal WinTrustDataUiChoice UiChoice;
        internal WinTrustDataRevocationChecks RevocationChecks;
        internal WinTrustDataUnionChoice UnionChoice;
        internal IntPtr FileInfo;
        internal WinTrustDataStateAction StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal WinTrustProviderFlags ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructureSize;
        internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        internal uint CertificateChainCount;
        internal IntPtr CertificateChain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        internal uint StructureSize;
        internal IntPtr CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint EncodingType;
        internal IntPtr EncodedCertificate;
        internal int EncodedCertificateBytes;
        internal IntPtr CertificateInfo;
        internal IntPtr CertificateStore;
    }

    private enum WinTrustDataUiChoice : uint
    {
        None = 2,
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        WholeChain = 1,
    }

    private enum WinTrustDataUnionChoice : uint
    {
        File = 1,
    }

    private enum WinTrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    private enum WinTrustProviderFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080,
        Safer = 0x00000100,
    }

    private static partial class WinTrustNative
    {
        [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust", SetLastError = true)]
        internal static partial int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid action,
            IntPtr trustData);

        [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
        internal static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

        [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
        internal static partial IntPtr WTHelperGetProvSignerFromChain(
            IntPtr providerData,
            uint signerIndex,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
            uint counterSignerIndex);
    }
}

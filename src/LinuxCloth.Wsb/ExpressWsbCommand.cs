using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

internal static class ExpressWsbCommand
{
    private const string CommandPrefix =
        "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"" +
        "$ErrorActionPreference = 'Stop'; " +
        "$Host.UI.RawUI.WindowTitle = 'linuxcloth - TableCloth Setup'; " +
        "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
        "$bootstrapUrl = '" + PinnedSporkRelease.BootstrapUrl + "'; " +
        "$bootstrapPath = Join-Path $env:TEMP ('linuxcloth-' + [Guid]::NewGuid().ToString('N') + '.exe'); ";

    private const string SiteIdsPrefix = "$siteIds = '";
    private const string SiteIdsSuffix = "'; ";

    private static readonly string CommandSuffix =
        "try { " +
        "Invoke-WebRequest -UseBasicParsing -Uri $bootstrapUrl -OutFile $bootstrapPath; " +
        "if ((Get-Item -LiteralPath $bootstrapPath).Length -ne " +
        PinnedSporkRelease.BootstrapSizeBytes + ") { throw 'Bootstrap size mismatch.' }; " +
        "$bootstrapHash = (Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256).Hash; " +
        "if ($bootstrapHash -ne '" + PinnedSporkRelease.BootstrapSha256 +
        "') { throw 'Bootstrap SHA-256 mismatch.' }; " +
        "$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapPath; " +
        "if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) " +
        "{ throw 'Bootstrap signature is not trusted.' }; " +
        "$sha = [Security.Cryptography.SHA256]::Create(); " +
        "try { $signerHash = ([BitConverter]::ToString($sha.ComputeHash(" +
        "$signature.SignerCertificate.RawData))).Replace('-', '') } finally { $sha.Dispose() }; " +
        "if ($signerHash -ne '" + PinnedSporkRelease.BootstrapSignerCertificateSha256 +
        "') { throw 'Bootstrap signer mismatch.' }; " +
        "& $bootstrapPath '--zip-url-template' '" + PinnedSporkRelease.SporkZipUrlTemplate +
        "' '--sha256-map' '" + PinnedSporkRelease.SporkSha256Map +
        "' '--site-ids' $siteIds; " +
        "if ($LASTEXITCODE -ne 0) { throw ('SporkBootstrap failed with exit code ' + $LASTEXITCODE) } " +
        "} catch { Write-Host ('Failed: ' + $_.Exception.Message) -ForegroundColor Red; " +
        "$null = Read-Host 'Press Enter to close'; exit 1 " +
        "} finally { Remove-Item -LiteralPath $bootstrapPath -Force -ErrorAction SilentlyContinue }\"";

    public static string Create(IReadOnlyList<ServiceId> serviceIds)
    {
        var validated = ServiceIdSet.ValidateAndCopy(serviceIds, allowEmpty: false, nameof(serviceIds));
        return CommandPrefix + SiteIdsPrefix + string.Join(' ', validated.Select(id => id.Value)) + SiteIdsSuffix + CommandSuffix;
    }

    public static bool TryParse(string command, out ServiceId[] serviceIds)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.Equals(command, CommandPrefix + CommandSuffix, StringComparison.Ordinal))
        {
            serviceIds = [];
            return true;
        }

        var prefix = CommandPrefix + SiteIdsPrefix;
        var suffix = SiteIdsSuffix + CommandSuffix;
        if (!command.StartsWith(prefix, StringComparison.Ordinal) ||
            !command.EndsWith(suffix, StringComparison.Ordinal))
        {
            serviceIds = [];
            return false;
        }

        var siteIdsText = command[prefix.Length..^suffix.Length];
        var tokens = siteIdsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            serviceIds = [];
            return false;
        }

        var parsed = new ServiceId[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!ServiceId.TryCreate(tokens[index], out parsed[index]))
            {
                serviceIds = [];
                return false;
            }
        }

        try
        {
            parsed = ServiceIdSet.ValidateAndCopy(parsed, allowEmpty: false, nameof(command));
        }
        catch (ArgumentException)
        {
            serviceIds = [];
            return false;
        }

        if (!string.Equals(command, Create(parsed), StringComparison.Ordinal))
        {
            serviceIds = [];
            return false;
        }

        serviceIds = parsed;
        return true;
    }
}

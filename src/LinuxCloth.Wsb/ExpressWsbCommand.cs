using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

internal static class ExpressWsbCommand
{
    private const string CommandPrefix =
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process powershell.exe -WindowStyle Normal -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command','$Host.UI.RawUI.WindowTitle = ''TableCloth Setup''; Write-Host '' Getting TableCloth ready...'' -ForegroundColor Cyan; if (-not (Resolve-DnsName -Name github.com -QuickTimeout -ErrorAction SilentlyContinue)) { Get-NetAdapter | Where-Object Status -eq ''Up'' | Set-DnsClientServerAddress -ServerAddresses 8.8.8.8,1.1.1.1 }; [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072; ";

    private const string SiteIdsPrefix = "$env:TABLECLOTH_SITE_IDS = ''";
    private const string SiteIdsSuffix = "''; ";

    private const string CommandSuffix =
        "try { iex ((New-Object Net.WebClient).DownloadString(''https://github.com/yourtablecloth/TableCloth/releases/latest/download/tablecloth-prepare.ps1'')) } catch { Write-Host ('' Failed: '' + $_.Exception.Message) -ForegroundColor Red; $null = Read-Host '' Press Enter to close'' }'\"";

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

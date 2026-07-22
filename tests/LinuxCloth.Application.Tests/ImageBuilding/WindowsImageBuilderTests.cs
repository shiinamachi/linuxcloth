using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Qmp;

namespace LinuxCloth.Application.Tests.ImageBuilding;

public sealed class WindowsImageBuilderTests
{
    [Fact]
    public async Task BeginsWithPreparedSparseDiskAndCopiedFirmwareVariables()
    {
        using var fixture = new ImageBuildFixture();

        var workspace = await fixture.BeginAsync();

        Assert.Equal(WindowsImageBuildPhase.Prepared, workspace.State.Phase);
        Assert.True(File.Exists(workspace.Staging.BaseImagePath));
        Assert.Equal(fixture.Toolchain.SevenZip, workspace.State.Toolchain.SevenZip);
        Assert.Equal(
            File.ReadAllBytes(fixture.OvmfVariablesPath),
            File.ReadAllBytes(workspace.Staging.OvmfVariablesTemplatePath));
        Assert.True(File.Exists(WindowsImageBuildStateStore.GetManifestPath(workspace.Staging)));
        Assert.Equal(1, fixture.MediaValidator.CallCount);
    }

    [Fact]
    public async Task FailedPreparationPreservesStagingAndCanResumeInPlace()
    {
        using var fixture = new ImageBuildFixture();
        fixture.Runner.FailCreateCount = 1;

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.BeginAsync(fixture.CreateRequest()));

        var staging = Assert.IsType<ImageRegistrationStaging>(failure.Staging);
        Assert.True(Directory.Exists(staging.DirectoryPath));
        Assert.True(File.Exists(WindowsImageBuildStateStore.GetManifestPath(staging)));

        var resumed = await fixture.Builder.ResumeAsync(staging.ImageId, staging.DirectoryPath);

        Assert.Equal(staging.DirectoryPath, resumed.Staging.DirectoryPath);
        Assert.Equal(WindowsImageBuildPhase.Prepared, resumed.State.Phase);
        Assert.Equal(2, fixture.Runner.Specs.Count);
    }

    [Fact]
    public async Task CanceledPreparationPreservesDiscoverableStaging()
    {
        using var fixture = new ImageBuildFixture();
        fixture.Runner.CancelCreate = true;

        var failure = await Assert.ThrowsAsync<WindowsImageBuildCanceledException>(
            () => fixture.Builder.BeginAsync(fixture.CreateRequest()));

        Assert.True(Directory.Exists(failure.Staging.DirectoryPath));
        Assert.True(File.Exists(WindowsImageBuildStateStore.GetManifestPath(failure.Staging)));
    }

    [Fact]
    public async Task SuccessfulInstallerInitializesTpmAndPromotesExactRegistryLayout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();

        var installed = await fixture.Builder.RunInstallerAsync(prepared);
        var ready = await fixture.Builder.RunVerificationAsync(installed);
        var image = await fixture.Builder.FinalizeAsync(ready);

        Assert.Equal(WindowsImageBuildPhase.ReadyToVerify, installed.State.Phase);
        Assert.Equal(WindowsImageBuildPhase.ReadyToFinalize, ready.State.Phase);
        Assert.False(Directory.Exists(prepared.Staging.DirectoryPath));
        Assert.False(Directory.Exists(ready.RuntimeDirectory));
        Assert.Equal(
            new[]
            {
                ManagedImageRegistry.BaseImageFileName,
                ManagedImageRegistry.MetadataFileName,
                ManagedImageRegistry.OvmfVariablesTemplateFileName,
                ManagedImageRegistry.SwtpmStateTemplateDirectoryName,
            },
            Directory.EnumerateFileSystemEntries(image.DirectoryPath)
                .Select(Path.GetFileName)
                .OrderBy(static name => name, StringComparer.Ordinal));
        var verification = await fixture.Registry.VerifyAsync(image.ImageId);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Issues));
        var provenance = Assert.IsType<ManagedImageBuildProvenance>(image.Metadata.BuildProvenance);
        Assert.Equal(fixture.WindowsIsoPath, provenance.WindowsIso.Path);
        Assert.Equal(fixture.VirtioWinIsoPath, provenance.VirtioWinIso.Path);
        Assert.Equal(fixture.GuestBridgePath, provenance.GuestBridgeExecutable.Path);
        Assert.Equal("X64", provenance.WindowsArchitecture);
        Assert.Equal(26100, provenance.WindowsBuild);
        Assert.Equal("Professional", provenance.WindowsEditionId);
        Assert.Equal(
            ManagedImageBuildProvenance.GuestSelfReportEvidence,
            provenance.EvidenceKind);
        var autounattend = fixture.Runner.ProvisioningFiles[
            GuestBridgeProvisioningContract.AutounattendFileName];
        var document = System.Xml.Linq.XDocument.Parse(autounattend);
        Assert.NotNull(document.Root);
        System.Xml.Linq.XNamespace unattendNamespace = "urn:schemas-microsoft-com:unattend";
        var productKey = Assert.Single(document.Descendants(unattendNamespace + "ProductKey"));
        Assert.Empty(productKey.Elements(unattendNamespace + "Key"));
        Assert.Equal(
            "Never",
            Assert.Single(productKey.Elements(unattendNamespace + "WillShowUI")).Value);
        var oobeSettings = Assert.Single(
            document.Descendants(unattendNamespace + "settings"),
            element => string.Equals(
                (string?)element.Attribute("pass"),
                "oobeSystem",
                StringComparison.Ordinal));
        var oobeInternational = Assert.Single(
            oobeSettings.Elements(unattendNamespace + "component"),
            element => string.Equals(
                (string?)element.Attribute("name"),
                "Microsoft-Windows-International-Core",
                StringComparison.Ordinal));
        Assert.Equal("ko-KR", Assert.Single(oobeInternational.Elements(unattendNamespace + "InputLocale")).Value);
        Assert.Equal("ko-KR", Assert.Single(oobeInternational.Elements(unattendNamespace + "SystemLocale")).Value);
        Assert.Equal("ko-KR", Assert.Single(oobeInternational.Elements(unattendNamespace + "UILanguage")).Value);
        Assert.Equal("ko-KR", Assert.Single(oobeInternational.Elements(unattendNamespace + "UserLocale")).Value);
        Assert.Contains("<HideOnlineAccountScreens>true</HideOnlineAccountScreens>", autounattend);
        Assert.Contains("<HideLocalAccountScreen>true</HideLocalAccountScreen>", autounattend);
        Assert.Contains("<HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>", autounattend);
        Assert.Contains("<WillWipeDisk>true</WillWipeDisk>", autounattend);
        Assert.Contains("<Key>/IMAGE/INDEX</Key><Value>6</Value>", autounattend);
        Assert.DoesNotContain("Microsoft-Windows-PnpCustomizationsWinPE", autounattend, StringComparison.Ordinal);
        Assert.DoesNotContain("E:\\vioscsi", autounattend, StringComparison.Ordinal);
        Assert.Equal(
            "virtio-driver",
            fixture.Runner.ProvisioningFiles["$WinPEDriver$/vioscsi/vioscsi.inf"]);
        Assert.Contains("<SetupUILanguage><UILanguage>ko-KR</UILanguage></SetupUILanguage>", autounattend);
        Assert.Contains("<InputLocale>ko-KR</InputLocale>", autounattend);
        Assert.Contains("<SystemLocale>ko-KR</SystemLocale>", autounattend);
        Assert.Contains("<UILanguage>ko-KR</UILanguage>", autounattend);
        Assert.Contains("<UserLocale>ko-KR</UserLocale>", autounattend);
        Assert.Contains("<InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>", autounattend);
        Assert.Contains("<Name>linuxcloth</Name>", autounattend);
        Assert.Contains("<AutoLogon>", autounattend);
        Assert.Contains("<LogonCount>1</LogonCount>", autounattend);
        Assert.DoesNotContain("<LogonCount>999</LogonCount>", autounattend);
        Assert.DoesNotContain("SkipMachineOOBE", autounattend, StringComparison.Ordinal);
        Assert.Contains(GuestBridgeProvisioningContract.InstallScriptFileName, autounattend);
        var installScript = fixture.Runner.ProvisioningFiles[
            GuestBridgeProvisioningContract.InstallScriptFileName];
        Assert.Contains("'NetKVM\\w11\\amd64\\netkvm.inf'", installScript);
        Assert.Contains("'viorng\\w11\\amd64\\viorng.inf'", installScript);
        Assert.Contains("'vioscsi\\w11\\amd64\\vioscsi.inf'", installScript);
        Assert.Contains("'vioserial\\w11\\amd64\\vioser.inf'", installScript);
        Assert.Contains("foreach ($relativePath in $virtioDriverPaths)", installScript);
        Assert.DoesNotContain("/subdirs", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Register-ScheduledTask -TaskName 'linuxcloth GuestBridge'", installScript);
        Assert.DoesNotContain("-TaskPath", installScript, StringComparison.Ordinal);
        Assert.Contains("shutdown.exe /s", installScript);
        Assert.Contains("Panther\\Unattend\\unattend.xml", installScript);
        Assert.Contains("DefaultPassword", installScript);
        Assert.Equal(4, fixture.Launcher.Specs.Count);
        Assert.DoesNotContain(
            fixture.Launcher.Specs,
            spec => string.Equals(
                spec.IdentityExecutablePath ?? spec.FileName,
                fixture.Toolchain.RemoteViewer,
                StringComparison.Ordinal));
        Assert.All(fixture.Launcher.Processes, process => Assert.True(process.WasDisposed));
        Assert.False(Directory.Exists(prepared.SetupLocaleDirectory));
    }

    [Fact]
    public async Task MissingBootCriticalVirtioDriverBlocksInstallerPreparation()
    {
        using var fixture = new ImageBuildFixture();
        fixture.Runner.FailVirtioDriverExtraction = true;
        var prepared = await fixture.BeginAsync();

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));

        Assert.Contains("virtio storage driver", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.Staging);
        Assert.True(Directory.Exists(failure.Staging.DirectoryPath));
    }

    [Fact]
    public async Task UppercaseBootCriticalVirtioDriverEntriesAreNormalized()
    {
        using var fixture = new ImageBuildFixture();
        fixture.Runner.UseUppercaseVirtioDriverEntries = true;
        var prepared = await fixture.BeginAsync();

        await fixture.Builder.RunInstallerAsync(prepared);

        Assert.Equal(
            "virtio-driver",
            fixture.Runner.ProvisioningFiles["$WinPEDriver$/vioscsi/vioscsi.inf"]);
        Assert.Equal(
            "virtio-driver",
            fixture.Runner.ProvisioningFiles["$WinPEDriver$/vioscsi/vioscsi.cat"]);
        Assert.Equal(
            "virtio-driver",
            fixture.Runner.ProvisioningFiles["$WinPEDriver$/vioscsi/vioscsi.sys"]);
    }

    [Fact]
    public async Task InstallerFailureReturnsToPreparedPhaseForRetry()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.Launcher.QemuExitCode = 7;

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));
        var resumed = await fixture.Builder.ResumeAsync(
            prepared.Staging.ImageId,
            prepared.Staging.DirectoryPath);

        Assert.Equal(prepared.Staging, failure.Staging);
        Assert.Equal(WindowsImageBuildPhase.Prepared, resumed.State.Phase);
        Assert.True(Directory.Exists(prepared.Staging.DirectoryPath));
    }

    [Fact]
    public async Task ProcessIdentityTimeoutIsNotReportedAsTheInstallationTimeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.Launcher.FailExecutable = fixture.Toolchain.Swtpm;
        fixture.Launcher.LaunchFailure = new TimeoutException(
            "Process '/usr/bin/bwrap' did not expose '/usr/bin/swtpm' within 00:00:05.");

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));

        Assert.Contains("expected executable identity", failure.Message, StringComparison.Ordinal);
        Assert.Contains("00:00:05", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("240 minutes", failure.Message, StringComparison.Ordinal);
        Assert.Equal(prepared.Staging, failure.Staging);
    }

    [Fact]
    public async Task EndpointTimeoutIsNotReportedAsTheInstallationTimeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.EndpointWaiter.Failure = new TimeoutException("simulated socket timeout");

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));

        Assert.Contains("swtpm", failure.Message, StringComparison.Ordinal);
        Assert.Contains("15 seconds", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("240 minutes", failure.Message, StringComparison.Ordinal);
        Assert.Equal(prepared.Staging, failure.Staging);
    }

    [Fact]
    public async Task VerificationRejectsAGuestEditionDifferentFromTheApprovedSelection()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.Builder.BeginAsync(
            fixture.CreateRequest() with
            {
                Installation = new WindowsInstallationSelection(1, "Core", "Windows 11 Home"),
            });
        var installed = await fixture.Builder.RunInstallerAsync(prepared);

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunVerificationAsync(installed));

        Assert.Contains("readiness handshake", failure.Message, StringComparison.OrdinalIgnoreCase);
        var resumed = await fixture.Builder.ResumeAsync(
            installed.Staging.ImageId,
            installed.Staging.DirectoryPath);
        Assert.Equal(WindowsImageBuildPhase.ReadyToVerify, resumed.State.Phase);
    }

    [Fact]
    public async Task InstallerRunsHeadlessWithoutLaunchingViewer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();

        var installed = await fixture.Builder.RunInstallerAsync(prepared);

        Assert.Equal(WindowsImageBuildPhase.ReadyToVerify, installed.State.Phase);
        Assert.DoesNotContain(
            fixture.Launcher.Specs,
            spec => string.Equals(
                spec.IdentityExecutablePath ?? spec.FileName,
                fixture.Toolchain.RemoteViewer,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallerConfirmsBootPromptWithoutSendingKeysDuringVerification()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.Launcher.BlockQemuExit = true;

        var installation = fixture.Builder.RunInstallerAsync(prepared);
        await WaitUntilAsync(
            () => fixture.QmpConnector.Monitor.SentKeys.Count > 0,
            TimeSpan.FromSeconds(5));
        fixture.Launcher.CompleteRunningQemu();
        var installed = await installation.WaitAsync(TimeSpan.FromSeconds(5));
        var installerKeyCount = fixture.QmpConnector.Monitor.SentKeys.Count;
        fixture.Launcher.BlockQemuExit = false;

        _ = await fixture.Builder.RunVerificationAsync(installed);

        Assert.Equal(1, fixture.QmpConnector.ConnectCount);
        Assert.Equal(installerKeyCount, fixture.QmpConnector.Monitor.SentKeys.Count);
        Assert.All(
            fixture.QmpConnector.Monitor.SentKeys,
            keyCode => Assert.Equal(QmpKeyCode.Enter, keyCode));
    }

    [Fact]
    public async Task CleanupFailureKeepsDurableRunningState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.Launcher.BlockQemuExit = true;
        fixture.Launcher.FailQemuTerminate = true;
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Builder.RunInstallerAsync(prepared, cancellation.Token);
        await WaitUntilAsync(
            () => fixture.Launcher.Processes.Any(
                process => process.Identity.ExecutablePath == fixture.Toolchain.QemuSystem),
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAsync<WindowsImageBuildException>(() => run);
        var state = await WindowsImageBuildStateStore.ReadAsync(prepared.Staging);

        Assert.Equal(WindowsImageBuildPhase.InstallerRunning, state.Phase);
        Assert.True(state.ActiveProcesses.ContainsKey(WindowsImageBuildProcessNames.Qemu));
        await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));
    }

    [Fact]
    public async Task ConcurrentInstallerRunIsRejectedByExclusiveBuildLock()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        fixture.Launcher.BlockQemuExit = true;
        var first = fixture.Builder.RunInstallerAsync(prepared);
        await WaitUntilAsync(
            () => fixture.Launcher.Processes.Any(
                process => process.Identity.ExecutablePath == fixture.Toolchain.QemuSystem),
            TimeSpan.FromSeconds(5));

        var secondFailure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));
        fixture.Launcher.CompleteRunningQemu();
        var installed = await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Another process", secondFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WindowsImageBuildPhase.ReadyToVerify, installed.State.Phase);
    }

    [Fact]
    public async Task InterruptedInstallerRequiresExplicitStoppedProcessConfirmation()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var interrupted = prepared with
        {
            State = prepared.State with
            {
                Phase = WindowsImageBuildPhase.InstallerRunning,
                ActiveHostBootId = StubBootIdProvider.BootId,
            },
        };
        await WindowsImageBuildStateStore.WriteAsync(interrupted.Staging, interrupted.State);
        var resumed = await fixture.Builder.ResumeAsync(
            interrupted.Staging.ImageId,
            interrupted.Staging.DirectoryPath);

        await Assert.ThrowsAsync<WindowsImageBuildException>(() => fixture.Builder.RunInstallerAsync(resumed));

        var reset = await fixture.Builder.RecoverInterruptedRunAsync(resumed);
        Assert.Equal(WindowsImageBuildPhase.Prepared, reset.State.Phase);
    }

    [Fact]
    public async Task AbandonRemovesOnlyOwnedStagingAndRuntime()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        Directory.CreateDirectory(prepared.RuntimeDirectory);
        File.WriteAllText(Path.Combine(prepared.RuntimeDirectory, "log"), "diagnostic");
        var unrelated = Path.Combine(fixture.Root, "keep");
        File.WriteAllText(unrelated, "keep");

        await fixture.Builder.AbandonAsync(prepared);

        Assert.False(Directory.Exists(prepared.Staging.DirectoryPath));
        Assert.False(Directory.Exists(prepared.RuntimeDirectory));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task FinalizationFailureRestoresResumableBuilderManifest()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var installed = await fixture.Builder.RunInstallerAsync(prepared);
        var ready = await fixture.Builder.RunVerificationAsync(installed);
        Directory.CreateDirectory(Path.Combine(fixture.Registry.ImagesDirectory, ready.State.ImageId.Value));

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.FinalizeAsync(ready));

        Assert.Equal(ready.Staging, failure.Staging);
        Assert.True(File.Exists(WindowsImageBuildStateStore.GetManifestPath(ready.Staging)));
        var resumed = await fixture.Builder.ResumeAsync(
            ready.Staging.ImageId,
            ready.Staging.DirectoryPath);
        Assert.Equal(WindowsImageBuildPhase.ReadyToFinalize, resumed.State.Phase);
    }

    [Fact]
    public async Task MissingGuestBridgeHandshakeBlocksPromotionAndReturnsToVerificationReady()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var installed = await fixture.Builder.RunInstallerAsync(prepared);
        fixture.Launcher.WriteVerificationResult = false;

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunVerificationAsync(installed));
        var resumed = await fixture.Builder.ResumeAsync(
            installed.Staging.ImageId,
            installed.Staging.DirectoryPath);

        Assert.Contains("handshake", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WindowsImageBuildPhase.ReadyToVerify, resumed.State.Phase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Registry.ImagesDirectory, "windows-11")));
    }

    [Fact]
    public async Task BaseOnlyVerificationDoesNotReattachInstallationMedia()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var installed = await fixture.Builder.RunInstallerAsync(prepared);
        File.Delete(fixture.WindowsIsoPath);
        File.Delete(fixture.VirtioWinIsoPath);

        var ready = await fixture.Builder.RunVerificationAsync(installed);

        Assert.Equal(WindowsImageBuildPhase.ReadyToFinalize, ready.State.Phase);
    }

    [Fact]
    public async Task PendingProcessOnCurrentBootCannotBeRecoveredByAssertion()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var interrupted = prepared with
        {
            State = prepared.State with
            {
                Phase = WindowsImageBuildPhase.InstallerRunning,
                ActiveHostBootId = StubBootIdProvider.BootId,
                PendingProcessName = WindowsImageBuildProcessNames.Qemu,
            },
        };
        await WindowsImageBuildStateStore.WriteAsync(interrupted.Staging, interrupted.State);

        var failure = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RecoverInterruptedRunAsync(interrupted));

        Assert.Contains("Reboot", failure.Message, StringComparison.OrdinalIgnoreCase);
        var state = await WindowsImageBuildStateStore.ReadAsync(interrupted.Staging);
        Assert.Equal(WindowsImageBuildPhase.InstallerRunning, state.Phase);
    }

    [Fact]
    public async Task ActiveBuildCannotBeAbandoned()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var interrupted = prepared with
        {
            State = prepared.State with
            {
                Phase = WindowsImageBuildPhase.InstallerRunning,
                ActiveHostBootId = StubBootIdProvider.BootId,
            },
        };
        await WindowsImageBuildStateStore.WriteAsync(interrupted.Staging, interrupted.State);

        await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.AbandonAsync(interrupted));

        Assert.True(Directory.Exists(interrupted.Staging.DirectoryPath));
    }

    [Fact]
    public async Task ResumeRejectsChangedInstallationMedia()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        await File.AppendAllTextAsync(fixture.WindowsIsoPath, "changed");

        var exception = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.ResumeAsync(
                prepared.Staging.ImageId,
                prepared.Staging.DirectoryPath));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(prepared.Staging.DirectoryPath));
    }

    [Fact]
    public async Task InstallerRejectsLinkedTpmStateBeforeStartingProcesses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var outside = Path.Combine(fixture.Root, "outside-tpm");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(
            Path.Combine(prepared.Staging.SwtpmStateTemplateDirectory, "linked"),
            outside);

        await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.RunInstallerAsync(prepared));

        Assert.Empty(fixture.Launcher.Specs);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task StrictStateManifestRejectsUnknownProperties()
    {
        using var fixture = new ImageBuildFixture();
        var prepared = await fixture.BeginAsync();
        var manifest = WindowsImageBuildStateStore.GetManifestPath(prepared.Staging);
        var json = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(
            manifest,
            json.Replace("{", "{\n  \"unexpected\": true,", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => fixture.Builder.ResumeAsync(
                prepared.Staging.ImageId,
                prepared.Staging.DirectoryPath));

        Assert.Contains("resume failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(prepared.Staging.DirectoryPath));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > timeout)
            {
                throw new TimeoutException("The image-build process did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }
}

using LinuxCloth.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var application = new CliApplication(
    new DefaultCliCommandServices(),
    Console.Out,
    Console.Error);
return await application.RunAsync(args, cancellation.Token).ConfigureAwait(false);

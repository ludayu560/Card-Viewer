using System.Windows;
namespace CardViewer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--decode-raf-worker")
            return RafWorkerDecoder.Run(args[1], args[2]);
        if (args.FirstOrDefault() == "--verify-build")
            return BuildVerification.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--self-test")
            return SelfTests.Run(args.Skip(1).FirstOrDefault() ?? "TestImages").GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--benchmark")
            return ThumbnailBenchmark.RunCommandAsync(args.Skip(1).FirstOrDefault() ?? "TestImages").GetAwaiter().GetResult();
        var app = new Application();
        app.Run(AppBootstrapper.CreateMainWindow(args.FirstOrDefault()));
        return 0;
    }
}

namespace OmlCli;

// The Core side of oml's warm workers (the launcher's WarmWorker): Service Studio starts once per worker, and each
// command then runs as in a fresh process, with the caller's console facts, and with the modules it only reads kept
// open for the next command (Model.Borrow: keyed by full path, write time and length, so a changed file loads again).
public static partial class Cli
{
    // Once, before the worker takes commands: starts Service Studio's model.
    public static void WarmUp(string serviceStudioDir)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        SelfTest.KeepProcess = true;
        SelfTest.Server = "warm worker";
        Model.WarmUp();
        // A first load, inspect and validate on a blank module compile most of what the first real command needs.
        var dir = Path.Combine(Path.GetTempPath(), "oml-warmup-" + Guid.NewGuid().ToString("N"));
        var (output, error) = (Console.Out, Console.Error);
        try
        {
            Directory.CreateDirectory(dir);
            var blank = Path.Combine(dir, "WarmUp.oml");
            var services = Model.Services();
            using (var module = services.CreateESpace(OutSystems.Model.Enumerations.SegmentationKind.CrossDevice, "WarmUp", "", false, services.NewKey()))
                Model.Save(module, blank);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Run(serviceStudioDir, ["inspect", blank, "--own"]);
            Run(serviceStudioDir, ["validate", blank]);
        }
        catch (Exception) { }   // only a head start: the commands work without it
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            Model.EndSharing();
            try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Before each command. Returns why the worker must not run commands any more (a self-test past its limit may still
    // hold the model), or null.
    public static string? WarmBegin(string serviceStudioDir, bool stdoutRedirected, bool stderrRedirected, int width, bool unicode, bool colors, long callerStartupMs)
    {
        if (SelfTest.Abandoned is { } stopped) return stopped;
        Model.ServiceStudioDir = serviceStudioDir;
        ConsoleFacts.Set(stdoutRedirected, stderrRedirected, width, unicode);
        TerminalColors.Enabled = colors;
        TerminalOutput.Reset(null);
        JsonOutput.Pretty = false;
        Timings.Warm(callerStartupMs);
        Model.WarmBegin();
        Model.ShareModules = true;
        Model.TrimShared();
        return null;
    }

    // After each command: above the memory limit, the open modules are closed. Returns the working set in MB after that.
    public static long WarmEnd(long limitMb)
    {
        if (Environment.WorkingSet / (1024 * 1024) > limitMb)
        {
            Model.EndSharing();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return Environment.WorkingSet / (1024 * 1024);
    }
}

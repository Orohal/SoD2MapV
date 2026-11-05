namespace SoD2MapViewer;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // headless smoke test: SoD2MapViewer.exe --smoke "D:\Games\State of Decay 2\StateOfDecay2"
        if (args.Length >= 1 && args[0] == "--diag")
        {
            try
            {
                return Diag.Run(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--smoke-all")
        {
            try
            {
                return Smoke.RunAll(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--patchtest")
        {
            try
            {
                return Smoke.PatchTest(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap",
                    args.Length > 3 ? args[3] : "",
                    args.Length > 4 ? float.Parse(args[4]) : 1000f,
                    args.Length > 5 ? float.Parse(args[5]) : 0f);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--smoke")
        {
            try
            {
                return Smoke.Run(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

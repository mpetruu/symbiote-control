namespace SymbioteControl;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test")
        {
            SelfTest.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] is "--probe" or "--integration-test")
        {
            IntegrationTest.Run(args[1], args[0] == "--integration-test");
            return;
        }
        if (args.Length == 2 && args[0] == "--native-probe")
        {
            IntegrationTest.RunNativeProbe(args[1]).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

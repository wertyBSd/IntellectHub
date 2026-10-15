namespace IntellectHub;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "IntellectHub.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("IntellectHub уже запущен.", "IntellectHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => AppLog.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Error("Unhandled exception", ex);
        };

        Application.Run(new MainForm());
    }
}

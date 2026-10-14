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
        Application.Run(new MainForm());
    }
}

namespace CiMeRecorderGui;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Global\\CiMeRecorderGui.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("이미 실행 중입니다.", "씨미녹화", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }    
}

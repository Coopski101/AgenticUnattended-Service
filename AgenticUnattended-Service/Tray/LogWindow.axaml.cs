using Avalonia.Controls;
using Avalonia.Threading;

namespace AgenticUnattended.Tray;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Icon = App.CreateIcon();
        App.LogSink.LogReceived += OnLogReceived;
        LogText.Text = App.LogSink.GetFullLog();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnLogReceived(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogText.Text += message + "\n";
            LogText.CaretIndex = LogText.Text?.Length ?? 0;
        });
    }
}

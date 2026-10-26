using System.Windows;
using System.Windows.Threading;

namespace PrecisionMotion.UI;

public partial class SplashScreen : Window
{
    private readonly DispatcherTimer _timer;
    private int _progress;

    public SplashScreen()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _progress++;
        ProgressBar.Value = _progress;

        if (_progress == 20)
            StatusText.Text = "Lendo tabelas de parâmetros balísticos...";
        else if (_progress == 60)
            StatusText.Text = "Configurando visão computacional GDI...";
        else if (_progress == 85)
            StatusText.Text = "Preparando interface gráfica...";

        if (_progress >= 100)
        {
            _timer.Stop();
            var mainWin = new MainWindow();
            mainWin.WindowState = WindowState.Maximized;
            mainWin.Show();
            Close();
        }
    }
}

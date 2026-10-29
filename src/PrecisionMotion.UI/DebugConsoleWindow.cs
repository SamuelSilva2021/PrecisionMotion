#if DEBUG
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PrecisionMotion.UI;

/// <summary>
/// Console de acompanhamento em tempo real (log do motor + leituras de reconhecimento).
/// Existe só em builds Debug; em Release o arquivo inteiro não é compilado.
/// </summary>
public sealed class DebugConsoleWindow : Window
{
    private const int MaxLines = 2000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly TextBox _log;
    private readonly TextBlock _live;
    private readonly CheckBox _autoScroll;
    private readonly DispatcherTimer _timer;
    private readonly Func<string>? _liveProvider;
    private int _lineCount;

    public DebugConsoleWindow(Func<string>? liveProvider = null)
    {
        _liveProvider = liveProvider;
        Title = "PrecisionMotion — Console de Debug";
        Width = 900;
        Height = 620;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1C));

        _live = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
            Text = "Aguardando motor..."
        };

        _log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(8, 0, 8, 8)
        };

        _autoScroll = new CheckBox { Content = "Rolagem automática", IsChecked = true, Margin = new Thickness(8), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var clear = new Button { Content = "Limpar", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(4, 6, 4, 6) };
        var copy = new Button { Content = "Copiar", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(4, 6, 4, 6) };
        clear.Click += (_, _) => { _log.Clear(); _lineCount = 0; };
        copy.Click += (_, _) => Clipboard.SetText(_log.Text);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(_autoScroll);
        toolbar.Children.Add(clear);
        toolbar.Children.Add(copy);

        var liveBorder = new Border
        {
            Child = new ScrollViewer { Content = _live, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x42)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(liveBorder, 1);
        Grid.SetRow(_log, 2);
        grid.Children.Add(toolbar);
        grid.Children.Add(liveBorder);
        grid.Children.Add(_log);
        Content = grid;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Seguro para chamar de qualquer thread; as linhas são exibidas em lote.</summary>
    public void Write(string line) => _pending.Enqueue(line);

    private void Flush()
    {
        if (_liveProvider != null)
            _live.Text = _liveProvider();

        if (_pending.IsEmpty)
            return;

        var sb = new System.Text.StringBuilder();
        while (_pending.TryDequeue(out var line))
        {
            sb.AppendLine(line);
            _lineCount++;
        }

        _log.AppendText(sb.ToString());

        if (_lineCount > MaxLines)
        {
            var lines = _log.Text.Split('\n');
            _log.Text = string.Join('\n', lines.Skip(lines.Length - MaxLines / 2));
            _lineCount = MaxLines / 2;
        }

        if (_autoScroll.IsChecked == true)
            _log.ScrollToEnd();
    }
}
#endif

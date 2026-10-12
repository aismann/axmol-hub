using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AxmolHub;

/// <summary>
/// The build progress window. A port of WPF's <c>BuildProgressWindow.cs</c>: it's entirely
/// **code-built** (no XAML), so the port is just swapping WPF types for Avalonia types.
///
/// Stage detection (compile / package / sign) relies on parsing the subprocess command lines, line
/// for line identical to the WPF version — these regexes were tuned against real CMake / Gradle /
/// apksigner output, so read the WPF comments before changing them.
/// </summary>
public sealed class BuildProgressWindow : Window
{
    private readonly TextBlock _stage = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _count = new() { HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brushes.LightGray };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 58, Foreground = Brushes.LightGray };
    private readonly TextBlock _elapsed = new() { Foreground = Brushes.Gray };
    private readonly ProgressBar _progress = new()
    {
        Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = true,
        Foreground = new SolidColorBrush(Color.FromRgb(57, 155, 220)),
        Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
    };
    private readonly Button _cancel = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    // Not readonly: the Closed callback is registered earlier than the timer creation, so the compiler considers it possibly null.
    private Timer? _timer;
    private readonly Action _cancelBuild;
    private bool _finished;
    private bool _cancelling;

    public BuildProgressWindow(string project, string target, string configuration, Action cancelBuild)
    {
        _cancelBuild = cancelBuild;
        Title = HubStrings.Get("BuildProgress");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = project, FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = target + " · " + configuration, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 24) });

        var row = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        row.Children.Add(_count);
        row.Children.Add(_stage);
        panel.Children.Add(row);

        _progress.Margin = new Thickness(0, 14, 0, 14);
        panel.Children.Add(_progress);
        panel.Children.Add(_detail);
        panel.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("BuildProgressHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 14, 0, 22),
        });

        _cancel.Content = HubStrings.Get("Cancel");
        var footer = new DockPanel();
        DockPanel.SetDock(_cancel, Dock.Right);
        footer.Children.Add(_cancel);
        footer.Children.Add(_elapsed);
        panel.Children.Add(footer);

        Content = panel;
        SetStage("BuildPreparing");

        _cancel.Click += (_, _) => RequestCancel();
        Closing += (_, e) =>
        {
            if (_finished)
            {
                return;
            }

            e.Cancel = true;
            RequestCancel();
        };
        Closed += (_, _) =>
        {
            _timer?.Dispose();
            _watch.Stop();
        };

        // Avalonia has no WPF DispatcherTimer, so use a plain Timer + UI-thread post.
        // Without that post you'd be mutating a TextBlock from a background thread — a cross-thread access.
        _timer = new Timer(_ => Dispatcher.UIThread.Post(() =>
            _elapsed.Text = HubStrings.Get("BuildElapsed") + " " + _watch.Elapsed.ToString(@"hh\:mm\:ss")),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _elapsed.Text = HubStrings.Get("BuildElapsed") + " 00:00:00";
    }

    private void RequestCancel()
    {
        if (_cancelling || _finished)
        {
            return;
        }

        _cancelling = true;
        _cancel.IsEnabled = false;
        SetStage("Cancelling");
        _cancelBuild();
    }

    private void SetStage(string key)
    {
        _stage.Text = HubStrings.Get(key);
        _progress.IsIndeterminate = true;
        _count.Text = "";
    }

    public void Report(string message)
    {
        if (_finished || _cancelling)
        {
            return;
        }

        // One reader of the build's own output, shared with the queue's row: these patterns were tuned against
        // real CMake / Gradle / apksigner output, and a copy of them in each surface is how the two start
        // disagreeing about which stage the build is in.
        var reading = TaskQueue.TaskFeed.Parse(message);

        if (reading.StageKey is { } stage
            && (!reading.OnlyAfterDeterminateProgress || !_progress.IsIndeterminate))
        {
            _stage.Text = HubStrings.Get(stage);
            // A stage arriving on its own retires the number the previous stage produced: signing an APK is not
            // 16% of anything.
            if (reading.StepTotal is null && reading.Percent is null)
            {
                _progress.IsIndeterminate = true;
                _count.Text = "";
            }
        }

        if (reading.StepTotal is { } total && total > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = 100.0 * (reading.StepCurrent ?? 0) / total;
            _count.Text = $"{reading.StepCurrent} / {total} · {_progress.Value:0}%";
        }
        else if (reading.Percent is { } percent)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = percent;
            _count.Text = $"{percent:0}%";
        }

        if (reading.Detail is { } line)
        {
            _detail.Text = line.Length > 220 ? line[..217] + "…" : line;
        }
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Close();
    }
}

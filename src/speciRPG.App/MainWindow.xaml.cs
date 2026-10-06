using System.ComponentModel;
using System.Windows;
using speciRPG.App.Input;

namespace speciRPG.App;

public partial class MainWindow : Window
{
    private readonly GlobalKeyboardMonitor? _keyboardMonitor;
    private int _detectedKeyCount;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _keyboardMonitor = new GlobalKeyboardMonitor();
            _keyboardMonitor.KeyPressed += OnTrackedKeyPressed;
            KeyboardStatusText.Text = "監視中: Z / Enter";
        }
        catch (Win32Exception exception)
        {
            KeyboardStatusText.Text = $"キーボード監視を開始できませんでした: {exception.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _keyboardMonitor?.Dispose();
        base.OnClosed(e);
    }

    private void OnTrackedKeyPressed(object? sender, TrackedKeyPressedEventArgs e)
    {
        _detectedKeyCount++;
        LastKeyText.Text = $"最後に検知したキー: {e.KeyName}（合計 {_detectedKeyCount} 回）";
    }
}
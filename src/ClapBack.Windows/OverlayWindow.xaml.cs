using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClapBack.Core.Models;

namespace ClapBack.Windows;

/// <summary>气泡被拖到的新位置（WPF 坐标，DIP）。</summary>
public sealed record OverlayPosition(double Left, double Top);

/// <summary>
/// 游戏内字符气泡：置顶、半透明、无边框的小窗，用来在游戏画面上显示识别结果。
/// </summary>
/// <remarks>
/// 几处关键选择：
/// <list type="bullet">
/// <item>加 <c>WS_EX_NOACTIVATE</c>：点它不会把焦点从游戏抢走，否则拖动一下就可能丢掉输入焦点。</item>
/// <item>加 <c>WS_EX_TOOLWINDOW</c> + <c>ShowInTaskbar=False</c>：不占任务栏、不进 Alt+Tab。</item>
/// <item>只在有内容时显示，到点自动隐藏：平时完全不挡鼠标，也不会一直压在画面上。</item>
/// <item>位置用 WPF 的 DIP 坐标存取，拖动时把屏幕物理像素换算回 DIP，避免高 DPI 下越拖越偏。</item>
/// </list>
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer _hideTimer = new();
    private bool _dragging;
    private double _shownLeft;
    private double _shownTop;
    private DateTime _lastPersist = DateTime.MinValue;
    // 注意：本项目同时开了 UseWPF 和 UseWindowsForms，Point / MouseEventArgs 会与
    // System.Drawing / System.Windows.Forms 里的同名类型冲突，所以这里必须写全名。
    private System.Windows.Point _grabOffset;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        // 这个窗口是 WS_EX_NOACTIVATE 的，鼠标捕获在极少数情况下会先丢、等不到 MouseUp；
        // 丢捕获也算拖动结束，否则「拖完松手」时位置就记不下来。
        LostMouseCapture += (_, _) => FinishDrag();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); PersistMovedPosition(); Hide(); };
    }

    /// <summary>用户拖动气泡结束后触发，参数是新的左上角位置（DIP）。</summary>
    public event EventHandler<OverlayPosition>? PositionChanged;

    /// <summary>按设置刷新外观；气泡正在显示时立即生效（设置里的预览能直接看到效果）。</summary>
    public void ApplySettings(ClapBackSettings settings)
    {
        BubbleText.FontSize = Math.Clamp(settings.OverlayFontSize, 12, 72);
        Opacity = Math.Clamp(settings.OverlayOpacity, 0.2, 1.0);
    }

    /// <summary>
    /// 显示一条气泡文字。位置取设置里上次拖动记录下来的坐标，没有记录就放在主屏底部居中。
    /// </summary>
    public void ShowMessage(string text, ClapBackSettings settings, bool showDragHint = false)
    {
        ApplySettings(settings);
        BubbleText.Text = text;
        HintText.Visibility = showDragHint ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible)
        {
            PlaceAt(settings);
        }

        Show();
        UpdateLayout();
        _shownLeft = Left;
        _shownTop = Top;

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.OverlayDurationSeconds, 1, 60));
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        PersistMovedPosition();
        Hide();
    }

    private void PlaceAt(ClapBackSettings settings)
    {
        var work = SystemParameters.WorkArea;
        var width = Width > 0 ? Width : 560;
        var left = settings.OverlayLeft ?? (work.Left + ((work.Width - width) / 2));
        var top = settings.OverlayTop ?? (work.Bottom - 160);
        Left = Clamp(left, work.Left, work.Right - width);
        Top = Clamp(top, work.Top, work.Bottom - 48);
    }

    /// <summary>把窗口夹回可见区域：换分辨率或拔掉副屏后，旧坐标可能已经跑到屏幕外。</summary>
    private void ClampToScreen()
    {
        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 80;
        Left = Clamp(Left, work.Left - 8, work.Right - width + 8);
        Top = Clamp(Top, work.Top - 8, work.Bottom - height + 8);
    }

    private static double Clamp(double value, double min, double max)
        => max <= min ? min : Math.Min(Math.Max(value, min), max);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var updated = style | WsExNoActivate | WsExToolWindow;
        if (updated != style)
        {
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(updated));
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = true;
        _grabOffset = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // 窗口是 WS_EX_NOACTIVATE 的，极少数情况下松手事件送不到；按钮状态变了就本地收尾。
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }

        var device = PointToScreen(Mouse.GetPosition(this));
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        // PointToScreen 给的是物理像素；窗口 Left/Top 是 DIP，必须换算回 DIP 才不会被 DPI 放大。
        var dip = toDip.Transform(device);
        Left = dip.X - _grabOffset.X;
        Top = dip.Y - _grabOffset.Y;
        PersistIfDue();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        FinishDrag();
        e.Handled = true;
    }

    /// <summary>收尾一次拖动：夹回屏幕、收起提示、把新位置抛给上层持久化。</summary>
    private void FinishDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        ClampToScreen();
        HintText.Visibility = Visibility.Collapsed;
        PositionChanged?.Invoke(this, new OverlayPosition(Left, Top));
    }

    /// <summary>
    /// 拖动过程中节流上报位置（约每 400ms 一次）。
    /// <para>
    /// 不把「记住位置」只押在松手事件上：这个窗口是 <c>WS_EX_NOACTIVATE</c> 的，
    /// 实测注入/合成鼠标输入时可能收不到 MouseUp，那样用户拖完的位置就丢了。
    /// 边拖边记最稳，松手时再补一次精确值。
    /// </para>
    /// </summary>
    private void PersistIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPersist < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _lastPersist = now;
        _shownLeft = Left;
        _shownTop = Top;
        PositionChanged?.Invoke(this, new OverlayPosition(Left, Top));
    }

    /// <summary>
    /// 气泡被拖动过就再兜一次位置持久化。放在「隐藏」这一刻：即使松手事件没送到，
    /// 下次也会从用户拖到的地方弹出，而不是回到原来的位置。
    /// </summary>
    private void PersistMovedPosition()
    {
        if (Math.Abs(Left - _shownLeft) < 0.5 && Math.Abs(Top - _shownTop) < 0.5)
        {
            return;
        }

        _shownLeft = Left;
        _shownTop = Top;
        PositionChanged?.Invoke(this, new OverlayPosition(Left, Top));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

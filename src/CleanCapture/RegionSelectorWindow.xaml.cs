using System;
using System.Drawing;
using System.Windows;
using System.Windows.Input;

namespace CleanCapture;

/// <summary>
/// 区域框选窗口：全屏遮罩，拖框选区。
/// 关键点：WPF 的坐标是“逻辑单位”，截图要的是“真实像素”，
/// 所以返回之前要乘以当前显示器的 DPI 缩放（125% 就是 1.25）。
/// 多屏时以鼠标所在的屏幕为准，见 GetDpiScale。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private System.Windows.Point _startPoint;
    private bool _dragging;

    /// <summary>用户选好的区域（真实像素），取消时为 null。</summary>
    public Rectangle? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SelectedRegion = null;
                Close();
            }
        };
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootCanvas);
        _dragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        SizeText.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(RootCanvas);
        var left = Math.Min(_startPoint.X, current.X);
        var top = Math.Min(_startPoint.Y, current.Y);
        var width = Math.Abs(current.X - _startPoint.X);
        var height = Math.Abs(current.Y - _startPoint.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionRect, left);
        System.Windows.Controls.Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        var scale = GetDpiScale();
        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);
        SizeText.Text = $"{pixelWidth} × {pixelHeight} 像素";
        System.Windows.Controls.Canvas.SetLeft(SizeText, left);
        System.Windows.Controls.Canvas.SetTop(SizeText, top + height + 8);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(RootCanvas);
        var left = Math.Min(_startPoint.X, end.X);
        var top = Math.Min(_startPoint.Y, end.Y);
        var width = Math.Abs(end.X - _startPoint.X);
        var height = Math.Abs(end.Y - _startPoint.Y);

        // 框得太小（小于 10×10 像素）当成误点，直接取消。
        if (width < 10 || height < 10)
        {
            SelectedRegion = null;
            Close();
            return;
        }

        var scale = GetDpiScale();
        SelectedRegion = new Rectangle(
            (int)Math.Round(left * scale),
            (int)Math.Round(top * scale),
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale));
        Close();
    }

    /// <summary>当前显示器的 DPI 缩放：100% 是 1.0，125% 是 1.25，依此类推。</summary>
    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
    }
}

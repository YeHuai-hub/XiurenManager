using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XiurenManager.Controls;

public sealed class ZoomPanImage : Grid
{
    private readonly Image image = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(0.5, 0.5),
        SnapsToDevicePixels = true
    };
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform translate = new();
    private Point dragStart;
    private Point translateStart;
    private bool dragging;

    public ZoomPanImage()
    {
        Background = Brushes.Transparent;
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        image.RenderTransform = transforms;
        Children.Add(image);
        ClipToBounds = true;
        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => EndDrag();
    }

    public ImageSource? Source
    {
        get => image.Source;
        set
        {
            image.Source = value;
            ResetView();
        }
    }

    public void ResetView()
    {
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        var next = Math.Clamp(scale.ScaleX * factor, 1, 12);
        scale.ScaleX = next;
        scale.ScaleY = next;
        if (next == 1)
        {
            translate.X = 0;
            translate.Y = 0;
        }
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }
        if (scale.ScaleX <= 1) return;
        dragStart = e.GetPosition(this);
        translateStart = new Point(translate.X, translate.Y);
        dragging = CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        translate.X = translateStart.X + point.X - dragStart.X;
        translate.Y = translateStart.Y + point.Y - dragStart.Y;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}

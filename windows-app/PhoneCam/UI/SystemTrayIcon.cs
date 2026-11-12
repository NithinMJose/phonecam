using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PhoneCam.UI;

public sealed class SystemTrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _icon;
    private readonly Forms.ToolStripMenuItem _cameraItem;
    private readonly Forms.ToolStripMenuItem _statusItem;

    public event Action? ShowRequested;
    public event Action? SwitchCameraRequested;
    public event Action? ExitRequested;

    public SystemTrayIcon()
    {
        _icon = CreateCameraIcon();

        var menu = new Forms.ContextMenuStrip();
        _statusItem = new Forms.ToolStripMenuItem("PhoneCam: Waiting") { Enabled = false };
        var openItem = new Forms.ToolStripMenuItem("Open PhoneCam");
        openItem.Click += (_, _) => ShowRequested?.Invoke();

        _cameraItem = new Forms.ToolStripMenuItem("Switch Camera (Back/Front)");
        _cameraItem.Click += (_, _) => SwitchCameraRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(_cameraItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "PhoneCam",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void UpdateStatus(string text)
    {
        var shortText = text.Length > 60 ? text[..60] + "..." : text;
        _notifyIcon.Text = shortText;
        _statusItem.Text = shortText;
    }

    private static Drawing.Icon CreateCameraIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PhoneCam.ico"));
            if (info?.Stream != null)
            {
                using (info.Stream)
                {
                    return new Drawing.Icon(info.Stream, new Drawing.Size(32, 32));
                }
            }
        }
        catch
        {
        }

        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(37, 99, 235));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            using var lensBrush = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillEllipse(lensBrush, 8, 8, 16, 16);

            using var innerBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(17, 24, 39));
            g.FillEllipse(innerBrush, 11, 11, 10, 10);

            using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(96, 165, 250));
            g.FillEllipse(dotBrush, 14, 14, 4, 4);
        }

        IntPtr hIcon = bmp.GetHicon();
        return Drawing.Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}


using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace WinDynamicNotch.Tray;

public sealed class TrayIconManager : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinDynamicNotch";

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _autostartItem;
    private readonly Icon _icon;
    private readonly Stream? _iconStream;

    public TrayIconManager(Action toggleWindow, Action exit)
    {
        (_icon, _iconStream) = LoadIcon();

        _autostartItem = new WinForms.ToolStripMenuItem("Запускать с Windows")
        {
            Checked = IsAutostartEnabled()
        };
        _autostartItem.Click += (_, _) =>
        {
            var enable = !_autostartItem.Checked;
            SetAutostart(enable);
            _autostartItem.Checked = IsAutostartEnabled();
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem("Показать / скрыть", null, (_, _) => toggleWindow()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Выход", null, (_, _) => exit()));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "WinDynamicNotch",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => toggleWindow();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
        _iconStream?.Dispose();
    }

    /// <summary>
    /// Иконка из вшитого app.ico (тот же файл, что у exe и установщика).
    /// Если ресурс недоступен — рисуем запасную иконку вручную.
    /// </summary>
    private static (Icon Icon, Stream? Stream) LoadIcon()
    {
        try
        {
            var resource = typeof(TrayIconManager).Assembly
                .GetManifestResourceStream("WinDynamicNotch.app.ico");

            if (resource is not null)
            {
                var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                resource.Dispose();
                buffer.Position = 0;

                // Поток нужно держать открытым, пока живёт иконка.
                var icon = new Icon(buffer, WinForms.SystemInformation.SmallIconSize);
                return (icon, buffer);
            }
        }
        catch
        {
            // Ничего страшного: ниже нарисуем иконку сами.
        }

        return (CreateTrayIcon(), null);
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                key.SetValue(ValueName, $"\"{path}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var path = CreateRoundedRect(new RectangleF(3, 2, 26, 21), 8f);
            using var brush = new SolidBrush(Color.FromArgb(255, 243, 243, 248));
            graphics.FillPath(brush, path);

            using var cutout = new SolidBrush(Color.FromArgb(255, 22, 22, 26));
            graphics.FillEllipse(cutout, 13, 9, 6, 5);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

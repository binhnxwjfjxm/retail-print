using System.Drawing;
using Forms = System.Windows.Forms;

namespace RetailPrint.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Action<bool> _setStartWithWindows;
    private bool _suppressStartupEvent;

    public TrayService(Action showWindow, Func<Task> testPrint, Action<bool> setStartWithWindows, Action exit, bool startupEnabled)
    {
        _setStartWithWindows = setStartWithWindows;
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Mở Retail Print");
        var testItem = new Forms.ToolStripMenuItem("In thử");
        _startupItem = new Forms.ToolStripMenuItem("Khởi động cùng Windows") { CheckOnClick = true, Checked = startupEnabled };
        var exitItem = new Forms.ToolStripMenuItem("Thoát");
        openItem.Click += (_, _) => showWindow();
        testItem.Click += async (_, _) => await testPrint();
        _startupItem.CheckedChanged += StartupItem_CheckedChanged;
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(openItem); menu.Items.Add(testItem); menu.Items.Add(_startupItem); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(exitItem);
        _notifyIcon = new Forms.NotifyIcon { Icon = LoadApplicationIcon(), Text = "Retail Print", Visible = true, ContextMenuStrip = menu };
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) showWindow(); };
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null) return icon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    private void StartupItem_CheckedChanged(object? sender, EventArgs e)
    {
        if (!_suppressStartupEvent) _setStartWithWindows(_startupItem.Checked);
    }

    public void SetStartupChecked(bool enabled)
    {
        if (_startupItem.Checked == enabled) return;
        _suppressStartupEvent = true;
        try { _startupItem.Checked = enabled; }
        finally { _suppressStartupEvent = false; }
    }

    public void ShowStatus(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

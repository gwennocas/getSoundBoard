using System.Drawing;
using System.Windows.Forms;

namespace GetSoundBoard.Services;

public sealed class TrayService : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _icon;

    public TrayService(Action open, Action stop, Action exit)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Abrir getSoundBoard", null, (_, _) => open());
        _menu.Items.Add("Parar tudo", null, (_, _) => stop());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "getSoundBoard",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

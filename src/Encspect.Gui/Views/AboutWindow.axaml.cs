using Avalonia.Controls;
using Avalonia.Input;
using Encspect.Host;

namespace Encspect.Gui.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + ReportJsonWriter.ToolVersion;
        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
    }
}

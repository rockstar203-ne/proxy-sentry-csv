using System.Diagnostics;
using System.Runtime.InteropServices;

var root = AppContext.BaseDirectory;
var appPath = Path.Combine(root, "components", "Proxy Sentry CSV.exe");

if (!File.Exists(appPath))
{
    MessageBoxW(
        IntPtr.Zero,
        $"Program components were not found:\n{appPath}",
        "Proxy Sentry CSV",
        0x00000010);
    return 1;
}

Process.Start(new ProcessStartInfo
{
    FileName = appPath,
    WorkingDirectory = Path.GetDirectoryName(appPath) ?? root,
    UseShellExecute = false
});

return 0;

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

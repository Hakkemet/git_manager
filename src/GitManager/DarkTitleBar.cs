using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GitManager;

/// <summary>Barra de título escura do Windows 10/11 (ignorada silenciosamente em versões sem suporte).</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));      // builds antigos do Win10
        }
        catch
        {
            // Sem DWM: mantém a barra padrão
        }
    }
}

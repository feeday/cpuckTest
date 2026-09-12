using System.Reflection;
using System.Runtime.CompilerServices;

namespace CPUCKTest;

/// <summary>
/// MainForm still constructs its legacy layout before UiEnhancer replaces it.
/// On a fast machine that old Temperature History panel can be painted for one frame.
/// Rebuild the final UI on WM_SHOWWINDOW, before the first visible paint, so there is
/// no startup flash. UiEnhancer's normal Idle handler is left in place as a fallback.
/// </summary>
internal static class StartupUiFix
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.AddMessageFilter(new BeforeShowFilter());
    }

    sealed class BeforeShowFilter : IMessageFilter
    {
        const int WM_SHOWWINDOW = 0x0018;
        bool prepared;

        public bool PreFilterMessage(ref Message m)
        {
            if (prepared || m.Msg != WM_SHOWWINDOW || m.WParam == IntPtr.Zero)
                return false;

            if (Control.FromHandle(m.HWnd) is not MainForm main)
                return false;

            prepared = true;

            try
            {
                // Use the same final layout methods that UiEnhancer normally runs on Idle,
                // but do it before Windows makes MainForm visible.
                InvokeUiEnhancer("ApplyFixedWindow", main);
                InvokeUiEnhancer("RebuildLayout", main);
                InvokeUiEnhancer("EnableDoubleBuffering", main);
            }
            catch
            {
                // Never block window creation. UiEnhancer's regular Idle fallback will still run.
            }

            return false;
        }

        static void InvokeUiEnhancer(string methodName, MainForm main)
        {
            var method = typeof(UiEnhancer).GetMethod(methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { main });
        }
    }
}

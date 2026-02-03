using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace FileCopyExtension
{
    [Guid("b3c8b417-6405-4f38-89c0-213123456789")] // Случайный GUID для окна
    public class HgToolWindow : ToolWindowPane
    {
        public HgToolWindow() : base(null)
        {
            Caption = "HG Modified Files";
            Content = new HgToolWindowControl();
        }
    }
}
using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace HgVs
{
    [Guid("ab80bc1f-c497-4cea-87e0-a68ccb644b49")]
    public class HgToolWindow : ToolWindowPane
    {
        public HgToolWindow() : base(null)
        {
            Caption = "HG Modified Files";
            Content = new Views.HgToolWindowControl();
        }
    }
}

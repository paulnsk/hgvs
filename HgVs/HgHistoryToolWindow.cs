using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace HgVs
{
    [Guid("41f2fe11-8929-433b-afe4-40e5ad045665")]
    public class HgHistoryToolWindow : ToolWindowPane
    {
        public HgHistoryToolWindow() : base(null)
        {
            Caption = "HG History";
            Content = new Views.HgHistoryToolWindowControl();
        }
    }
}

using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace FileCopyExtension
{
    [Guid("c4d9b528-7516-4e49-9bd1-324235678910")]
    public class HgHistoryToolWindow : ToolWindowPane
    {
        public HgHistoryToolWindow() : base(null)
        {
            Caption = "HG History";
            Content = new Views.HgHistoryToolWindowControl();
        }
    }
}
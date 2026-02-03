using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FileCopyExtension
{
    internal sealed class TotalCmdDumpCommand
    {
        public const int CommandId = 0x0101; // Переиспользован ID от HgOpenFiles
        public static readonly Guid CommandSet = new Guid("e3636cf9-410e-4892-81e6-2fc4f329c0aa");

        private readonly AsyncPackage package;

        private TotalCmdDumpCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static TotalCmdDumpCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new TotalCmdDumpCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dumpPath = FileDumpLogic.DumpOpenFiles(package);

            if (!string.IsNullOrEmpty(dumpPath))
            {
                var totalCmdPath = @"c:\Program Files\totalcmd\TOTALCMD64.EXE";

                try
                {
                    // /O - activate if running
                    // /T - new tab
                    // /L - set path in active panel
                    var args = $"/O /T /L=\"{dumpPath}\"";
                    Process.Start(totalCmdPath, args);
                }
                catch (Exception ex)
                {
                    // Fallback or error
                    Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                       package,
                       $"Dump created at {dumpPath}, but failed to launch TotalCmd: {ex.Message}",
                       "Warning",
                       Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                       Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
                       Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }
        }
    }
}

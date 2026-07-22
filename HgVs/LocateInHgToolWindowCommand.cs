using EnvDTE;
using EnvDTE80;
using HgVs.ViewModels;
using HgVs.Views;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace HgVs
{
    internal sealed class LocateInHgToolWindowCommand
    {
        public const int CommandId = 0x0104;
        public static readonly Guid CommandSet = new Guid("0ad800ce-c2db-409b-82fc-f656ddb11660");

        private readonly AsyncPackage package;

        private LocateInHgToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, commandId));
        }

        public static LocateInHgToolWindowCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new LocateInHgToolWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("HgVs/LocateInHgModifiedFiles");
        }

        private async Task ExecuteAsync()
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE2;
            var document = dte?.ActiveDocument;
            if (document == null || string.IsNullOrEmpty(document.FullName)) return;

            var fullPath = document.FullName;
            var caretLine = (document.Selection as TextSelection)?.ActivePoint?.Line ?? 1;

            var window = package.FindToolWindow(typeof(HgToolWindow), 0, true);
            if (window?.Frame == null ||
                !(window.Content is HgToolWindowControl control) ||
                !(control.DataContext is HgToolWindowViewModel viewModel))
            {
                return;
            }

            await viewModel.EnsureItemsLoadedAsync();
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var item = viewModel.FindVisibleItem(fullPath, caretLine);
            if (item == null) return;

            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)window.Frame).Show());
            control.FocusItem(item);
        }
    }
}

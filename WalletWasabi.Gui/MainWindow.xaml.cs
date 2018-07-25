using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml;
using AvalonStudio.Extensibility;
using AvalonStudio.Shell;
using AvalonStudio.Shell.Controls;
using WalletWasabi.Gui.Tabs.WalletManager;

namespace WalletWasabi.Gui
{
	public class MainWindow : MetroWindow
	{
		public MainWindow()
		{
			InitializeComponent();

			this.AttachDevTools();
		}

		private void InitializeComponent()
		{
			Activated += OnActivated;
			Closing += OnClosing;
			AvaloniaXamlLoader.Load(this);
		}

		private void OnActivated(object sender, EventArgs e)
		{
			Activated -= OnActivated;
			DisplayWalletManager();
		}

		private void DisplayWalletManager()
		{
			var isAnyWalletAvailable = Directory.Exists(Global.WalletsDir) && Directory.EnumerateFiles(Global.WalletsDir).Any();

			var walletManagerViewModel = new WalletManagerViewModel();
			IoC.Get<IShell>().AddDocument(walletManagerViewModel);

			if (isAnyWalletAvailable)
			{
				walletManagerViewModel.SelectLoadWallet();
			}
			else
			{
				walletManagerViewModel.SelectGenerateWallet();
			}
		}

		private void OnClosing(object sender, CancelEventArgs e)
		{
			var mustClose = AskCloseAndDequeCoins();
			e.Cancel = !mustClose;
			if(mustClose)
			{
				Closing -= OnClosing;
			}
		}

		private bool AskCloseAndDequeCoins()
		{
			return true;
		} 
	}
}

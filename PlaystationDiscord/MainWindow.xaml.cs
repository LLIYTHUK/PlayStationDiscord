﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Security.Cryptography;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlaystationDiscord
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private PSN m_Playstation;
		private DiscordController DiscordController { get; set; } = new DiscordController();
		private CancellationTokenSource DiscordCts = new CancellationTokenSource();

		private string CurrentGame { get; set; } = default(string);
		private DateTime TimeStarted { get; set; } = default(DateTime);

		public PSN Playstation
		{
			private get => m_Playstation;
			set
			{
				m_Playstation = value;
				WriteTokens(value.Tokens);
				Start();
			}
		}

		private string ApplicationDataDirectory
		{
			get => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/PS4Discord";
		}

		private string TokensFile
		{
			get => ApplicationDataDirectory + "/tokens.dat";
		}

		private void WriteTokens(Tokens tokens)
		{
			// TODO - Maybe use a serializer here for the entire Tokens object
			var savedTokens = $"{tokens.access_token}:{tokens.refresh_token}";
			var stored = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(savedTokens), null, DataProtectionScope.LocalMachine));
			if (!Directory.Exists(ApplicationDataDirectory)) Directory.CreateDirectory(ApplicationDataDirectory);
			File.WriteAllText(TokensFile, stored);
		}

		private Tokens CheckForTokens()
		{
			if (!File.Exists(TokensFile)) throw new FileNotFoundException();
			var storedTokens = File.ReadAllText(TokensFile);
			var tokens = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(storedTokens), null, DataProtectionScope.LocalMachine));
			var pieces = tokens.Split(':');
			return new Tokens()
			{
				access_token = pieces[0],
				refresh_token = pieces[1]
			};
		}

		private void Start()
		{
			new DiscordController().Initialize();

			DiscordRPC.UpdatePresence(ref DiscordController.presence);

			DiscordCts = new CancellationTokenSource();

			Task.Run(() => Update(DiscordCts.Token));
		}

		private void Stop()
		{
			DiscordCts.Cancel();

			DiscordRPC.Shutdown();
		}

		public async Task Update(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				UpdatePresence();

				try
				{
					await Task.Delay(TimeSpan.FromSeconds(30), token);
				}
				catch (TaskCanceledException)
				{
					break;
				}
			}
		}

		private void UpdatePresence()
		{
			var game = FetchGame();


			// Hack - This is a mess
			// So apparently, either something with `ref` in C# OR something with Discord messes up Unicode literals
			// To fix this, instead of passing a string to the struct and sending that over to RPC, we need to make a pointer to it
			// Dirty, but fixes the Unicode characters.
			// https://github.com/discordapp/discord-rpc/issues/119#issuecomment-363916563


			var currentStatus = game.titleName ?? game.onlineStatus;
			var encoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(currentStatus));
			encoded += "\0\0"; // Null terminate for the pointer

			var pointer = Marshal.AllocCoTaskMem(Encoding.UTF8.GetByteCount(encoded));
			Marshal.Copy(Encoding.UTF8.GetBytes(encoded), 0, pointer, Encoding.UTF8.GetByteCount(encoded));

			DiscordController.presence = new DiscordRPC.RichPresence()
			{
				largeImageKey = "ps4_main",
				largeImageText = pointer,
			};

			DiscordController.presence.details = pointer;

			if (game.gameStatus != null) DiscordController.presence.state = @game.gameStatus;

			if (game.npTitleId != null)
			{
				if (!game.npTitleId.Equals(CurrentGame))
				{
					DiscordController.presence.startTimestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
					CurrentGame = game.npTitleId;
					TimeStarted = DateTime.UtcNow;
				}
				else
				{
					DiscordController.presence.startTimestamp = (long)(TimeStarted - new DateTime(1970, 1, 1)).TotalSeconds;
				}

			}

			DiscordRPC.UpdatePresence(ref DiscordController.presence);

			// Leak? - Not sure if this is the right method to free the marshal'd mem
			Marshal.FreeCoTaskMem(pointer);
		}

		private ProfileRoot GetProfile()
		{
			return Task.Run(async () => await Playstation.Info()).Result; // Deadlock
		}

		private Presence FetchGame()
		{
			var data = GetProfile();
			return data.profile.presences[0];
		}

		private void LoadComponents()
		{
			try
			{
				var tokens = CheckForTokens();

				Playstation = new PSN(tokens).Refresh();

				var info = GetProfile();

				lblWelcome.Content = $"Welcome, {info.profile.onlineId}!";

				var bitmap = new BitmapImage();
				bitmap.BeginInit();
				bitmap.UriSource = new Uri(info.profile.avatarUrls[1].avatarUrl, UriKind.Absolute);
				bitmap.EndInit();

				imgAvatar.Source = bitmap;

				btnSignIn.Visibility = Visibility.Hidden;
				lblWelcome.Visibility = Visibility.Visible;
				imgAvatar.Visibility = Visibility.Visible;
				lblEnableRP.Visibility = Visibility.Visible;
				togEnableRP.Visibility = Visibility.Visible;
			}
			catch (FileNotFoundException)
			{
				btnSignIn.Visibility = Visibility.Visible;
				lblWelcome.Visibility = Visibility.Hidden;
				imgAvatar.Visibility = Visibility.Hidden;
				lblEnableRP.Visibility = Visibility.Hidden;
				togEnableRP.Visibility = Visibility.Hidden;
			}
		}

		public MainWindow()
		{
			InitializeComponent();

			LoadComponents();
		}

		private void Button_Click(object sender, RoutedEventArgs e)
		{
			var signIn = new SignIn();
			signIn.Closed += SignIn_Closed;
			signIn.Show();
		}

		private void SignIn_Closed(object sender, EventArgs e)
		{
			if (Playstation == null) return;

			LoadComponents();
		}

		private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			DiscordRPC.Shutdown();
		}

		private void togEnableRP_PreviewMouseUp(object sender, MouseButtonEventArgs e)
		{
			if (togEnableRP.IsOn) Stop();
			else Start();
		}
	}
}
﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WindowsDesktop;
using MetroTrilithon.Lifetime;
using SylphyHorn.Interop;
using SylphyHorn.Properties;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.UI;
using SylphyHorn.UI.Bindings;

namespace SylphyHorn
{
	using ActionRegister = Func<Func<ShortcutKey>, Action<IntPtr>, IDisposable>;

	public class ApplicationPreparation
	{
		private readonly HookService _hookService;
		private readonly Action _shutdownAction;
		private readonly IDisposableHolder _disposable;
		private TaskTrayIcon _taskTrayIcon;

		public event Action VirtualDesktopInitialized;

		public event Action VirtualDesktopInitializationCanceled;

		public event Action<Exception> VirtualDesktopInitializationFailed;

		public ApplicationPreparation(HookService hookService, Action shutdownAction, IDisposableHolder disposable)
		{
			this._hookService = hookService;
			this._shutdownAction = shutdownAction;
			this._disposable = disposable;

			this._hookService.Reload = () =>
			{
				this.RegisterActions();
			};
			this._hookService.Suspended += () =>
			{
				this.ResizePropertyList();
			};
		}

		public void RegisterActions()
		{
			this.RegisterActions(Settings.ShortcutKey, this._hookService.RegisterKeyAction);
			this.RegisterActions(Settings.MouseShortcut, this._hookService.RegisterMouseAction);
		}

		public TaskTrayIcon CreateTaskTrayIcon()
		{
			if (this._taskTrayIcon == null)
			{
				const string iconUri = "pack://application:,,,/SylphyHorn;Component/.assets/tasktray.ico";

				if (!Uri.TryCreate(iconUri, UriKind.Absolute, out var uri)) return null;

				var icon = IconHelper.GetIconFromResource(uri);
				var menus = new[]
				{
					new TaskTrayIconItem(Resources.TaskTray_Menu_Settings, ShowSettings, () => Application.Args.CanSettings),
					new TaskTrayIconItem(Resources.TaskTray_Menu_Exit, this._shutdownAction),
				};

				this._taskTrayIcon = new TaskTrayIcon(icon, menus);
			}

			return this._taskTrayIcon;

			void ShowSettings()
			{
				using (this._hookService.Suspend())
				{
					if (SettingsWindow.Instance != null)
					{
						SettingsWindow.Instance.Activate();
					}
					else
					{
						SettingsWindow.Instance = new SettingsWindow
						{
							DataContext = new SettingsWindowViewModel(this._hookService),
						};

						SettingsWindow.Instance.ShowDialog();
						SettingsWindow.Instance = null;
					}
				}
			}
		}

		public TaskTrayBaloon CreateFirstTimeBaloon()
		{
			var baloon = this.CreateTaskTrayIcon().CreateBaloon();
			baloon.Title = ProductInfo.Title;
			baloon.Text = Resources.TaskTray_FirstTimeMessage;
			baloon.Timespan = TimeSpan.FromMilliseconds(5000);

			return baloon;
		}

		public void PrepareVirtualDesktop()
		{
			var provider = new VirtualDesktopProvider()
			{
				ComInterfaceAssemblyPath = Path.Combine(Directories.LocalAppData.FullName, "assemblies"),
			};

			VirtualDesktop.Provider = provider;
			VirtualDesktop.Provider.Initialize().ContinueWith(Continue, TaskScheduler.FromCurrentSynchronizationContext());

			void Continue(Task t)
			{
				switch (t.Status)
				{
					case TaskStatus.RanToCompletion:
						this.OverrideVirtualDesktopsIfNeeded();
						this.RegisterActions();
						this.RegisterVirtualDesktopEvents();
						this.VirtualDesktopInitialized?.Invoke();
						break;

					case TaskStatus.Canceled:
						this.VirtualDesktopInitializationCanceled?.Invoke();
						break;

					case TaskStatus.Faulted:
						this.VirtualDesktopInitializationFailed?.Invoke(t.Exception);
						break;
				}
			}
		}

		private void OverrideVirtualDesktopsIfNeeded()
		{
			if (ProductInfo.IsWindows11OrLater)
			{
				var generalSettings = Settings.General;
				var hasSettings = generalSettings.DesktopNames.Count > 0 || generalSettings.DesktopBackgroundImagePaths.Count > 0;
				if (hasSettings && generalSettings.OverrideDesktopsOnStartup)
				{
					this.FitDesktopsWithPropertyList();
					this.UpdateDesktopSettingsByPropertyList();
				}
				else
				{
					this.ResizePropertyList();
					this.ApplyDesktopSettingsToPropertyList();
				}
			}
			else
			{
				this.ResizePropertyList();
			}
			WallpaperService.SetPosition(VirtualDesktop.Current);
		}

		private void RegisterVirtualDesktopEvents()
		{
			var idCaches = VirtualDesktop.GetDesktops().Select(d => d.Id).ToArray();
			VirtualDesktop.Created += (sender, args) =>
			{
				this.ResizePropertyList();
				LocalSettingsProvider.Instance.SaveAsync().Wait();
				idCaches = VirtualDesktop.GetDesktops().Select(d => d.Id).ToArray();
			};
			VirtualDesktop.Destroyed += (sender, args) =>
			{
				var destroyedIndex = Array.IndexOf(idCaches, args.Destroyed.Id);
				if (destroyedIndex < 0) return;
				var positionSettings = Settings.General.DesktopBackgroundPositions;
				for (var i = destroyedIndex; i + 1 < positionSettings.Count; ++i)
				{
					positionSettings.Value[i].Value = positionSettings.Value[i + 1].Value;
				}
				this.ResizePropertyList();
				LocalSettingsProvider.Instance.SaveAsync().Wait();
				idCaches = VirtualDesktop.GetDesktops().Select(d => d.Id).ToArray();
			};

			if (!ProductInfo.IsWindows11OrLater) return;

			VirtualDesktop.Moved += (sender, args) =>
			{
				this.ApplyDesktopSettingsToPropertyList();
				Settings.General.DesktopBackgroundPositions.Swap(args.OldIndex, args.NewIndex);
				LocalSettingsProvider.Instance.SaveAsync().Wait();
				idCaches = VirtualDesktop.GetDesktops().Select(d => d.Id).ToArray();
			};
			VirtualDesktop.Renamed += (sender, args) =>
			{
				var desktop = args.Source;
				var index = desktop.Index;
				var names = Settings.General.DesktopNames.Value;
				if (index >= names.Count) this.ResizePropertyList();
				var targetName = names[index];
				targetName.Value = args.NewName;
				LocalSettingsProvider.Instance.SaveAsync().Wait();
			};
			VirtualDesktop.WallpaperChanged += (sender, args) =>
			{
				var desktop = args.Source;
				var index = desktop.Index;
				var paths = Settings.General.DesktopBackgroundImagePaths.Value;
				if (index >= paths.Count) this.ResizePropertyList();
				var targetPath = paths[index];
				targetPath.Value = args.NewPath;
				LocalSettingsProvider.Instance.SaveAsync().Wait();
			};
		}

		private void RegisterActions(ShortcutKeySettings settings, ActionRegister register)
		{
			register(() => settings.MoveLeft.ToShortcutKey(), hWnd => hWnd.MoveToLeft())
				.AddTo(this._disposable);

			register(() => settings.MoveLeftAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToLeft()?.Switch())
				.AddTo(this._disposable);

			register(() => settings.MoveRight.ToShortcutKey(), hWnd => hWnd.MoveToRight())
				.AddTo(this._disposable);

			register(() => settings.MoveRightAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToRight()?.Switch())
				.AddTo(this._disposable);

			register(() => settings.MoveNew.ToShortcutKey(), hWnd => hWnd.MoveToNew())
				.AddTo(this._disposable);

			register(() => settings.MoveNewAndSwitch.ToShortcutKey(), hWnd => hWnd.MoveToNew()?.Switch())
				.AddTo(this._disposable);

			var isKeyboardSettings = settings as MouseShortcutSettings == null;
			if (isKeyboardSettings)
			{
				if (Settings.General.OverrideWindowsDefaultKeyCombination)
				{
					register(() => settings.SwitchToLeftWithDefault.ToShortcutKey(), _ => { })
						.AddTo(this._disposable);

					register(() => settings.SwitchToRightWithDefault.ToShortcutKey(), _ => { })
						.AddTo(this._disposable);
				}
				else if (Settings.General.LoopDesktop)
				{
					register(
							() => settings.SwitchToLeftWithDefault.ToShortcutKey(),
							_ => VirtualDesktopService.GetLeft()?.Switch())
						.AddTo(this._disposable);

					register(
							() => settings.SwitchToRightWithDefault.ToShortcutKey(),
							_ => VirtualDesktopService.GetRight()?.Switch())
						.AddTo(this._disposable);
				}

				register(() => settings.SwitchToLeft.ToShortcutKey(), _ => VirtualDesktopService.GetLeft()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToRight.ToShortcutKey(), _ => VirtualDesktopService.GetRight()?.Switch())
					.AddTo(this._disposable);
			}
			else
			{
				register(() => settings.SwitchToLeft.ToShortcutKey(), _ => VirtualDesktopService.GetLeft()?.Switch())
					.AddTo(this._disposable);

				register(() => settings.SwitchToRight.ToShortcutKey(), _ => VirtualDesktopService.GetRight()?.Switch())
					.AddTo(this._disposable);
			}

			register(() => settings.CloseAndSwitchLeft.ToShortcutKey(), _ => VirtualDesktopService.CloseAndSwitchLeft())
				.AddTo(this._disposable);

			register(() => settings.CloseAndSwitchRight.ToShortcutKey(), _ => VirtualDesktopService.CloseAndSwitchRight())
				.AddTo(this._disposable);

			register(() => settings.ShowTaskView.ToShortcutKey(), _ => VirtualDesktopService.ShowTaskView())
				.AddTo(this._disposable);

			register(() => settings.ShowWindowSwitch.ToShortcutKey(), _ => VirtualDesktopService.ShowWindowSwitch())
				.AddTo(this._disposable);

			register(() => settings.Pin.ToShortcutKey(), hWnd => hWnd.Pin())
				.AddTo(this._disposable);

			register(() => settings.Unpin.ToShortcutKey(), hWnd => hWnd.Unpin())
				.AddTo(this._disposable);

			register(() => settings.TogglePin.ToShortcutKey(), hWnd => hWnd.TogglePin())
				.AddTo(this._disposable);

			register(() => settings.PinApp.ToShortcutKey(), hWnd => hWnd.PinApp())
				.AddTo(this._disposable);

			register(() => settings.UnpinApp.ToShortcutKey(), hWnd => hWnd.UnpinApp())
				.AddTo(this._disposable);

			register(() => settings.TogglePinApp.ToShortcutKey(), hWnd => hWnd.TogglePinApp())
				.AddTo(this._disposable);

			var desktopCount = VirtualDesktopService.Count;
			var switchToIndices = settings.SwitchToIndices.Value;
			for (var index = 0; index < desktopCount && index < switchToIndices.Count; ++index)
			{
				RegisterSpecifiedDesktopSwitching(index, switchToIndices[index].ToShortcutKey());
			}

			var moveToIndices = settings.MoveToIndices.Value;
			for (var index = 0; index < desktopCount && index < moveToIndices.Count; ++index)
			{
				RegisterMovingToSpecifiedDesktop(index, moveToIndices[index].ToShortcutKey());
			}

			void RegisterSpecifiedDesktopSwitching(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, _ => VirtualDesktopService.GetByIndex(i)?.Switch())
					.AddTo(this._disposable);
			};

			void RegisterMovingToSpecifiedDesktop(int i, ShortcutKey shortcut)
			{
				register(() => shortcut, hWnd => hWnd.MoveToIndex(i))
					.AddTo(this._disposable);
			};
		}

		private void ResizePropertyList()
		{
			var desktopCount = VirtualDesktopService.Count;
			Settings.General.DesktopNames.Resize(desktopCount);
			Settings.General.DesktopBackgroundImagePaths.Resize(desktopCount);
			Settings.General.DesktopBackgroundPositions.Resize(desktopCount);
			Settings.ShortcutKey.SwitchToIndices.Resize(desktopCount);
			Settings.ShortcutKey.MoveToIndices.Resize(desktopCount);
			Settings.MouseShortcut.SwitchToIndices.Resize(desktopCount);
			Settings.MouseShortcut.MoveToIndices.Resize(desktopCount);
		}

		private void ApplyDesktopSettingsToPropertyList()
		{
			// Only for Window 11
			var desktops = VirtualDesktop.GetDesktops();
			Array.ForEach(Settings.General.DesktopNames.Value.ToArray(), prop => prop.Value = desktops[prop.Index].Name);
			Array.ForEach(Settings.General.DesktopBackgroundImagePaths.Value.ToArray(), prop => prop.Value = desktops[prop.Index].WallpaperPath);
		}

		private void UpdateDesktopSettingsByPropertyList()
		{
			// Only for Window 11
			var desktops = VirtualDesktop.GetDesktops();
			var desktopNames = Settings.General.DesktopNames.Value.Select(prop => prop.Value).ToArray();
			var wallpaperPaths = Settings.General.DesktopBackgroundImagePaths.Value.Select(prop => prop.Value).ToArray();
			for (int i = 0; i < desktopNames.Length; ++i)
			{
				desktops[i].Name = desktopNames[i];
			}
			for (int i = 0; i < wallpaperPaths.Length; ++i)
			{
				desktops[i].WallpaperPath = wallpaperPaths[i];
			}
		}

		private void FitDesktopsWithPropertyList()
		{
			var generalSettings = Settings.General;
			var nameCount = generalSettings.DesktopNames.Count;
			var wallpaperCount = generalSettings.DesktopBackgroundImagePaths.Count;
			var settingsCount = nameCount >= wallpaperCount ? nameCount : wallpaperCount;
			if (nameCount < settingsCount)
			{
				generalSettings.DesktopNames.Resize(settingsCount);
			}
			else if (wallpaperCount < settingsCount)
			{
				generalSettings.DesktopBackgroundImagePaths.Resize(settingsCount);
			}
			var desktops = VirtualDesktop.GetDesktops();
			var currentCount = desktops.Length;
			if (settingsCount > currentCount)
			{
				for (var i = currentCount; i < settingsCount; ++i)
				{
					VirtualDesktop.Create();
				}
			}
			else if (settingsCount < currentCount)
			{
				for (var i = settingsCount; i < currentCount; ++i)
				{
					desktops[i].Remove();
				}
			}
		}
	}
}

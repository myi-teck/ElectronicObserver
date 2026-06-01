using BrowserLib;
using ElectronicObserver.Data;
using ElectronicObserver.Data.Battle;
using ElectronicObserver.Observer;
using ElectronicObserver.Resource;
using ElectronicObserver.Utility.Mathematics;
using mshtml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace ElectronicObserver.Window
{

	/// <summary>
	/// ブラウザのホスト側フォーム
	/// </summary>
	[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
	public partial class FormBrowserHost : DockContent, IBrowserHost
	{


		public static readonly string BrowserExeName = "EOBrowser.exe";


		/// <summary>
		/// FormBrowserHostの通信サーバ
		/// </summary>
		private string ServerUri = "net.pipe://localhost/" + Process.GetCurrentProcess().Id + "/ElectronicObserver";

		/// <summary>
		/// FormBrowserとの通信インターフェース
		/// </summary>
		private PipeCommunicator<IBrowser> Browser;

		private Process BrowserProcess;

		private IntPtr BrowserWnd = IntPtr.Zero;

		/// <summary>
		/// モバイル遠隔操作画面へ定期的にHUDデータを同期するためのタイマー
		/// </summary>
		private Timer _hudTimer;



		[Flags]
		private enum InitializationStageFlag
		{
			InitialAPILoaded = 1,
			BrowserConnected = 2,
			SetProxyCompleted = 4,
			Completed = 7,
		}

		/// <summary>
		/// 初期化ステージカウント
		/// 完了したらログインページを開く (各処理が終わらないと正常にロードできないため)
		/// </summary>
		private InitializationStageFlag _initializationStage = 0;
		private InitializationStageFlag InitializationStage
		{
			get { return _initializationStage; }
			set
			{
				//AddLog( 1, _initializationStage + " -> " + value );
				if (_initializationStage != InitializationStageFlag.Completed && value == InitializationStageFlag.Completed)
				{
					if (Utility.Configuration.Config.FormBrowser.IsEnabled)
					{
						NavigateToLogInPage();
					}
				}

				_initializationStage = value;
			}
		}



		public FormBrowserHost(FormMain parent)
		{
			InitializeComponent();

			Icon = ResourceManager.ImageToIcon(ResourceManager.Instance.Icons.Images[(int)ResourceManager.IconContent.FormBrowser]);
		}

		public void InitializeApiCompleted()
		{
			InitializationStage |= InitializationStageFlag.InitialAPILoaded;
		}

		private void FormBrowser_Load(object sender, EventArgs e)
		{
			LaunchBrowserProcess();
		}


		private void LaunchBrowserProcess()
		{
			// 通信サーバ起動
			Browser = new PipeCommunicator<IBrowser>(
				this, typeof(IBrowserHost), ServerUri, "BrowserHost");

			try
			{
				// プロセス起動

				if (System.IO.File.Exists(BrowserExeName))
					BrowserProcess = Process.Start(BrowserExeName, ServerUri);

				else    //デバッグ環境用 作業フォルダにかかわらず自分と同じフォルダのを参照する
					BrowserProcess = Process.Start(
						System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\" + BrowserExeName,
						ServerUri);

				// 残りはサーバに接続してきたブラウザプロセスがドライブする

			}
			catch (Exception ex)
			{
				Utility.ErrorReporter.SendErrorReport(ex, "ブラウザプロセスの起動に失敗しました。");
				MessageBox.Show("ブラウザプロセスの起動に失敗しました。\r\n" + ex.Message,
					"エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		internal void ConfigurationChanged()
		{
			Font = Utility.Configuration.Config.UI.MainFont;
			Browser.AsyncRemoteRun(() => Browser.Proxy.ConfigurationChanged(Configuration));
		}


		//ロード直後の適用ではレイアウトがなぜか崩れるのでこのタイミングでも適用
		void InitialAPIReceived(string apiname, dynamic data)
		{
			if (InitializationStage != InitializationStageFlag.Completed)       // 初期化が終わってから
				return;

			Browser.AsyncRemoteRun(() => Browser.Proxy.InitialAPIReceived());
		}


		/// <summary>
		/// 指定した URL のページを開きます。
		/// </summary>
		public void Navigate(string url)
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.Navigate(url));
		}

		/// <summary>
		/// 艦これのログインページを開きます。
		/// </summary>
		public void NavigateToLogInPage()
		{
			Navigate(Utility.Configuration.Config.FormBrowser.LogInPageURL);
		}

		/// <summary>
		/// ブラウザを再読み込みします。
		/// </summary>
		public void RefreshBrowser()
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.RefreshBrowser());
		}

		/// <summary>
		/// ズームを適用します。
		/// </summary>
		public void ApplyZoom()
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.ApplyZoom());
		}

		/// <summary>
		/// スタイルシートを適用します。
		/// </summary>
		public void ApplyStyleSheet()
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.ApplyStyleSheet());
		}

		/// <summary>
		/// DMMによるページ更新ダイアログを非表示にします。
		/// </summary>
		public void DestroyDMMreloadDialog()
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.DestroyDMMreloadDialog());
		}


		/// <summary>
		/// スクリーンショットを保存します。
		/// </summary>
		public void SaveScreenShot()
		{
			Browser.AsyncRemoteRun(() => Browser.Proxy.SaveScreenShot());
		}


		public void SendErrorReport(string exceptionName, string message)
		{
			Utility.ErrorReporter.SendErrorReport(new Exception(exceptionName), message);
		}

		public void AddLog(int priority, string message)
		{
			Utility.Logger.Add(priority, message);
		}


		public BrowserLib.BrowserConfiguration Configuration
		{
			get
			{
				BrowserLib.BrowserConfiguration config = new BrowserLib.BrowserConfiguration();
				var c = Utility.Configuration.Config.FormBrowser;

				config.ZoomRate = c.ZoomRate;
				config.ZoomFit = c.ZoomFit;
				config.LogInPageURL = c.LogInPageURL;
				config.IsEnabled = c.IsEnabled;
				config.ScreenShotPath = c.ScreenShotPath;
				config.ScreenShotFormat = c.ScreenShotFormat;
				config.ScreenShotSaveMode = c.ScreenShotSaveMode;
				config.StyleSheet = c.StyleSheet;
				config.IsScrollable = c.IsScrollable;
				config.AppliesStyleSheet = c.AppliesStyleSheet;
				config.AppliesColorFilter = c.AppliesColorFilter;
				config.ColorFilterMode = c.ColorFilterMode;
				config.AppliesHoverCover = c.AppliesHoverCover;
				config.IsDMMreloadDialogDestroyable = c.IsDMMreloadDialogDestroyable;
				config.AvoidTwitterDeterioration = c.AvoidTwitterDeterioration;
				config.ToolMenuDockStyle = (int)c.ToolMenuDockStyle;
				config.IsToolMenuVisible = c.IsToolMenuVisible;
				config.ConfirmAtRefresh = c.ConfirmAtRefresh;
				config.HardwareAccelerationEnabled = c.HardwareAccelerationEnabled;
				config.PreserveDrawingBuffer = c.PreserveDrawingBuffer;
				config.ForceColorProfile = c.ForceColorProfile;
				config.SavesBrowserLog = c.SavesBrowserLog;
				config.EnableDebugMenu = Utility.Configuration.Config.Debug.EnableDebugMenu;
				return config;
			}
		}

		public void ConfigurationUpdated(BrowserLib.BrowserConfiguration config)
		{

			var c = Utility.Configuration.Config.FormBrowser;

			c.ZoomRate = config.ZoomRate;
			c.ZoomFit = config.ZoomFit;
			c.LogInPageURL = config.LogInPageURL;
			c.IsEnabled = config.IsEnabled;
			c.ScreenShotPath = config.ScreenShotPath;
			c.ScreenShotFormat = config.ScreenShotFormat;
			c.ScreenShotSaveMode = config.ScreenShotSaveMode;
			c.StyleSheet = config.StyleSheet;
			c.IsScrollable = config.IsScrollable;
			c.AppliesStyleSheet = config.AppliesStyleSheet;
			c.AppliesColorFilter = config.AppliesColorFilter;
			c.ColorFilterMode = config.ColorFilterMode;
			c.AppliesHoverCover = config.AppliesHoverCover;
			c.IsDMMreloadDialogDestroyable = config.IsDMMreloadDialogDestroyable;
			c.AvoidTwitterDeterioration = config.AvoidTwitterDeterioration;
			c.ToolMenuDockStyle = (DockStyle)config.ToolMenuDockStyle;
			c.IsToolMenuVisible = config.IsToolMenuVisible;
			c.ConfirmAtRefresh = config.ConfirmAtRefresh;
			c.HardwareAccelerationEnabled = config.HardwareAccelerationEnabled;
			c.PreserveDrawingBuffer = config.PreserveDrawingBuffer;
			c.ForceColorProfile = config.ForceColorProfile;
			c.SavesBrowserLog = config.SavesBrowserLog;
			//Utility.Configuration.Config.Debug.EnableDebugMenu = config.EnableDebugMenu;

			// volume
			if (Utility.Configuration.Config.BGMPlayer.SyncBrowserMute)
			{
				Utility.SyncBGMPlayer.Instance.IsMute = config.IsMute;
			}
		}

		public void GetIconResource()
		{

			string[] keys = new string[] {
				"Browser_ScreenShot",
				"Browser_Zoom",
				"Browser_ZoomIn",
				"Browser_ZoomOut",
				"Browser_Unmute",
				"Browser_Mute",
				"Browser_Refresh",
				"Browser_Navigate",
				"Browser_Other",
			};
			int unitsize = 16 * 16 * 4;

			byte[] canvas = new byte[unitsize * keys.Length];

			for (int i = 0; i < keys.Length; i++)
			{
				Image img = ResourceManager.Instance.Icons.Images[keys[i]];
				if (img != null)
				{
					using (Bitmap bmp = new Bitmap(img))
					{

						BitmapData bmpdata = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
						Marshal.Copy(bmpdata.Scan0, canvas, unitsize * i, unitsize);
						bmp.UnlockBits(bmpdata);

					}
				}
			}

			Browser.AsyncRemoteRun(() => Browser.Proxy.SetIconResource(canvas));

		}


		public void RequestNavigation(string baseurl)
		{

			using (var dialog = new Window.Dialog.DialogTextInput("移動先の入力", "移動先の URL を入力してください。"))
			{
				dialog.InputtedText = baseurl;

				if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
				{

					Navigate(dialog.InputtedText);
				}
			}

		}


		public async void ClearCache()
		{
			Utility.Logger.Add(2, "キャッシュの削除を開始するため、ブラウザを終了しています…");

			try
			{
				if (!Browser?.Closed ?? false)
				{
					Browser.Proxy?.CloseBrowser();

					await Browser.CloseAsync(this);

					TerminateBrowserProcess();
				}
			}
			catch (Exception) { }


			await ClearCacheAsync().ContinueWith(task =>
			{
				Utility.Logger.Add(2, "キャッシュの削除処理が終了しました。ブラウザを再起動しています…");

				_initializationStage = InitializationStageFlag.InitialAPILoaded;
				try
				{
					LaunchBrowserProcess();
				}
				catch (Exception ex)
				{
					Utility.ErrorReporter.SendErrorReport(ex, "ブラウザの再起動に失敗しました。");
					MessageBox.Show("ブラウザプロセスの再起動に失敗しました。\r\n申し訳ありませんが本ツールを一旦終了してください。", ":(", MessageBoxButtons.OK, MessageBoxIcon.Error);
				}


			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		private async Task ClearCacheAsync()
		{
			int trial;
			Exception lastException = null;
			var dir = new DirectoryInfo("BrowserCache");

			for (trial = 0; trial < 4; trial++)
			{
				try
				{
					dir.Refresh();

					if (dir.Exists)
						dir.Delete(true);
					else
						break;

					for (int i = 0; i < 10; i++)
					{
						dir.Refresh();
						if (dir.Exists)
						{
							await Task.Delay(50);
						}
						else break;
					}
					if (!dir.Exists)
					{
						break;
					}
				}
				catch (Exception ex)
				{
					lastException = ex;
					await Task.Delay(500);
				}
			}
			if (trial == 4)
			{
				Utility.ErrorReporter.SendErrorReport(lastException, "キャッシュの削除に失敗しました。");
			}
		}


		public void ConnectToBrowser(IntPtr hwnd)
		{
			BrowserWnd = hwnd;

			// 子ウィンドウに設定
			SetParent(BrowserWnd, this.Handle);
			MoveWindow(BrowserWnd, 0, 0, this.Width, this.Height, true);

			//キー入力をブラウザに投げる
			Application.AddMessageFilter(new KeyMessageGrabber(BrowserWnd));

			// デッドロックするので非同期で処理
			BeginInvoke((Action)(() =>
			{
				// ブラウザプロセスに接続
				Browser.Connect(ServerUri + "Browser/Browser");
				Browser.Faulted += Browser_Faulted;

				ConfigurationChanged();

				Utility.Configuration.Instance.ConfigurationChanged += ConfigurationChanged;

				APIObserver.Instance.APIList["api_start2/getData"].ResponseReceived +=
					(string apiname, dynamic data) => InitialAPIReceived(apiname, data);

				// プロキシをセット
				Browser.AsyncRemoteRun(() => Browser.Proxy.SetProxy(BuildDownstreamProxy()));
				APIObserver.Instance.ProxyStarted += () =>
				{
					Browser.AsyncRemoteRun(() => Browser.Proxy.SetProxy(BuildDownstreamProxy()));
				};

				InitializationStage |= InitializationStageFlag.BrowserConnected;

				// 遠隔HUD同期タイマーの初期化と起動 (2秒毎にゲームデータを送信)
				_hudTimer = new Timer();
				_hudTimer.Interval = 2000;
				_hudTimer.Tick += (s, ev) => SyncHudData();
				_hudTimer.Start();

				// ログ追加イベントドリブン更新（ログが追加された際に即座にHUDデータを再送信）
				Utility.Logger.Instance.LogAdded += (Utility.Logger.LogData data) =>
				{
					if (data.Priority >= 1 && Browser != null && !Browser.Closed && Browser.Proxy != null)
					{
						// 定期更新と同じ SyncHudData を利用（クライアント側で差分処理を行う）
						// Invoke しないのは、GUIスレッドの処理順序を乱さないため
						Task.Run(() => SyncHudData());
					}
				};

			}));
		}

		private string BuildDownstreamProxy()
		{
			var config = Utility.Configuration.Config.Connection;

			if (!string.IsNullOrEmpty(config.DownstreamProxy))
			{
				return config.DownstreamProxy;

			}
			if (config.UseSystemProxy)
			{
				return APIObserver.Instance.ProxyPort.ToString();

			}
			return string.Format("http=127.0.0.1:{0};https=127.0.0.1:{0}", APIObserver.Instance.ProxyPort);
		}

	


		public void SetProxyCompleted()
		{
			InitializationStage |= InitializationStageFlag.SetProxyCompleted;
		}


		void Browser_Faulted(Exception e)
		{
			if (Browser.Proxy == null)
			{
				Utility.ErrorReporter.SendErrorReport(e, "ブラウザプロセスが予期せず終了しました。");
			}
			else
			{
				Utility.ErrorReporter.SendErrorReport(e, "ブラウザプロセス間で通信エラーが発生しました。");
			}
		}


		private void TerminateBrowserProcess()
		{
			if (!BrowserProcess.WaitForExit(2000))
			{
				try
				{
					// 2秒待って終了しなかったらKill
					BrowserProcess.Kill();
				}
				catch (Exception)
				{
					// プロセスが既に終了してた場合などに例外が出る
				}
			}
			BrowserWnd = IntPtr.Zero;
		}

		public void CloseBrowser()
		{
			// 同期タイマーを安全に停止
			_hudTimer?.Stop();
			_hudTimer?.Dispose();
			_hudTimer = null;

			try
			{

				if (Browser == null)
				{
					// ブラウザを開いていない場合はnullなので
					return;
				}
				if (!Browser.Closed)
				{
					// ブラウザプロセスが異常終了した場合などはnullになる
					if (Browser.Proxy != null)
					{
						Browser.Proxy.CloseBrowser();
					}
					Browser.Close();
					TerminateBrowserProcess();
				}

			}
			catch (Exception ex)
			{       //ブラウザプロセスが既に終了していた場合など

				Utility.ErrorReporter.SendErrorReport(ex, "ブラウザの終了中にエラーが発生しました。");
			}

		}

		private void FormBrowserHost_Resize(object sender, EventArgs e)
		{
			if (BrowserWnd != IntPtr.Zero)
			{
				MoveWindow(BrowserWnd, 0, 0, this.Width, this.Height, true);
			}
		}

		private void FormBrowserHost_Paint(object sender, PaintEventArgs e)
		{
			if (BrowserProcess?.HasExited ?? false)
			{
				var image = ResourceManager.Instance.Icons.Images[(int)ResourceManager.IconContent.ConditionVeryTired];
				e.Graphics.DrawImage(image, new Rectangle(16, 16, 16, 16));

				e.Graphics.DrawString("ブラウザが起動していません。\r\nクリックすると起動します。", Utility.Configuration.Config.UI.MainFont, Brushes.Black, new PointF(48, 16));
			}
		}


		private void FormBrowserHost_Click(object sender, EventArgs e)
		{
			Refresh();

			if (InitializationStage == InitializationStageFlag.Completed && (BrowserProcess?.HasExited ?? false))
			{
				if (!Browser?.Closed ?? false)
				{
					Browser.Close();
				}

				InitializationStage = InitializationStageFlag.InitialAPILoaded;
				LaunchBrowserProcess();
			}
		}



		/// <summary>
		/// ハートビート用
		/// </summary>
		public IntPtr HWND => this.Handle;

		protected override string GetPersistString()
		{
			return "Browser";
		}


		#region 呪文

		[DllImport("user32.dll", SetLastError = true)]
		private static extern uint SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int cx, int cy, bool repaint);


		#endregion

		/// <summary>
		/// 2秒毎にゲーム内データをJSON化し、WCFを介してブラウザ（サブプロセス）に送信します。
		/// </summary>
		private void SyncHudData()
		{
			if (Browser == null || Browser.Closed || Browser.Proxy == null)
				return;

			try
			{
				var db = KCDatabase.Instance;
				var sb = new StringBuilder();
				sb.Append("{");

				// ----------------------------------------------------------------------------
				// 資源データのJSON化処理
				// ----------------------------------------------------------------------------
				sb.Append("\"material\":{");
				if (db.Material != null && db.Material.IsAvailable)
				{
					sb.Append($"\"fuel\":{db.Material.Fuel},\"ammo\":{db.Material.Ammo},\"steel\":{db.Material.Steel},\"bauxite\":{db.Material.Bauxite},\"bucket\":{db.Material.InstantRepair}");
				}
				else
				{
					sb.Append("\"fuel\":0,\"ammo\":0,\"steel\":0,\"bauxite\":0,\"bucket\":0");
				}
				sb.Append("},");

				// ----------------------------------------------------------------------------
				// 艦隊データのJSON化処理
				// ----------------------------------------------------------------------------
				sb.Append("\"fleets\":[");
				if (db.Fleet != null && db.Fleet.IsAvailable)
				{
					var fleetList = new List<string>();
					foreach (var fleet in db.Fleet.Fleets.Values)
					{
						if (fleet == null) continue;

						string state = "ACTIVE";
						string stateText = "ACTIVE / 出撃可能";
						string remaining = "";

						// 1. 遠征中のチェック
						if (fleet.ExpeditionState == 1)
						{
							state = "MISSION";
							var timeLeft = fleet.ExpeditionTime - DateTime.Now;
							if (timeLeft.TotalSeconds > 0)
							{
								remaining = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)timeLeft.TotalHours, timeLeft.Minutes, timeLeft.Seconds);
								stateText = $"MISSION [{remaining}]";
							}
							else stateText = "MISSION [帰還完了]";
						}
						// 2. 入渠中のチェック（艦隊内に入渠中の艦艇がいる場合）
						else if (fleet.MembersInstance.Any(ship => ship != null && ship.RepairingDockID >= 0))
						{
							state = "DOCKING";
							// 入渠中の艦艇の完了時間を取得（最も遅いものを表示）
							var dockingShips = fleet.MembersInstance.Where(ship => ship != null && ship.RepairingDockID >= 0).ToList();
							var maxCompletionTime = dockingShips.Max(ship =>
							{
								var dock = db.Docks.Values.FirstOrDefault(d => d.DockID == ship.RepairingDockID);
								return dock != null ? dock.CompletionTime : DateTime.MinValue;
							});
							var timeLeft = maxCompletionTime - DateTime.Now;
							if (timeLeft.TotalSeconds > 0)
							{
								remaining = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)timeLeft.TotalHours, timeLeft.Minutes, timeLeft.Seconds);
								stateText = $"DOCKING [{remaining}]";
							}
							else stateText = "DOCKING [完了]";
						}
						// 3. 大破警告・疲労警告のチェック
						else
						{
							bool hasCritical = false;
							bool hasWarning = false;
							int minCond = 100;
							foreach (var ship in fleet.MembersInstance)
							{
								if (ship == null) continue;
								// 入渠中の艦艇はHPが25%以下でも大破警告の対象から除外する（本家の FleetState.cs と同じ仕様）
								if (ship.HPCurrent * 4 <= ship.HPMax && ship.RepairingDockID == -1) hasCritical = true;
								if (ship.Condition < 49) { hasWarning = true; if (ship.Condition < minCond) minCond = ship.Condition; }
							}

							if (hasCritical) { state = "EMERGENCY"; stateText = "EMERGENCY / 大破警告"; }
							else if (hasWarning) { state = "STANDBY"; stateText = $"STANDBY [回復 {(int)Math.Ceiling((49 - minCond) / 3.0) * 3}分]"; }
						}
						// 艦隊内の各艦船データを ships 配列として追加
						var shipList = new List<string>();
						foreach (var ship in fleet.MembersInstance)
						{
							if (ship == null)
							{
								// 空スロットは null で表現
								shipList.Add("null");
								continue;
							}
							shipList.Add(BuildShipJson(ship, fleet.FleetID));
						}
						string shipsJson = string.Join(",", shipList);
						fleetList.Add($"{{\"id\":{fleet.FleetID},\"name\":\"{EscapeJson(fleet.Name)}\",\"state\":\"{state}\",\"stateText\":\"{EscapeJson(stateText)}\",\"expeditionRemaining\":\"{remaining}\",\"ships\":[{shipsJson}]}}");
					}
					sb.Append(string.Join(",", fleetList));
				}
				sb.Append("],");

				// ----------------------------------------------------------------------------
				// 入渠ドックデータのJSON化処理
				// ----------------------------------------------------------------------------
				sb.Append("\"docks\":[");
				var dockList = new List<string>();
				if (db.Docks != null && db.Docks.Count > 0)
				{
					foreach (var dock in db.Docks.Values)
					{
						if (dock == null) continue;
						dockList.Add("{" +
							$"\"dockId\":{dock.DockID}," +
							$"\"state\":{dock.State}," +
							$"\"shipId\":{dock.ShipID}," +
							$"\"completionTime\":\"{dock.CompletionTime:yyyy-MM-ddTHH:mm:ss}\"" +
							"}");
					}
				}
				sb.Append(string.Join(",", dockList));
				sb.Append("],");

				// ----------------------------------------------------------------------------
				// 作戦海域・戦術予測HUDのJSON化処理
				// ----------------------------------------------------------------------------
				sb.Append("\"battle\":{");
				var battle = db.Battle;
				if (battle != null && battle.FirstBattle != null)
				{
					string area = "出撃中";
					var compass = db.Battle?.Compass;
					if (compass != null)
					{
						area = $"{compass.MapAreaID}-{compass.MapInfoID} [CELL {compass.Destination}]";
					}

					string enemy = "深海棲艦";
					if (battle.FirstBattle.Initial != null && battle.FirstBattle.Initial.EnemyMembersInstance != null)
					{
						var enemyLeader = battle.FirstBattle.Initial.EnemyMembersInstance.FirstOrDefault();
						if (enemyLeader != null)
						{
							enemy = enemyLeader.NameWithClass;
						}
					}

					string predict = "BATTLE IN PROGRESS";
					sb.Append($"\"area\":\"{EscapeJson(area)}\",");
					sb.Append($"\"enemy\":\"{EscapeJson(enemy)}\",");
					sb.Append($"\"predict\":\"{EscapeJson(predict)}\"");
				}
				else
				{
					sb.Append("\"area\":\"-\",");
					sb.Append("\"enemy\":\"-\",");
					sb.Append("\"predict\":\"-\"");
				}
				sb.Append("},");

				// ----------------------------------------------------------------------------
				// 詳細戦闘情報HUDのJSON化処理（連合艦隊・7隻編成/遊撃部隊対応）
				// ----------------------------------------------------------------------------
				sb.Append("\"combat\":{");
					string formation = "味方: - vs 敵: -";
					string engagement = "-";
					string touch = "味方: - / 敵: -";
					
					// battle が null または FirstBattle が null の場合はデフォルト値を使用
					if (battle != null && battle.FirstBattle != null)
					{
						var first = battle.FirstBattle;
						if (first.Searching != null)
						{
							formation = $"{Constants.GetFormationShort(first.Searching.FormationFriend)} vs {Constants.GetFormationShort(first.Searching.FormationEnemy)}";
							engagement = Constants.GetEngagementForm(first.Searching.EngagementForm);
						}
					}

					sb.Append($"\"formation\":\"{EscapeJson(formation)}\",");
					sb.Append($"\"engagement\":\"{EscapeJson(engagement)}\",");

					// アクティブな戦闘データが存在する場合のみ、詳細なHP及び艦娘情報を抽出
					var activeBattle = (battle != null) ? (battle.SecondBattle ?? battle.FirstBattle) : null;
					if (activeBattle != null && activeBattle.Initial != null && activeBattle.ResultHPs != null)
					{
						var initial = (battle?.FirstBattle?.Initial ?? activeBattle.Initial); // 開始時初期値は最初の戦闘のものを使用（fallback あり）
						var resultHPs = activeBattle.ResultHPs; // 結果HPは現在アクティブな戦闘結果を使用
						var friendInitHPs = initial?.FriendInitialHPs;
						var friendMaxHPs = initial?.FriendMaxHPs;
						
						// 航空戦フェーズが存在する場合は触接状態を抽出
						var airBattle = activeBattle as ElectronicObserver.Data.Battle.BattleDay;
						touch = $"味方: {(airBattle?.AirBattle?.IsAvailable ?? false ? Constants.GetSearchingResultShort(airBattle.AirBattle.TouchAircraftFriend) : "-")} / 敵: {(airBattle?.AirBattle?.IsAvailable ?? false ? Constants.GetSearchingResultShort(airBattle.AirBattle.TouchAircraftEnemy) : "-")}";
						sb.Append($"\"touch\":\"{EscapeJson(touch)}\",");
		
						// --- 損害率・勝利区分・航空戦結果の抽出 ---
						
						// 損害率（自軍HP減少率）
						double? friendDamageRate = null;
						if (initial != null && resultHPs != null && friendInitHPs != null && friendMaxHPs != null)
						{
							int totalInitialHP = 0, totalCurrentHP = 0;
							for (int i = 0; i < friendInitHPs.Length; i++)
							{
								if (friendInitHPs[i] == -1) continue;
								int refindex = BattleIndex.Get(BattleSides.FriendMain, i);
								if (refindex >= resultHPs.Count) continue;
								totalInitialHP += friendInitHPs[i];
								totalCurrentHP += Math.Max(0, resultHPs[refindex]);
							}
							// 連合艦隊の随伴艦隊分も加算
							if (initial.IsFriendCombined && initial.FriendInitialHPsEscort != null)
							{
								for (int i = 0; i < initial.FriendInitialHPsEscort.Length; i++)
								{
									if (initial.FriendInitialHPsEscort[i] == -1) continue;
									int refindex = BattleIndex.Get(BattleSides.FriendEscort, i);
									if (refindex >= resultHPs.Count) continue;
									totalInitialHP += initial.FriendInitialHPsEscort[i];
									totalCurrentHP += Math.Max(0, resultHPs[refindex]);
								}
							}
							if (totalInitialHP > 0)
							{
								friendDamageRate = Math.Round((1.0 - (double)totalCurrentHP / totalInitialHP) * 100, 1);
							}
						}
						sb.Append($"\"damageRate\":{(friendDamageRate.HasValue ? friendDamageRate.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "null")},");
		
						// 勝利区分（戦闘結果が確定している場合は実値、それ以外は予測値）
						string victory = "-";
						if (battle.Result != null && !string.IsNullOrEmpty(battle.Result.Rank))
						{
							// 戦闘結果APIが受信済み：実際の勝利ランク文字列（"SS", "S", "A", "B", "C", "T-L", "B-L"）を使用
							victory = battle.Result.Rank;
						}
						else if (battle.FirstBattle != null)
						{
							// 戦闘中：予測勝利ランクを使用（int -> string 変換）
							int predictedRank = battle.PredictWinRank(out _, out _);
							victory = Constants.GetWinRank(predictedRank);
						}
						sb.Append($"\"victory\":\"{EscapeJson(victory)}\",");
		
						// 航空戦結果（航空戦フェーズが存在する場合に簡潔な文字列を生成）
						string airBattleResult = "-";
						if (airBattle?.AirBattle?.IsAvailable ?? false)
						{
							var ab = airBattle.AirBattle;
							string airSuperiority = Constants.GetAirSuperiority(ab.AirSuperiority);
							int friendLost1 = ab.AircraftLostStage1Friend;
							int friendTotal1 = ab.AircraftTotalStage1Friend;
							int enemyLost1 = ab.AircraftLostStage1Enemy;
							int enemyTotal1 = ab.AircraftTotalStage1Enemy;
							airBattleResult = $"{airSuperiority} 自-{friendLost1}/{friendTotal1} 敵-{enemyLost1}/{enemyTotal1}";
							// 第二次航空戦が存在する場合は追加
							if (ab.IsStage2Available)
							{
								airBattleResult += $" | 第2次 自-{ab.AircraftLostStage2Friend}/{ab.AircraftTotalStage2Friend} 敵-{ab.AircraftLostStage2Enemy}/{ab.AircraftTotalStage2Enemy}";
							}
						}
						sb.Append($"\"airBattle\":\"{EscapeJson(airBattleResult)}\",");
		
						// --- 1. 自軍本隊（最大7隻・遊撃部隊含む）の個別HP情報抽出 ---
						sb.Append("\"friendShips\":[");
						var friendList = new List<string>();
						if (friendInitHPs != null && friendMaxHPs != null)
						{
							for (int i = 0; i < friendInitHPs.Length; i++)
							{
								if (friendInitHPs[i] == -1) continue;
								int refindex = BattleIndex.Get(BattleSides.FriendMain, i);
								if (refindex >= resultHPs.Count) continue;
								int currentHP = resultHPs[refindex];
								int maxHP = friendMaxHPs[i];

								string name = "艦娘";
								if (initial.FriendFleet != null && i < initial.FriendFleet.MembersInstance.Count && initial.FriendFleet.MembersInstance[i] != null)
								{
									name = initial.FriendFleet.MembersInstance[i].Name;
								}

								string status = "OK";
								if (currentHP <= 0) status = "SUNK";
								else if (currentHP * 4 <= maxHP) status = "EMERGENCY";
								else if (currentHP * 2 <= maxHP) status = "WARNING";

								friendList.Add("{" +
									$"\"name\":\"{EscapeJson(name)}\"," +
									$"\"hp\":{Math.Max(0, currentHP)}," +
									$"\"maxHp\":{maxHP}," +
									$"\"status\":\"{status}\"" +
									"}");
							}
						}
						sb.Append(string.Join(",", friendList));
						sb.Append("],");

						// --- 2. 自軍随伴（連合艦隊・最大6隻）の個別HP情報抽出 ---
						sb.Append("\"friendEscortShips\":[");
						var friendEscortList = new List<string>();
						if (initial != null && initial.IsFriendCombined && initial.FriendInitialHPsEscort != null && initial.FriendMaxHPsEscort != null)
						{
							var friendInitHPsEscort = initial.FriendInitialHPsEscort;
							var friendMaxHPsEscort = initial.FriendMaxHPsEscort;
							for (int i = 0; i < friendInitHPsEscort.Length; i++)
							{
								if (friendInitHPsEscort[i] == -1) continue;
								int refindex = BattleIndex.Get(BattleSides.FriendEscort, i);
								if (refindex >= resultHPs.Count) continue;
								int currentHP = resultHPs[refindex];
								int maxHP = friendMaxHPsEscort[i];

								string name = "艦娘(随伴)";
								if (initial.FriendFleetEscort != null && i < initial.FriendFleetEscort.MembersInstance.Count && initial.FriendFleetEscort.MembersInstance[i] != null)
								{
									name = initial.FriendFleetEscort.MembersInstance[i].Name;
								}

								string status = "OK";
								if (currentHP <= 0) status = "SUNK";
								else if (currentHP * 4 <= maxHP) status = "EMERGENCY";
								else if (currentHP * 2 <= maxHP) status = "WARNING";

								friendEscortList.Add("{" +
									$"\"name\":\"{EscapeJson(name)}\"," +
									$"\"hp\":{Math.Max(0, currentHP)}," +
									$"\"maxHp\":{maxHP}," +
									$"\"status\":\"{status}\"" +
									"}");
							}
						}
						sb.Append(string.Join(",", friendEscortList));
						sb.Append("],");

						// --- 3. 敵軍本隊（最大6隻）の個別HP情報抽出 ---
						sb.Append("\"enemyShips\":[");
						var enemyList = new List<string>();
						var enemyInitHPs = initial?.EnemyInitialHPs;
						var enemyMaxHPs = initial?.EnemyMaxHPs;
						if (enemyInitHPs != null && enemyMaxHPs != null)
						{
							for (int i = 0; i < enemyInitHPs.Length; i++)
							{
								if (enemyInitHPs[i] == -1) continue;
								int refindex = BattleIndex.Get(BattleSides.EnemyMain, i);
								if (refindex >= resultHPs.Count) continue;
								int currentHP = resultHPs[refindex];
								int maxHP = enemyMaxHPs[i];

								string name = "敵艦";
								if (initial.EnemyMembersInstance != null && i < initial.EnemyMembersInstance.Length && initial.EnemyMembersInstance[i] != null)
								{
									name = initial.EnemyMembersInstance[i].Name;
								}

								string status = "OK";
								if (currentHP <= 0) status = "SUNK";
								else if (currentHP * 4 <= maxHP) status = "EMERGENCY";
								else if (currentHP * 2 <= maxHP) status = "WARNING";

								enemyList.Add("{" +
									$"\"name\":\"{EscapeJson(name)}\"," +
									$"\"hp\":{Math.Max(0, currentHP)}," +
									$"\"maxHp\":{maxHP}," +
									$"\"status\":\"{status}\"" +
									"}");
							}
						}
						sb.Append(string.Join(",", enemyList));
						sb.Append("],");

						// --- 4. 敵軍随伴（敵連合艦隊・最大6隻）の個別HP情報抽出 ---
						sb.Append("\"enemyEscortShips\":[");
						var enemyEscortList = new List<string>();
						if (initial != null && initial.IsEnemyCombined && initial.EnemyInitialHPsEscort != null && initial.EnemyMaxHPsEscort != null)
						{
							var enemyInitHPsEscort = initial.EnemyInitialHPsEscort;
							var enemyMaxHPsEscort = initial.EnemyMaxHPsEscort;
							for (int i = 0; i < enemyInitHPsEscort.Length; i++)
							{
								if (enemyInitHPsEscort[i] == -1) continue;
								int refindex = BattleIndex.Get(BattleSides.EnemyEscort, i);
								if (refindex >= resultHPs.Count) continue;
								int currentHP = resultHPs[refindex];
								int maxHP = enemyMaxHPsEscort[i];

								string name = "敵随伴艦";
								if (initial.EnemyMembersEscortInstance != null && i < initial.EnemyMembersEscortInstance.Length && initial.EnemyMembersEscortInstance[i] != null)
								{
									name = initial.EnemyMembersEscortInstance[i].Name;
								}

								string status = "OK";
								if (currentHP <= 0) status = "SUNK";
								else if (currentHP * 4 <= maxHP) status = "EMERGENCY";
								else if (currentHP * 2 <= maxHP) status = "WARNING";

								enemyEscortList.Add("{" +
									$"\"name\":\"{EscapeJson(name)}\"," +
									$"\"hp\":{Math.Max(0, currentHP)}," +
									$"\"maxHp\":{maxHP}," +
									$"\"status\":\"{status}\"" +
									"}");
							}
						}
						sb.Append(string.Join(",", enemyEscortList));
						sb.Append("]");
					}
					else
					{
						sb.Append("\"touch\":\"味方: - / 敵: -\",");
						sb.Append("\"damageRate\":null,");
						sb.Append("\"victory\":\"-\",");
						sb.Append("\"airBattle\":\"-\",");
						sb.Append("\"friendShips\":[],");
						sb.Append("\"friendEscortShips\":[],");
						sb.Append("\"enemyShips\":[],");
						sb.Append("\"enemyEscortShips\":[]");
					}
				sb.Append("},");

				// ----------------------------------------------------------------------------
				// 処理ブロック名 : 受領中任務データのJSON化処理
				// 概要           : 現在受領中の任務（Stateが2=受領中または3=達成）をQuestID順にソートし、
				//                  JSONシリアライズしてquests配列として追加します。
				// ----------------------------------------------------------------------------
				sb.Append("\"quests\":[");
				var questList = new List<string>();
				if (db.Quest != null && db.Quest.IsLoaded)
				{
					// Stateが2（進行中）または3（達成）の任務のみをQuestID順で抽出
					var runningQuests = db.Quest.Quests.Values
						.Where(q => q.State == 2 || q.State == 3)
						.OrderBy(q => q.QuestID);
					foreach (var q in runningQuests)
					{
						string progressText = "-";
						double progressRate = 0.0;

						if (q.State == 3)
						{
							progressText = "達成！";
							progressRate = 1.0;
						}
						else if (db.QuestProgress.Progresses.ContainsKey(q.QuestID))
						{
							var p = db.QuestProgress[q.QuestID];
							progressText = p.ToString();
							progressRate = p.ProgressPercentage;
						}
						else
						{
							switch (q.Progress)
							{
								case 1:
									progressText = "50%以上";
									progressRate = 0.5;
									break;
								case 2:
									progressText = "80%以上";
									progressRate = 0.8;
									break;
							}
						}

						questList.Add("{" +
							$"\"id\":{q.QuestID}," +
							$"\"name\":\"{EscapeJson(q.Name)}\"," +
							$"\"progressText\":\"{EscapeJson(progressText)}\"," +
							$"\"progressRate\":{progressRate:F2}," +
							$"\"state\":{q.State}" +
							"}");
					}
				}
				sb.Append(string.Join(",", questList));
				sb.Append("],");

				// ----------------------------------------------------------------------------
				// 処理ブロック名 : ログデータのJSON化処理
				// 概要           : 最新10件のログ（Priority ≥ 1）を取得してJSON配列として追加します。
				// ----------------------------------------------------------------------------
				sb.Append("\"logs\":[");
				var logList = new List<string>();
				if (Utility.Logger.Log != null && Utility.Logger.Log.Count > 0)
				{
					var recentLogs = Utility.Logger.Log
						.Where(l => l.Priority >= 1)
						.OrderByDescending(l => l.Time)
						.Take(10)
						.ToList();
					foreach (var log in recentLogs)
					{
						string timeStr = log.Time.ToString("HH:mm:ss");
						logList.Add("{" +
							$"\"time\":\"{timeStr}\"," +
							$"\"priority\":{log.Priority}," +
							$"\"message\":\"{EscapeJson(log.Message)}\"" +
							"}");
					}
				}
				sb.Append(string.Join(",", logList));
				sb.Append("]");

				sb.Append("}");

				// WCFを介してブラウザ（サブプロセス）の UpdateHudData に送信
				string hudJson = sb.ToString();
				Browser.AsyncRemoteRun(() => Browser.Proxy.UpdateHudData(hudJson));
			}
			catch (Exception ex)
			{
				Utility.Logger.Add(1, $"遠隔HUDデータ同期エラー: {ex.Message}");
			}
		}

		//====================================================================================
		// 処理ブロック名 : BuildShipJson
		// 概要           : 指定された艦船データからJSON文字列を生成します。
		//                  艦名・レベル・HP・運・燃料・弾薬・装備・パラメータ・入渠情報・艦隊IDを含む一括データを返却します。
		//------------------------------------------------------------------------------------
		private string BuildShipJson(ShipData ship, int fleetId)
		{
			var sb = new StringBuilder();
			sb.Append("{");

			// --- 基本情報 ---
			sb.Append($"\"masterId\":{ship.MasterID},");
			sb.Append($"\"shipId\":{ship.ShipID},");
			sb.Append($"\"name\":\"{EscapeJson(ship.Name)}\",");
			sb.Append($"\"level\":{ship.Level},");
			sb.Append($"\"isMarried\":{ship.IsMarried.ToString().ToLower()},");

			// --- 状態情報 ---
			sb.Append($"\"hp\":{ship.HPCurrent},");
			sb.Append($"\"maxHp\":{ship.HPMax},");
			sb.Append($"\"hpRate\":{ship.HPRate},");
			sb.Append($"\"condition\":{ship.Condition},");
			sb.Append($"\"fuel\":{ship.Fuel},");
			sb.Append($"\"ammo\":{ship.Ammo},");
			sb.Append($"\"fuelMax\":{ship.FuelMax},");
			sb.Append($"\"ammoMax\":{ship.AmmoMax},");
			sb.Append($"\"fuelRate\":{ship.FuelRate},");
			sb.Append($"\"ammoRate\":{ship.AmmoRate},");

			// --- 装備情報（固定4スロット） ---
			sb.Append("\"equipment\":[");
			var slotList = new List<string>();
			for (int i = 0; i < 4 && i < ship.SlotInstance.Count; i++)
			{
				var eq = ship.SlotInstance[i];
				if (eq == null)
				{
					// 空スロットは null で表現
					slotList.Add("null");
				}
				else
				{
					slotList.Add($"\"{EscapeJson(eq.Name)}\"");
				}
			}
			// 6-2 連合艦隊第2艦隊など6スロットを持つ場合、残りのスロットも追加
			for (int i = 4; i < ship.SlotInstance.Count; i++)
			{
				var eq = ship.SlotInstance[i];
				if (eq == null)
				{
					slotList.Add("null");
				}
				else
				{
					slotList.Add($"\"{EscapeJson(eq.Name)}\"");
				}
			}
			// 補強増設（ExpansionSlot）を追加
			// ExpansionSlot: 0=未補強、-1=補強済み（装備なし）、>0=装備あり
			if (ship.ExpansionSlot > 0)
			{
				var expEq = KCDatabase.Instance.Equipments[ship.ExpansionSlot];
				slotList.Add($"\"[補強]{EscapeJson(expEq?.Name ?? "")}\"");
			}
			else if (ship.ExpansionSlot == -1)
			{
				// 補強済みだが装備なし
				slotList.Add("\"[補強]空\"");
			}
			else
			{
				// 未補強
				slotList.Add("\"[補強]---\"");
			}
			sb.Append(string.Join(",", slotList));
			sb.Append("],");

			// --- 航空機搭載量 ---
			sb.Append("\"aircraft\":[");
			var aircraftList = new List<string>();
			for (int i = 0; i < ship.Aircraft.Count; i++)
			{
				aircraftList.Add(ship.Aircraft[i].ToString());
			}
			sb.Append(string.Join(",", aircraftList));
			sb.Append("],");

			// --- パラメータ情報 ---
			sb.Append("\"parameters\":{");
			sb.Append($"\"firepower\":{ship.FirepowerTotal},");
			sb.Append($"\"torpedo\":{ship.TorpedoTotal},");
			sb.Append($"\"aa\":{ship.AATotal},");
			sb.Append($"\"armor\":{ship.ArmorTotal},");
			sb.Append($"\"evasion\":{ship.EvasionTotal},");
			sb.Append($"\"asw\":{ship.ASWTotal},");
			sb.Append($"\"los\":{ship.LOSTotal},");
			sb.Append($"\"luck\":{ship.LuckTotal}");
			sb.Append("},");

			// --- 入渠情報 ---
			sb.Append("\"dock\":{");
			if (ship.RepairingDockID >= 0)
			{
				// 入渠中の場合
				sb.Append("\"is_docking\":true,");
				sb.Append($"\"end_time\":\"{DateTime.Now.AddMilliseconds(ship.RepairTime):yyyy-MM-ddTHH:mm:ss}\"");
			}
			else
			{
				// 入渠していない場合
				sb.Append("\"is_docking\":false,");
				sb.Append("\"end_time\":null");
			}
			sb.Append("},");

			// --- 所属艦隊ID ---
			sb.Append($"\"fleet_id\":{fleetId}");

			sb.Append("}");
			return sb.ToString();
		}

		private string EscapeJson(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
		}

	}


	/// <summary>
	/// 別プロセスのウィンドウにフォーカスがあるとキーボードショートカットが効かなくなるため、
	/// キー関連のメッセージのコピーを別のウィンドウに送る
	/// </summary>
	internal class KeyMessageGrabber : IMessageFilter
	{
		private IntPtr TargetWnd;

		[DllImport("user32.dll")]
		private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		private const int WM_KEYDOWN = 0x100;
		private const int WM_KEYUP = 0x101;
		private const int WM_SYSKEYDOWN = 0x0104;
		private const int WM_SYSKEYUP = 0x0105;

		public KeyMessageGrabber(IntPtr targetWnd)
		{
			TargetWnd = targetWnd;
		}

		public bool PreFilterMessage(ref Message m)
		{
			switch (m.Msg)
			{
				case WM_KEYDOWN:
				case WM_KEYUP:
				case WM_SYSKEYDOWN:
				case WM_SYSKEYUP:
					PostMessage(TargetWnd, m.Msg, m.WParam, m.LParam);
					break;
			}
			return false;
		}
	}

}
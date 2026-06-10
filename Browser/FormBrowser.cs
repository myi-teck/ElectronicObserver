using Browser.CefOp;
using BrowserLib;
using CefSharp;
using CefSharp.WinForms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Browser
{
	/// <summary>
	/// ブラウザを表示するフォームです。
	/// </summary>
	/// <remarks>thx KanColleViewer!</remarks>
	[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single/*, IncludeExceptionDetailInFaults = true*/)]
	public partial class FormBrowser : Form, BrowserLib.IBrowser
	{

		private readonly Size KanColleSize = new Size(1200, 720);
		private readonly string BrowserCachePath = "BrowserCache";
		protected string KanColleUrl => "https://play.games.dmm.com/game/kancolle";
		private string StyleClassId { get; } = Guid.NewGuid().ToString().Substring(0, 8);

		protected string PageScript => $@"
			(function applyPageCss() {{

				const STYLE_ID = '{StyleClassId}';

				function apply() {{
					try {{
						if (!document || !document.head) return false;

						let style = document.getElementById(STYLE_ID);
						if (!style) {{
							style = document.createElement('style');
							style.id = STYLE_ID;
							document.head.appendChild(style);
						}}

						style.textContent = `
							body {{
								margin: 0;
								padding: 0;
								min-width: 0;
								min-height: 0;
								overflow: hidden;
								background-color: black;
							}}

							#main-ntg {{
								position: static;
							}}

							#area-game {{
								margin-left: 0;
								margin-right: 0;
								padding: 0;
								width: 1200px;
								height: 720px;
								position: relative;
							}}

							.dmm-ntgnavi,
							.area-naviapp,
							#ntg-recommend,
							#foot,
							#foot+img {{
								display: none !important;
							}}

							#w, #main-ntg, #page {{
								margin: 0;
								padding: 0;
								width: 100%;
								height: 0;
								background: none !important;
							}}

							.gamesResetStyle,
							.gamesResetStyle * {{
								background: none !important;
							}}

							.gamesResetStyle > header,
							.gamesResetStyle > footer,
							.gamesResetStyle > aside {{
								display: none !important;
							}}

							#game_frame {{
								--game-frame-width: 1200px;
								--game-frame-height: 720px;
								position: fixed;
								top: 0;
								left: 0;
							}}
						`;
						return true;
					}} catch (e) {{
						return false;
					}}
				}}

				// 即時適用 + 自己修復
				if (!apply()) {{
					const timer = setInterval(() => {{
						if (apply()) clearInterval(timer);
					}}, 100);
				}}

			}})();
			";


		protected string FrameScript => $@"
			(function applyFrameCss() {{

				const STYLE_ID = '{StyleClassId}';

				function apply() {{
					try {{
						if (!document || !document.head) return false;

						let style = document.getElementById(STYLE_ID);
						if (!style) {{
							style = document.createElement('style');
							style.id = STYLE_ID;
							document.head.appendChild(style);
						}}

						style.textContent = `
							body {{
								visibility: hidden;
							}}

							#flashWrap {{
								position: fixed;
								left: 0;
								top: 0;
								width: 100% !important;
								height: 100% !important;
							}}

							#htmlWrap {{
								visibility: visible;
								width: 100% !important;
								height: 100% !important;
							}}
						`;
						return true;
					}} catch (e) {{
						return false;
					}}
				}}

				// 即時適用 + 自己修復
				if (!apply()) {{
					const timer = setInterval(() => {{
						if (apply()) clearInterval(timer);
					}}, 100);
				}}

			}})();
			";

		protected string RestoreScript => $@"
			(function restoreCss() {{

				function waitForHead() {{
					if (!document || !document.head) {{
						setTimeout(waitForHead, 50);
						return;
					}}

					try {{
						var node = document.getElementById('{StyleClassId}');
						if (node) {{
							node.remove();
						}}
					}} catch (e) {{
						console.error('RestoreScript: スタイル削除に失敗:', e);
					}}
				}}

				waitForHead();

			}})();
			";

		protected string DMMScript => @"
			(function disableDMMReloadDialog() {

				function waitForDMM() {
					try {
						// DMM.netgame が存在しない場合は待つ
						if (typeof DMM === 'undefined' ||
							!DMM.netgame ||
							typeof DMM.netgame.reloadDialog === 'undefined') {

							setTimeout(waitForDMM, 100);
							return;
						}

						// reloadDialog を無効化
						DMM.netgame.reloadDialog = function () {};

					} catch (e) {
						console.error('DMMScript: DMMリロードダイアログ無効化に失敗:', e);
					}
				}

				waitForDMM();

			})();
			";

		protected string OverrideReloadDialogScript => @"
			(function overrideConfirm() {

				function waitForWindow() {
					try {
						// window が未初期化なら待つ
						if (typeof window === 'undefined') {
							setTimeout(waitForWindow, 50);
							return;
						}

						// confirm が存在しない場合も待つ
						if (typeof window.confirm === 'undefined') {
							setTimeout(waitForWindow, 50);
							return;
						}

						// confirm を無効化
						Object.defineProperty(window, 'confirm', {
							configurable: true,
							writable: true,
							value: function() { return false; }
						});

					} catch (e) {
						console.error('OverrideReloadDialogScript: confirm() 無効化に失敗:', e);
					}
				}

				waitForWindow();

			})();
			";

		/// <summary>
		/// WebGLバッファクリア（真っ黒画面）問題を回避するため、ブラウザの描画フレーム（requestAnimationFrame）に完全同期して
		/// 軽量差分判定を行い、かつメインスレッドをブロックしない非同期toBlob + FileReaderによるバックグラウンドWebPキャッシュを行い、
		/// C#からの要求時にその最新キャッシュを非同期引き抜きするハイブリッドJavaScriptスクリプト。
		/// 変化がない場合はWebPエンコードを完全にスキップし、変化時のみ描画スレッドを占有せずバックグラウンドでWebP化を行います。
		/// 2026/06/02 myi 変更（同期toDataURLを完全廃止し、非同期toBlobによるバックグラウンドエンコードと多重起動防止を実装）
		/// </summary>
		internal string CaptureEngineScript => @"
			(function() {
				// 二重登録 of 防止
				if (window._kancolleCaptureEngine) return;

				window._kancolleCaptureEngine = {
					webpQuality: 0.6,      // WebP圧縮品質（初期値60%、スライダーとリアルタイム連動）
					lastWebp: null,        // 最新のエンコード済みWebP画像キャッシュ
					lastThumbData: null,    // 前フレームの極小サムネイルImageData（差分判定用）
					hasNewFrame: false,    // 未取得 of 新しいフレームがあるかを示すフラグ
					lastCaptureTime: 0,
					intervalMs: 100,       // 10 FPS (100msに1回キャプチャと差分判定を行う)
					isEncoding: false,     // バックグラウンドでのエンコード実行中排他ロックフラグ

					start: function() {
						const canvas = document.querySelector('canvas');
						if (!canvas) {
							// canvas が未生成の場合は150ms待って再試行
							setTimeout(() => this.start(), 150);
							return;
						}

						// 差分判定および縮小処理用の極小インメモリ canvas を初期化
						const thumbCanvas = document.createElement('canvas');
						thumbCanvas.width = 160;
						thumbCanvas.height = 96;
						const thumbCtx = thumbCanvas.getContext('2d');

						// WebGLのpreserveDrawingBuffer=falseによるバッファ即時クリアを克服するため、
						// requestAnimationFrame 同期ループを用いて、描画完了直後のバッファがある瞬間をフックする
						const loop = () => {
							const now = Date.now();
							// 100ms周期に達し、かつ現在エンコード中でない場合のみ処理を実行
							if (now - this.lastCaptureTime >= this.intervalMs && !this.isEncoding) {
								try {
									if (thumbCtx) {
										// 現在のゲーム画面の縮小コピーを描画
										thumbCtx.drawImage(canvas, 0, 0, 160, 96);
										const currThumbData = thumbCtx.getImageData(0, 0, 160, 96);

										let hasDiff = false;
										if (!this.lastThumbData) {
											// 初回起動時は必ず差分ありと判定
											hasDiff = true;
										} else {
											const d1 = this.lastThumbData.data;
											const d2 = currThumbData.data;
											// RGBのピクセル差分をループ比較（しきい値8でアンチエイリアスの微小ノイズを許容）
											for (let i = 0; i < d1.length; i += 4) {
												if (Math.abs(d1[i] - d2[i]) > 8 ||
													Math.abs(d1[i+1] - d2[i+1]) > 8 ||
													Math.abs(d1[i+2] - d2[i+2]) > 8) {
													hasDiff = true;
													break;
												}
											}
										}

										// 差分が検出された場合のみ、WebGLバッファが生きているこの描画フレーム内で非同期エンコードを開始
										if (hasDiff) {
											this.isEncoding = true; // 排他ロックを確保
											this.lastThumbData = currThumbData;

											// 非同期で WebP Blob へエンコード（完全にバックグラウンドスレッドで処理）
											canvas.toBlob((blob) => {
												if (!blob) {
													this.isEncoding = false;
													return;
												}
												// FileReaderを使って非同期に Base64 DataURL へ変換し、完了後にキャッシュを更新
												const reader = new FileReader();
												reader.onloadend = () => {
													this.lastWebp = reader.result;
													this.hasNewFrame = true;
													this.isEncoding = false; // 排他ロックを解放
												};
												reader.onerror = () => {
													this.isEncoding = false;
												};
												reader.readAsDataURL(blob);
											}, 'image/webp', this.webpQuality);
										}
									}
									this.lastCaptureTime = now;
								} catch (e) {
									console.error('Capture loop error:', e);
									this.isEncoding = false;
								}
							}
							requestAnimationFrame(loop);
						};
						requestAnimationFrame(loop);
					},

					// C# から EvaluateScriptAsync で呼び出され、キャッシュデータを引き抜くメソッド
					pull: function(force) {
						// 初回ロード時等で lastWebp キャッシュがまだ無い場合は、その場で一度強制的にエンコードを試みる（フォールバック）
						if (!this.lastWebp) {
							const canvas = document.querySelector('canvas');
							if (canvas) {
								try {
									this.lastWebp = canvas.toDataURL('image/webp', this.webpQuality);
									this.hasNewFrame = true;
								} catch (e) {
									return ['error', e.toString()];
								}
							}
						}

						// クライアントの初期化要求（force = true）時は、未取得フラグにかかわらず現在のキャッシュを強制返却
						if (force && this.lastWebp) {
							return ['full', this.lastWebp];
						}

						// 新しい差分フレームがキャッシュされている場合はそれを返却し、取得済みフラグを下げる
						if (this.hasNewFrame && this.lastWebp) {
							this.hasNewFrame = false;
							return ['full', this.lastWebp];
						}

						// 変化がない場合は、余分なデータ転送を避けるため none を返却
						return ['none', ''];
					}
				};
				window._kancolleCaptureEngine.start();
			})();
			";

		private bool RestoreStyleSheet = false;

		// FormBrowserHostの通信サーバ
		private string ServerUri;

		// FormBrowserの通信サーバ
		private PipeCommunicator<BrowserLib.IBrowserHost> BrowserHost;

		private BrowserLib.BrowserConfiguration Configuration;

		// 親プロセスが生きているか定期的に確認するためのタイマー
		private Timer HeartbeatTimer = new Timer();
		private IntPtr HostWindow;


		internal ChromiumWebBrowser Browser = null;

		private RemoteController _remoteController;

		private string ProxySettings = null;


		private bool _styleSheetApplied;
		/// <summary>
		/// スタイルシートの変更が適用されているか
		/// </summary>
		private bool StyleSheetApplied
		{
			get { return _styleSheetApplied; }
			set
			{
				if (value)
				{
					//Browser.Anchor = AnchorStyles.None;
					ApplyZoom();
					SizeAdjuster_SizeChanged(null, new EventArgs());
				}
				else
				{
					SizeAdjuster.SuspendLayout();
					if (IsBrowserInitialized)
					{
						//Browser.Anchor = AnchorStyles.Top | AnchorStyles.Left;
						Browser.Location = new Point(0, 0);
						Browser.MinimumSize = new Size(0, 0);
						Browser.Size = SizeAdjuster.Size;
					}
					SizeAdjuster.ResumeLayout();
				}

				_styleSheetApplied = value;
			}
		}

		/// <summary>
		/// 艦これが読み込まれているかどうか
		/// </summary>
		private bool IsKanColleLoaded { get; set; }

		private VolumeManager _volumeManager = null;

		private string _lastScreenShotPath;


		private NumericUpDown ToolMenu_Other_Volume_VolumeControl
		{
			get { return (NumericUpDown)((ToolStripControlHost)ToolMenu_Other_Volume.DropDownItems["ToolMenu_Other_Volume_VolumeControlHost"]).Control; }
		}

		private PictureBox ToolMenu_Other_LastScreenShot_Control
		{
			get { return (PictureBox)((ToolStripControlHost)ToolMenu_Other_LastScreenShot.DropDownItems["ToolMenu_Other_LastScreenShot_ImageHost"]).Control; }
		}



		/// <summary>
		/// </summary>
		/// <param name="serverUri">ホストプロセスとの通信用URL</param>
		public FormBrowser(string serverUri)
		{
			InitializeComponent();

			// 拡張メニューの初期化 (タグ付けと共通イベントの適用)
			InitializeColorFilterMenu();

			ServerUri = serverUri;
			StyleSheetApplied = false;


			// 音量設定用コントロールの追加
			{
				var control = new NumericUpDown();
				control.Name = "ToolMenu_Other_Volume_VolumeControl";
				control.Maximum = 100;
				control.TextAlign = HorizontalAlignment.Right;
				control.Font = ToolMenu_Other_Volume.Font;

				control.ValueChanged += ToolMenu_Other_Volume_ValueChanged;
				control.Tag = false;

				var host = new ToolStripControlHost(control, "ToolMenu_Other_Volume_VolumeControlHost");

				control.Size = new Size(host.Width - control.Margin.Horizontal, host.Height - control.Margin.Vertical);
				control.Location = new Point(control.Margin.Left, control.Margin.Top);


				ToolMenu_Other_Volume.DropDownItems.Add(host);
			}

			// スクリーンショットプレビューコントロールの追加
			{
				double zoomrate = 0.5;
				var control = new PictureBox();
				control.Name = "ToolMenu_Other_LastScreenShot_Image";
				control.SizeMode = PictureBoxSizeMode.Zoom;
				control.Size = new Size((int)(KanColleSize.Width * zoomrate), (int)(KanColleSize.Height * zoomrate));
				control.Margin = new Padding();
				control.Image = new Bitmap((int)(KanColleSize.Width * zoomrate), (int)(KanColleSize.Height * zoomrate), PixelFormat.Format24bppRgb);
				using (var g = Graphics.FromImage(control.Image))
				{
					g.Clear(SystemColors.Control);
					g.DrawString("スクリーンショットをまだ撮影していません。\r\n", Font, Brushes.Black, new Point(4, 4));
				}

				var host = new ToolStripControlHost(control, "ToolMenu_Other_LastScreenShot_ImageHost");

				host.Size = new Size(control.Width + control.Margin.Horizontal, control.Height + control.Margin.Vertical);
				host.AutoSize = false;
				control.Location = new Point(control.Margin.Left, control.Margin.Top);

				host.Click += ToolMenu_Other_LastScreenShot_ImageHost_Click;

				ToolMenu_Other_LastScreenShot.DropDownItems.Insert(0, host);
			}

		}


		private void FormBrowser_Load(object sender, EventArgs e)
		{
			SetWindowLong(this.Handle, GWL_STYLE, WS_CHILD);

			// ホストプロセスに接続
			BrowserHost = new PipeCommunicator<BrowserLib.IBrowserHost>(
				this, typeof(BrowserLib.IBrowser), ServerUri + "Browser", "Browser");
			BrowserHost.Connect(ServerUri + "/BrowserHost");
			BrowserHost.Faulted += BrowserHostChannel_Faulted;


			ConfigurationChanged(BrowserHost.Proxy.Configuration);


			// ウィンドウの親子設定＆ホストプロセスから接続してもらう
			BrowserHost.Proxy.ConnectToBrowser(this.Handle);

			// 親ウィンドウが生きているか確認 
			HeartbeatTimer.Tick += (EventHandler)((sender2, e2) =>
			{
				BrowserHost.AsyncRemoteRun(() => { HostWindow = BrowserHost.Proxy.HWND; });
			});
			HeartbeatTimer.Interval = 2000; // 2秒ごと　
			HeartbeatTimer.Start();


			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.GetIconResource());


			InitializeBrowser();

			// 2026/06/10 myi 新規作成（設定されたポート・リッスンアドレスを用いてリモート操作サーバーを開始）
			int port = Configuration.RemoteControllerPort;
			if (port <= 0 || port > 65535)
			{
				port = 8080;
			}
			string address = Configuration.RemoteControllerAddress;
			if (string.IsNullOrEmpty(address))
			{
				address = "*";
			}

			_remoteController = new RemoteController(this, port, address);
			_remoteController.Start();
		}


		/// <summary>
		/// ブラウザを初期化します。
		/// 最初の呼び出しのみ有効です。二回目以降は何もしません。
		/// </summary>
		void InitializeBrowser()
		{
			if (Browser != null)
				return;

			if (ProxySettings == null)
				return;


			var settings = new CefSettings()
			{
				BrowserSubprocessPath = Path.Combine(
						AppDomain.CurrentDomain.SetupInformation.ApplicationBase,
						Environment.Is64BitProcess ? "x64" : "x86",
						"CefSharp.BrowserSubprocess.exe"),
				CachePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), BrowserCachePath),
				Locale = "ja",
				AcceptLanguageList = "ja,en-US,en",        // todo: いる？
				LogSeverity = Configuration.SavesBrowserLog ? LogSeverity.Error : LogSeverity.Disable,
				LogFile = Configuration.SavesBrowserLog
					? Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "BrowserLog.log")
					: string.Empty,
			};

			if (!Configuration.HardwareAccelerationEnabled)
				settings.DisableGpuAcceleration();

			settings.CefCommandLineArgs.Add("proxy-server", ProxySettings);
			if (Configuration.ForceColorProfile)
				settings.CefCommandLineArgs.Add("force-color-profile", "srgb");

			// --------------------------------------------------------------------------------------------
			// 遠隔操作時のために、バックグラウンド（最小化や他のウィンドウに隠れた状態）
			// でも描画・JavaScriptを停止・制限させない設定
			// --------------------------------------------------------------------------------------------
			settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
			settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
			settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");
			// Chrome 87以降で導入された ネイティブウィンドウの隠蔽検知 による描画停止を無効化
			settings.CefCommandLineArgs.Add("disable-features", "CalculateNativeWinOcclusion,RendererBackgrounding");

			CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
			Cef.Initialize(settings, false, (IBrowserProcessHandler)null);


			var requestHandler = new CustomRequestHandler(pixiSettingEnabled: Configuration.PreserveDrawingBuffer);
			requestHandler.RenderProcessTerminated += (mes) => AddLog(3, mes);

			Browser = new ChromiumWebBrowser(@"about:blank")
			{
				Dock = DockStyle.None,
				Size = SizeAdjuster.Size,
				RequestHandler = requestHandler,
				MenuHandler = new MenuHandler(),
				KeyboardHandler = new KeyboardHandler(),
				DragHandler = new DragHandler(),
			};
			Browser.LoadingStateChanged += Browser_LoadingStateChanged;
			Browser.FrameLoadStart += Browser_OnFrameLoadStart;
			Browser.FrameLoadEnd += Browser_FrameLoadEnd;
			Browser.IsBrowserInitializedChanged += Browser_IsBrowserInitializedChanged;
			SizeAdjuster.Controls.Add(Browser);
		}


		void Exit()
		{
			if (!BrowserHost.Closed)
			{
				BrowserHost.Close();
				HeartbeatTimer.Stop();
				_remoteController?.Dispose();
				Cef.Shutdown();
				Application.Exit();
			}
		}

		void BrowserHostChannel_Faulted(Exception e)
		{
			// 親と通信できなくなったら終了する
			Exit();
		}

		public void CloseBrowser()
		{
			HeartbeatTimer.Stop();
			// リモートコールでClose()呼ぶのばヤバそうなので非同期にしておく
			BeginInvoke((Action)(() => Exit()));
		}

		public void ConfigurationChanged(BrowserLib.BrowserConfiguration conf)
		{
			Configuration = conf;

			SizeAdjuster.AutoScroll = Configuration.IsScrollable;
			ToolMenu_Other_Zoom_Fit.Checked = Configuration.ZoomFit;
			ApplyZoom();
			ToolMenu_Other_AppliesStyleSheet.Checked = Configuration.AppliesStyleSheet;
			ToolMenu.Dock = (DockStyle)Configuration.ToolMenuDockStyle;
			ToolMenu.Visible = Configuration.IsToolMenuVisible;
			ToolMenu_Other_ClearCache.Visible = conf.EnableDebugMenu;

			UpdateColorFilterMenu();
			ApplyColorFilter();
		}

		private void ConfigurationUpdated()
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.ConfigurationUpdated(Configuration));
		}

		/// <summary>
		/// リモートコントローラーからログインページへ移動します。
		/// </summary>
		public void NavigateToLoginPage()
		{
			Navigate(Configuration.LogInPageURL);
		}

		/// <summary>
		/// リモートコントローラーからプライバシーフィルターモードを設定します。
		/// </summary>
		public void SetFilterMode(int mode)
		{
			Configuration.ColorFilterMode = mode;
			ApplyColorFilter();
			ConfigurationUpdated();
		}

		private void AddLog(int priority, string message)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.AddLog(priority, message));
		}

		internal void SendErrorReport(string exceptionName, string message)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.SendErrorReport(exceptionName, message));
		}


		public void InitialAPIReceived()
		{
			IsKanColleLoaded = true;

			//ロード直後の適用ではレイアウトがなぜか崩れるのでこのタイミングでも適用
			ApplyStyleSheet();
			ApplyZoom();
			ApplyColorFilter();
			DestroyDMMreloadDialog();

			//起動直後はまだ音声が鳴っていないのでミュートできないため、この時点で有効化
			SetVolumeState();
		}


		private void SizeAdjuster_SizeChanged(object sender, EventArgs e)
		{
			// 最小化や非表示時はブラウザコントロールのサイズ変更をスキップする
			// 理由：最小化やドッキング解除による一時的な非表示などを行うと、SizeAdjusterのサイズが
			//       極小（0×0や1×1など）になり、Browser.Sizeも連動して極小化される。
			//       これによりCEFのビューポートも縮小され、リモート操作(RemoteController)の
			//       SendMouseClickEventに渡す座標がビューポート外となり、クリックが一切効かなくなる。
			//       そのため、極小サイズ時はサイズ変更処理を行わず、以前の正常なサイズを維持する。
			if (SizeAdjuster.Width <= 10 || SizeAdjuster.Height <= 10)
				return;

			if (!StyleSheetApplied)
			{
				if (Browser != null)
				{
					Browser.Location = new Point(0, 0);
					Browser.Size = SizeAdjuster.Size;
				}
				return;
			}

			ApplyZoom();
		}

		private void CenteringBrowser()
		{
			if (SizeAdjuster.Width == 0 || SizeAdjuster.Height == 0) return;
			int x = Browser.Location.X, y = Browser.Location.Y;
			bool isScrollable = Configuration.IsScrollable;

			if (!isScrollable || Browser.Width <= SizeAdjuster.Width)
			{
				x = (SizeAdjuster.Width - Browser.Width) / 2;
			}
			if (!isScrollable || Browser.Height <= SizeAdjuster.Height)
			{
				y = (SizeAdjuster.Height - Browser.Height) / 2;
			}

			//if ( x != Browser.Location.X || y != Browser.Location.Y )
			Browser.Location = new Point(x, y);
		}


		// ---------------------------------------------------------
		// LoadingStateChanged（DOM 完成後の確実な再適用）
		// ---------------------------------------------------------
		private void Browser_LoadingStateChanged(object sender, LoadingStateChangedEventArgs e)
		{
			if (e.IsLoading)
				return;

			BeginInvoke((Action)(() =>
			{
				try
				{
					// DOM が完成した後に確実に適用
					ApplyStyleSheet();
					ApplyZoom();
					ApplyColorFilter();
					DestroyDMMreloadDialog();
				}
				catch (Exception ex)
				{
					SendErrorReport(ex.ToString(), "LoadingStateChanged: 再適用に失敗しました。");
				}
			}));
		}


		internal bool IsBrowserInitialized =>
			Browser != null &&
			Browser.IsBrowserInitialized;

		internal IFrame GetMainFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			var frame = browser.MainFrame;

			if (IsMainFrame(frame))
				return frame;

			return null;
		}

		internal IFrame GetGameFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (IsGameFrame(frame))
					return frame;
			}
			return null;
		}

		internal IFrame GetKanColleFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (IsKanColleFrame(frame))
					return frame;
			}
			return null;
		}



		private bool IsMainFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var url = frame.Url;

				// DMM のメインページはほぼ必ずこのどれか
				return url.Contains("dmm.com/netgame") ||
					   url.Contains("dmm.com/play") ||
					   url.Contains("games.dmm.com") ||
					   url.Contains("www.dmm.com/netgame");
			}
			catch
			{
				return false;
			}
		}


		private bool IsGameFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var uri = new Uri(frame.Url);

				// KanColle のゲームフレームは必ず /gadgets/ifr で始まる
				return uri.Host.Contains("osapi.dmm.com") &&
					   uri.AbsolutePath.StartsWith("/gadgets/ifr");
			}
			catch
			{
				return false;
			}
		}

		private bool IsKanColleFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var uri = new Uri(frame.Url);

				return uri.AbsolutePath.Contains("/kcs2/index.php");
			}
			catch
			{
				return false;
			}
		}



		/// <summary>
		/// スタイルシートを適用します。
		/// </summary>
		public void ApplyStyleSheet()
		{
			if (!IsBrowserInitialized)
				return;

			if (!Configuration.AppliesStyleSheet && !RestoreStyleSheet)
				return;

			try
			{
				var mainframe = GetMainFrame();
				var gameframe = GetGameFrame();

				// --- Restore ---
				if (RestoreStyleSheet)
				{
					mainframe?.EvaluateScriptAsync(RestoreScript);
					gameframe?.EvaluateScriptAsync(RestoreScript);

					StyleSheetApplied = false;
					RestoreStyleSheet = false;
					return;
				}

				// --- Apply ---
				// MainFrame は先行適用（v140 対策）
				if (mainframe != null)
				{
					mainframe.EvaluateScriptAsync(PageScript);
				}

				// GameFrame が取れた時点で「完成」
				if (gameframe != null)
				{
					gameframe.EvaluateScriptAsync(FrameScript)
						.ContinueWith(_ =>
						{
							BeginInvoke((Action)(() =>
							{
								StyleSheetApplied = true;
								ApplyZoom();
							}));
						});
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "ApplyStyleSheet: スタイルシート適用に失敗しました。");
			}
		}

		private BrowserPrivacyFilterManager _privacyFilterManager;
		internal BrowserPrivacyFilterManager PrivacyFilterManager => _privacyFilterManager ?? (_privacyFilterManager = new BrowserPrivacyFilterManager(this));

		/// <summary>
		/// プライバシーフィルタ(カラーフィルタや目隠し設定等)を適用または解除します。
		/// </summary>
		public void ApplyColorFilter()
		{
			PrivacyFilterManager.ApplyFilter(Configuration.ColorFilterMode, Configuration.AppliesHoverCover);
		}

		private void InitializeColorFilterMenu()
		{
			// UIアイテムにモード番号をTagとして持たせ、排他選択のイベントハンドラを登録する
			ToolMenu_ColorFilter_None.Tag = 0;
			ToolMenu_ColorFilter_Grayscale.Tag = 1;
			ToolMenu_ColorFilter_LowContrast.Tag = 4;

			var items = new[] {
				ToolMenu_ColorFilter_None,
				ToolMenu_ColorFilter_Grayscale,
				ToolMenu_ColorFilter_LowContrast
			};

			foreach (var item in items)
			{
				item.Click += ToolMenu_ColorFilter_Item_Click;
			}

			// カバー機能（ホバー時のみ表示）は独立のトグルスイッチとして別登録
			ToolMenu_ColorFilter_HoverCover.Click += ToolMenu_ColorFilter_HoverCover_Click;
		}

		private void ToolMenu_ColorFilter_Item_Click(object sender, EventArgs e)
		{
			if (sender is ToolStripMenuItem item && item.Tag is int mode)
			{
				Configuration.ColorFilterMode = mode;
				UpdateColorFilterMenu();
				ApplyColorFilter();
				ConfigurationUpdated();
			}
		}

		private void ToolMenu_ColorFilter_HoverCover_Click(object sender, EventArgs e)
		{
			Configuration.AppliesHoverCover = ToolMenu_ColorFilter_HoverCover.Checked;
			ApplyColorFilter(); // ColorFilter処理も再評価・再適用する可能性があるため共通メソッドを呼ぶ
			ConfigurationUpdated();
		}

		private void UpdateColorFilterMenu()
		{
			ToolMenu_ColorFilter_None.Checked = (Configuration.ColorFilterMode == 0);
			ToolMenu_ColorFilter_Grayscale.Checked = (Configuration.ColorFilterMode == 1);
			ToolMenu_ColorFilter_LowContrast.Checked = (Configuration.ColorFilterMode == 4);

			ToolMenu_ColorFilter_HoverCover.Checked = Configuration.AppliesHoverCover;
		}



		/// <summary>
		/// DMMによるページ更新ダイアログを非表示にします。
		/// </summary>
		public void DestroyDMMreloadDialog()
		{
			if (!IsBrowserInitialized)
				return;

			if (!Configuration.IsDMMreloadDialogDestroyable)
				return;

			try
			{
				var mainframe = GetMainFrame();
				if (mainframe == null)
					return;

				mainframe.EvaluateScriptAsync(DMMScript);
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "DMMによるページ更新ダイアログの非表示に失敗しました。");
			}

		}


		// ---------------------------------------------------------
		// FrameLoadStart（早期注入）
		// ---------------------------------------------------------
		private void Browser_OnFrameLoadStart(object sender, FrameLoadStartEventArgs e)
		{
			if (Configuration == null)
				return;

			try
			{
				// メインフレーム → PageScript（DOM 安全版）
				if (IsMainFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(PageScript);
				}

				// ゲームフレーム → FrameScript（DOM 安全版）
				if (IsGameFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(FrameScript);
				}

				// confirm() 無効化（DOM 安全版）
				if (Configuration.IsDMMreloadDialogDestroyable && e.Frame.IsMain)
				{
					e.Frame.ExecuteJavaScriptAsync(OverrideReloadDialogScript);
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "FrameLoadStart: スクリプト注入に失敗しました。");
			}
		}


		// タイミングによっては(特に起動時)、ブラウザの初期化が完了する前に Navigate() が呼ばれることがある
		// その場合ロードに失敗してブラウザが白画面でスタートしてしまう（手動でログインページを開けば続行は可能だが）
		// 応急処置として失敗したとき後で再試行するようにしてみる
		private string navigateCache = null;
		private void Browser_IsBrowserInitializedChanged(object sender, EventArgs e)
		{
			if (IsBrowserInitialized && navigateCache != null)
			{
				// ロードが完了したので再試行
				string url = navigateCache;            // 非同期コールするのでコピーを取っておく必要がある
				BeginInvoke((Action)(() => Navigate(url)));
				navigateCache = null;
			}
		}

		/// <summary>
		/// 指定した URL のページを開きます。
		/// </summary>
		public void Navigate(string url)
		{
			if (url != Configuration.LogInPageURL || !Configuration.AppliesStyleSheet)
				StyleSheetApplied = false;
			Browser.Load(url);

			if (!IsBrowserInitialized)
			{
				// 大方ロードできないのであとで再試行する
				navigateCache = url;
			}
		}

		/// <summary>
		/// ブラウザを再読み込みします。
		/// </summary>
		public void RefreshBrowser() => RefreshBrowser(false);

		/// <summary>
		/// ブラウザを再読み込みします。
		/// </summary>
		/// <param name="ignoreCache">キャッシュを無視するか。</param>
		public void RefreshBrowser(bool ignoreCache)
		{
			if (!Configuration.AppliesStyleSheet)
				StyleSheetApplied = false;

			// UI スレッドで実行する（WCF 等から来る可能性に対応）
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => RefreshBrowser(ignoreCache)));
				return;
			}

			try
			{
				// キャッシュ無視モードはフルリロード（Cef の API に合わせる）
				if (ignoreCache)
				{
					Browser.Reload(true);
					return;
				}

				// 通常はメインフレームだけリロードして副次的な負荷を減らす
				var mainFrame = GetMainFrame();
				if (mainFrame != null)
				{
					try
					{
						// IFrame に Reload() は存在しないため、フレーム側で location.reload() を実行する
						mainFrame.ExecuteJavaScriptAsync("location.reload();");
						return;
					}
					catch
					{
						// フレーム経由で失敗したらフォールバック
					}
				}

				// フォールバック：ブラウザ全体をリロード
				Browser.Reload(false);
			}
			catch (Exception ex)
			{
				// 応急処置としてログを残しておく
				AddLog(1, "RefreshBrowser failed: " + ex.Message);
				try { Browser.Reload(ignoreCache); } catch { }
			}
		}

		/// <summary>
		/// メインプロセスから送られてきたゲーム内HUDデータを受信し、リモートコントローラーへ中継します。
		/// </summary>
		public void UpdateHudData(string hudJson)
		{
			_remoteController?.UpdateHudData(hudJson);
		}

		private void Browser_FrameLoadEnd(object sender, FrameLoadEndEventArgs e)
		{
			// 艦これゲームフレームのロード完了時に、常駐キャプチャエンジンを自動注入して起動
			// 2026/06/01 myi 新規作成
			try
			{
				if (IsKanColleFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(CaptureEngineScript);
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: キャプチャエンジンの自動注入に失敗しました。");
			}

			if (!Configuration.AppliesStyleSheet)
				return;

			try
			{
				if (IsMainFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(PageScript);
				}

				if (IsGameFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(FrameScript);
					StyleSheetApplied = true;
				}
			}
			catch { }
		}


		/// <summary>
		/// ズームを適用します。
		/// </summary>
		public void ApplyZoom()
		{
			if (!IsBrowserInitialized)
				return;

			// 最小化や非表示などでサイズが極小になっている場合は処理しない
			// 理由：極小サイズでズーム計算やブラウザサイズの再設定を行うと、Browser.Sizeも極小化され、
			//       リモート操作の座標計算やクリックイベント送信が正常に動作しなくなるため。
			if (SizeAdjuster.Width <= 10 || SizeAdjuster.Height <= 10)
				return;


			double zoomRate = Configuration.ZoomRate;
			bool fit = Configuration.ZoomFit && StyleSheetApplied;


			double zoomFactor;

			if (fit)
			{
				double rateX = (double)SizeAdjuster.Width / KanColleSize.Width;
				double rateY = (double)SizeAdjuster.Height / KanColleSize.Height;
				zoomFactor = Math.Min(rateX, rateY);
			}
			else
			{
				if (zoomRate < 0.1)
					zoomRate = 0.1;
				if (zoomRate > 10)
					zoomRate = 10;

				zoomFactor = zoomRate;
			}


			Browser.SetZoomLevel(Math.Log(zoomFactor, 1.2));


			if (StyleSheetApplied)
			{
				Browser.Size = Browser.MinimumSize = new Size(
					(int)(KanColleSize.Width * zoomFactor),
					(int)(KanColleSize.Height * zoomFactor)
					);

				CenteringBrowser();
			}

			if (fit)
			{
				ToolMenu_Other_Zoom_Current.Text = "現在: ぴったり";
			}
			else
			{
				ToolMenu_Other_Zoom_Current.Text = $"現在: {zoomRate:p1}";
			}

		}



		/// <summary>
		/// スクリーンショットを撮影します。
		/// </summary>
		private async Task<Bitmap> TakeScreenShot()
		{
			var kancolleFrame = GetKanColleFrame();
			if (kancolleFrame == null)
			{
				AddLog(3, string.Format("艦これが読み込まれていないため、スクリーンショットを撮ることはできません。"));
				System.Media.SystemSounds.Beep.Play();
				return null;
			}


			Task<ScreenShotPacket> InternalTakeScreenShot()
			{
				var request = new ScreenShotPacket();

				if (Browser == null || !Browser.IsBrowserInitialized)
					return request.TaskSource.Task;


				string script = $@"
					(async function() {{
						await CefSharp.BindObjectAsync('{request.ID}');

						function waitForCanvas() {{
							return new Promise(resolve => {{
								const check = () => {{
									const canvas = document.querySelector('canvas');
									if (canvas && canvas.width > 0 && canvas.height > 0) {{
										resolve(canvas);
									}} else {{
										requestAnimationFrame(check);
									}}
								}};
								check();
							}});
						}}

						const canvas = await waitForCanvas();

						requestAnimationFrame(() => {{
							try {{
								const dataurl = canvas.toDataURL('image/png');
								{request.ID}.complete(dataurl);
							}} catch (e) {{
								{request.ID}.complete(null);
							}}
						}});
					}})();
					";

				Browser.JavascriptObjectRepository.Register(request.ID, request, true);
				kancolleFrame.ExecuteJavaScriptAsync(script);

				return request.TaskSource.Task;
			}

			var result = await InternalTakeScreenShot();

			// ごみ掃除
			Browser.JavascriptObjectRepository.UnRegister(result.ID);
			kancolleFrame.ExecuteJavaScriptAsync($@"delete {result.ID}");

			return result.GetImage();
		}



		/// <summary>
		/// スクリーンショットを撮影し、設定で指定された保存先に保存します。
		/// </summary>
		public async Task SaveScreenShot()
		{

			int savemode = Configuration.ScreenShotSaveMode;
			int format = Configuration.ScreenShotFormat;
			string folderPath = Configuration.ScreenShotPath;
			bool is32bpp = format != 1 && Configuration.AvoidTwitterDeterioration;

			Bitmap image = null;
			try
			{
				image = await TakeScreenShot();


				if (image == null)
					return;

				if (is32bpp)
				{
					if (image.PixelFormat != PixelFormat.Format32bppArgb)
					{
						var imgalt = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
						using (var g = Graphics.FromImage(imgalt))
						{
							g.DrawImage(image, new Rectangle(0, 0, imgalt.Width, imgalt.Height));
						}

						image.Dispose();
						image = imgalt;
					}

					// 不透明ピクセルのみだと jpeg 化されてしまうため、1px だけわずかに透明にする
					Color temp = image.GetPixel(image.Width - 1, image.Height - 1);
					image.SetPixel(image.Width - 1, image.Height - 1, Color.FromArgb(252, temp.R, temp.G, temp.B));
				}
				else
				{
					if (image.PixelFormat != PixelFormat.Format24bppRgb)
					{
						var imgalt = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
						using (var g = Graphics.FromImage(imgalt))
						{
							g.DrawImage(image, new Rectangle(0, 0, imgalt.Width, imgalt.Height));
						}

						image.Dispose();
						image = imgalt;
					}
				}


				// to file
				if ((savemode & 1) != 0)
				{
					try
					{
						if (!Directory.Exists(folderPath))
							Directory.CreateDirectory(folderPath);

						string ext;
						ImageFormat imgFormat;

						switch (format)
						{
							case 1:
								ext = "jpg";
								imgFormat = ImageFormat.Jpeg;
								break;
							case 2:
							default:
								ext = "png";
								imgFormat = ImageFormat.Png;
								break;
						}

						string path = $"{folderPath}\\{DateTime.Now:yyyyMMdd_HHmmssff}.{ext}";
						image.Save(path, imgFormat);
						_lastScreenShotPath = path;

						AddLog(2, $"スクリーンショットを {path} に保存しました。");
					}
					catch (Exception ex)
					{
						SendErrorReport(ex.ToString(), "スクリーンショットの保存に失敗しました。");
					}
				}


				// to clipboard
				if ((savemode & 2) != 0)
				{
					try
					{
						Clipboard.SetImage(image);

						if ((savemode & 3) != 3)
							AddLog(2, "スクリーンショットをクリップボードにコピーしました。");
					}
					catch (Exception ex)
					{
						SendErrorReport(ex.ToString(), "スクリーンショットのクリップボードへのコピーに失敗しました。");
					}
				}

			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "スクリーンショットの撮影に失敗しました。");
			}
			finally
			{
				image?.Dispose();
			}

		}


		public void SetProxy(string proxy)
		{
			ushort port;
			if (ushort.TryParse(proxy, out port))
			{
				ProxySettings = "http=127.0.0.1:" + port;           // todo: 動くには動くが正しいかわからない
			}
			else
			{
				//WinInetUtil.SetProxyInProcess(proxy, "local");
				ProxySettings = proxy;
			}

			InitializeBrowser();

			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.SetProxyCompleted());
		}



		public void SetIconResource(byte[] canvas)
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

			for (int i = 0; i < keys.Length; i++)
			{
				Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);

				if (canvas != null)
				{
					BitmapData bmpdata = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
					Marshal.Copy(canvas, unitsize * i, bmpdata.Scan0, unitsize);
					bmp.UnlockBits(bmpdata);
				}

				Icons.Images.Add(keys[i], bmp);
			}


			ToolMenu_ScreenShot.Image = ToolMenu_Other_ScreenShot.Image =
				Icons.Images["Browser_ScreenShot"];
			ToolMenu_Zoom.Image = ToolMenu_Other_Zoom.Image =
				Icons.Images["Browser_Zoom"];
			ToolMenu_Other_Zoom_Increment.Image =
				Icons.Images["Browser_ZoomIn"];
			ToolMenu_Other_Zoom_Decrement.Image =
				Icons.Images["Browser_ZoomOut"];
			ToolMenu_Refresh.Image = ToolMenu_Other_Refresh.Image =
				Icons.Images["Browser_Refresh"];
			ToolMenu_NavigateToLogInPage.Image = ToolMenu_Other_NavigateToLogInPage.Image =
				Icons.Images["Browser_Navigate"];
			ToolMenu_Other.Image =
				Icons.Images["Browser_Other"];

			ToolMenu_ColorFilter.Image = Icons.Images["Browser_ScreenShot"]; // TODO: 専用アイコンが無い為、とりあえずスクリーンショットと同じアイコン
			ToolMenu_ColorFilter.Text = "プライバシーフィルタの切り替え";

			SetVolumeState();
		}


		private void TryGetVolumeManager()
		{
			_volumeManager = VolumeManager.CreateInstanceByProcessName("CefSharp.BrowserSubprocess");
		}

		private void SetVolumeState()
		{

			bool mute;
			float volume;

			try
			{
				if (_volumeManager == null)
				{
					TryGetVolumeManager();
				}

				mute = _volumeManager.IsMute;
				volume = _volumeManager.Volume * 100;

			}
			catch (Exception)
			{
				// 音量データ取得不能時
				_volumeManager = null;
				mute = false;
				volume = 100;
			}

			ToolMenu_Mute.Image = ToolMenu_Other_Mute.Image =
				Icons.Images[mute ? "Browser_Mute" : "Browser_Unmute"];

			{
				var control = ToolMenu_Other_Volume_VolumeControl;
				control.Tag = false;
				control.Value = (decimal)volume;
				control.Tag = true;
			}

			Configuration.Volume = volume;
			Configuration.IsMute = mute;
			ConfigurationUpdated();
		}


		private async void ToolMenu_Other_ScreenShot_Click(object sender, EventArgs e)
		{
			await SaveScreenShot();
		}

		private void ToolMenu_Other_Zoom_Decrement_Click(object sender, EventArgs e)
		{
			Configuration.ZoomRate = Math.Max(Configuration.ZoomRate - 0.2, 0.1);
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Increment_Click(object sender, EventArgs e)
		{
			Configuration.ZoomRate = Math.Min(Configuration.ZoomRate + 0.2, 10);
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Click(object sender, EventArgs e)
		{

			double zoom;

			if (sender == ToolMenu_Other_Zoom_25)
				zoom = 0.25;
			else if (sender == ToolMenu_Other_Zoom_50)
				zoom = 0.50;
			else if (sender == ToolMenu_Other_Zoom_Classic)
				zoom = 0.667;       // 2/3 ジャストだと 799x479 になる
			else if (sender == ToolMenu_Other_Zoom_75)
				zoom = 0.75;
			else if (sender == ToolMenu_Other_Zoom_100)
				zoom = 1;
			else if (sender == ToolMenu_Other_Zoom_150)
				zoom = 1.5;
			else if (sender == ToolMenu_Other_Zoom_200)
				zoom = 2;
			else if (sender == ToolMenu_Other_Zoom_250)
				zoom = 2.5;
			else if (sender == ToolMenu_Other_Zoom_300)
				zoom = 3;
			else if (sender == ToolMenu_Other_Zoom_400)
				zoom = 4;
			else
				zoom = 1;

			Configuration.ZoomRate = zoom;
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Fit_Click(object sender, EventArgs e)
		{
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked;
			ApplyZoom();
			ConfigurationUpdated();
		}


		//ズームUIの使いまわし
		private void ToolMenu_Other_DropDownOpening(object sender, EventArgs e)
		{
			var list = ToolMenu_Zoom.DropDownItems.Cast<ToolStripItem>().ToArray();
			ToolMenu_Other_Zoom.DropDownItems.AddRange(list);
		}

		private void ToolMenu_Zoom_DropDownOpening(object sender, EventArgs e)
		{

			var list = ToolMenu_Other_Zoom.DropDownItems.Cast<ToolStripItem>().ToArray();
			ToolMenu_Zoom.DropDownItems.AddRange(list);
		}


		private void ToolMenu_Other_Mute_Click(object sender, EventArgs e)
		{
			if (_volumeManager == null)
			{
				TryGetVolumeManager();
			}

			try
			{
				_volumeManager.ToggleMute();

			}
			catch (Exception)
			{
				System.Media.SystemSounds.Beep.Play();
			}

			SetVolumeState();
		}

		void ToolMenu_Other_Volume_ValueChanged(object sender, EventArgs e)
		{

			var control = ToolMenu_Other_Volume_VolumeControl;

			if (_volumeManager == null)
			{
				TryGetVolumeManager();
			}

			try
			{
				if ((bool)control.Tag)
					_volumeManager.Volume = (float)(control.Value / 100);
				control.BackColor = SystemColors.Window;

			}
			catch (Exception)
			{
				control.BackColor = Color.MistyRose;

			}

		}


		private void ToolMenu_Other_Refresh_Click(object sender, EventArgs e)
		{

			if (!Configuration.ConfirmAtRefresh ||
				MessageBox.Show("再読み込みします。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{
				RefreshBrowser();
			}
		}

		private void ToolMenu_Other_RefreshIgnoreCache_Click(object sender, EventArgs e)
		{
			if (!Configuration.ConfirmAtRefresh ||
				MessageBox.Show("キャッシュを無視して再読み込みします。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{
				RefreshBrowser(true);
			}
		}

		private void ToolMenu_Other_NavigateToLogInPage_Click(object sender, EventArgs e)
		{

			if (MessageBox.Show("ログインページへ移動します。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{

				Navigate(Configuration.LogInPageURL);
			}

		}

		private void ToolMenu_Other_Navigate_Click(object sender, EventArgs e)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.RequestNavigation(Browser.GetMainFrame()?.Url ?? ""));
		}

		private void ToolMenu_Other_AppliesStyleSheet_Click(object sender, EventArgs e)
		{
			Configuration.AppliesStyleSheet = ToolMenu_Other_AppliesStyleSheet.Checked;
			if (!Configuration.AppliesStyleSheet)
				RestoreStyleSheet = true;
			ApplyStyleSheet();
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Alignment_Click(object sender, EventArgs e)
		{

			if (sender == ToolMenu_Other_Alignment_Top)
				ToolMenu.Dock = DockStyle.Top;
			else if (sender == ToolMenu_Other_Alignment_Bottom)
				ToolMenu.Dock = DockStyle.Bottom;
			else if (sender == ToolMenu_Other_Alignment_Left)
				ToolMenu.Dock = DockStyle.Left;
			else
				ToolMenu.Dock = DockStyle.Right;

			Configuration.ToolMenuDockStyle = (int)ToolMenu.Dock;

			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Alignment_Invisible_Click(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = false;
			ConfigurationUpdated();
		}



		private void SizeAdjuster_DoubleClick(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = true;
			ConfigurationUpdated();
		}

		private void ContextMenuTool_ShowToolMenu_Click(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = true;
			ConfigurationUpdated();
		}

		private void ContextMenuTool_Opening(object sender, CancelEventArgs e)
		{

			if (IsKanColleLoaded || ToolMenu.Visible)
				e.Cancel = true;
		}


		private void ToolMenu_ScreenShot_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_ScreenShot_Click(sender, e);
		}

		private void ToolMenu_Mute_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_Mute_Click(sender, e);
		}

		private void ToolMenu_Refresh_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_Refresh_Click(sender, e);
		}

		private void ToolMenu_NavigateToLogInPage_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_NavigateToLogInPage_Click(sender, e);
		}




		private void FormBrowser_Activated(object sender, EventArgs e)
		{
			Browser.Focus();
		}

		private void ToolMenu_Other_Alignment_DropDownOpening(object sender, EventArgs e)
		{

			foreach (var item in ToolMenu_Other_Alignment.DropDownItems)
			{
				var menu = item as ToolStripMenuItem;
				if (menu != null)
				{
					menu.Checked = false;
				}
			}

			switch ((DockStyle)Configuration.ToolMenuDockStyle)
			{
				case DockStyle.Top:
					ToolMenu_Other_Alignment_Top.Checked = true;
					break;
				case DockStyle.Bottom:
					ToolMenu_Other_Alignment_Bottom.Checked = true;
					break;
				case DockStyle.Left:
					ToolMenu_Other_Alignment_Left.Checked = true;
					break;
				case DockStyle.Right:
					ToolMenu_Other_Alignment_Right.Checked = true;
					break;
			}

			ToolMenu_Other_Alignment_Invisible.Checked = !Configuration.IsToolMenuVisible;
		}


		private void ToolMenu_Other_LastScreenShot_DropDownOpening(object sender, EventArgs e)
		{

			try
			{

				using (var fs = new FileStream(_lastScreenShotPath, FileMode.Open, FileAccess.Read))
				{
					ToolMenu_Other_LastScreenShot_Control.Image?.Dispose();

					ToolMenu_Other_LastScreenShot_Control.Image = Image.FromStream(fs);
				}

			}
			catch (Exception)
			{
				// *ぷちっ*
			}

		}

		void ToolMenu_Other_LastScreenShot_ImageHost_Click(object sender, EventArgs e)
		{
			if (_lastScreenShotPath != null && File.Exists(_lastScreenShotPath))
				Process.Start(_lastScreenShotPath);
		}

		private void ToolMenu_Other_LastScreenShot_OpenScreenShotFolder_Click(object sender, EventArgs e)
		{
			if (Directory.Exists(Configuration.ScreenShotPath))
				Process.Start(Configuration.ScreenShotPath);
		}

		private void ToolMenu_Other_LastScreenShot_CopyToClipboard_Click(object sender, EventArgs e)
		{

			if (_lastScreenShotPath != null && File.Exists(_lastScreenShotPath))
			{
				try
				{
					using (var img = new Bitmap(_lastScreenShotPath))
					{
						Clipboard.SetImage(img);
						AddLog(2, string.Format("スクリーンショット {0} をクリップボードにコピーしました。", _lastScreenShotPath));
					}
				}
				catch (Exception ex)
				{
					SendErrorReport(ex.Message, "スクリーンショットのクリップボードへのコピーに失敗しました。");
				}
			}
		}

		private void ToolMenu_Other_OpenDevTool_Click(object sender, EventArgs e)
		{
			if (!IsBrowserInitialized)
				return;

			Browser.GetBrowser().ShowDevTools();
		}

		private void ToolMenu_Other_ClearCache_Click(object sender, EventArgs e)
		{
			if (MessageBox.Show("キャッシュをクリアするため、ブラウザを再起動します。\r\nよろしいですか？\r\n※環境によっては本ツールが終了する場合があります。その場合は再起動してください。", "ブラウザ再起動確認",
				MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
			{
				BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.ClearCache());
			}
		}

		protected override void WndProc(ref Message m)
		{

			if (m.Msg == WM_ERASEBKGND)
				// ignore this message
				return;

			base.WndProc(ref m);
		}


		#region 呪文


		[DllImport("user32.dll", EntryPoint = "GetWindowLongA", SetLastError = true)]
		private static extern uint GetWindowLong(IntPtr hwnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
		private static extern uint SetWindowLong(IntPtr hwnd, int nIndex, uint dwNewLong);

		private const int GWL_STYLE = (-16);
		private const uint WS_CHILD = 0x40000000;
		private const uint WS_VISIBLE = 0x10000000;
		private const int WM_ERASEBKGND = 0x14;





		#endregion
	}



	/// <summary>
	/// ウィンドウが非アクティブ状態から1回のクリックでボタンが押せる ToolStrip です。
	/// </summary>
	internal class ExtraToolStrip : ToolStrip
	{
		public ExtraToolStrip() : base() { }

		private const uint WM_MOUSEACTIVATE = 0x21;
		private const uint MA_ACTIVATE = 1;
		private const uint MA_ACTIVATEANDEAT = 2;
		private const uint MA_NOACTIVATE = 3;
		private const uint MA_NOACTIVATEANDEAT = 4;

		protected override void WndProc(ref Message m)
		{
			base.WndProc(ref m);

			if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT)
				m.Result = (IntPtr)MA_ACTIVATE;
		}
	}

}

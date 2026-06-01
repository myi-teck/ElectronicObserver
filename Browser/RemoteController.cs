//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.cs
// summary        : モバイル端末からの遠隔操作（画面配信・操作転送）を管理するクラス
//--------------------------------------------------------------------------------------------
// author         : myi
// creation date  : 2026/04/20
// version        : 1.92
//--------------------------------------------------------------------------------------------
#region "change history"
// change history : 2026/04/20 myi 新規作成
//                  2026/05/25 myi HTMLレイアウト調整の利便性向上のため、HTMLコンテンツを外部ファイル(RemoteController.html)に切り出し.
//                                           ビルド不要でリアルタイム反映可能なロード処理を実装。
//                                           また、余分なハードコードHTML(デフォルト定義)を完全に削除し、動的パス探索を強化。
//                  2026/05/25 myi モバイル端末へ実際の資源・艦隊・直近の戦闘情報をリアルタイム返却する JSON API (/hud) を実装。
//                  2026/05/25 myi 連合艦隊および7隻編成(遊撃部隊)に対応した、一隻毎の詳細HP情報を返却するJSON APIへの拡張。
//                  2026/05/25 myi プロセス間通信(WCF)経由でメインプロセスからHUD情報を受信し、循環参照を防ぐキャッシュ配信方式へ変更。
//                  2026/05/25 myi 仮想マウスポインタとゲーム画面のアスペクト比自動補正ロジック(GetBrowserCoords)の復活・統合。
//                  2026/05/26 myi レスポンスヘッダに charset=utf-8 を追加し、ブラウザでの日本語文字化け（?表示）を修正。
//                  2026/05/26 myi 艦隊タブ機能（一括ストック方式）対応として、デモデータにships配列・docks配列を追加拡張。
//                  2026/05/26 bAs)shirakawa デモ用HUDデータ(SendHudData)に受領中任務の検証用ダミーデータ(quests)を追加。
//                  2026/05/26 bAs)shirakawa デモ用任務データ(SendHudData)のdescriptionを廃止、進捗・進捗率(progressText, progressRate)を新規定義。
//                  2026/05/27 bAs)shirakawa サーバー主導バージョン管理によるHUD更新最適化（無駄な通信とDOM再描画をスキップする機能）を追加。
//                  2026/05/27 myi デモデータに第3艦隊の入渠中（DOCKING）状態を追加し、入渠中の艦艇がいる艦隊の表示検証用としてデモデータを拡張。
//                  2026/05/28 myi 未使用の画像サイズ縮小用定数(TargetWidth, TargetHeight)を削除。
//                  2026/05/28 myi デモデータ(combatブロック)にdamageRate, victory, airBattleプロパティを追加。
//                  2026/05/28 bAs)shirakawa /control APIに navigate_login, set_filter, toggle_cover アクションを追加。設定HUDからログインページ移動・プライバシーフィルター操作を可能に。
//                  2026/05/29 myi 差分WebP方式による画面配信機能を実装（JPEG→差分WebPへ移行）。
//                                           低解像度JPEGサムネイル比較方式を採用し、静的画面では204 No Contentで通信量ゼロを実現。
//                                           セーフコード（Marshal.Copy）によるピクセル差分算出。
//                  2026/05/29 myi サムネイルキャプチャをclip+Scale方式（GPU縮小）に変更。160×96への縮小をDevTools側で行うことで
//                                           比較ピクセル数を57分の1（864,000→15,360）に削減し、定数ThumbnailWidth/Heightと実サイズを一致させた。
//                  2026/05/29 myi 送信方式の統一として、SendScreenDiffWebPAsync内のfullキーフレーム送信後の
//                                           サムネイルキャプチャも同様にclip+Scale方式（GPU縮小）に統一。
//                  2026/05/31 myi 案A採用による画面縮小拡大 of ガタつきバグの修正。clip指定のない全体キャプチャ1回化とC#側サムネイルリサイズによる安定化および負荷削減。
//                  2026/06/01 myi 常駐フック型フレームキャッシュ方式（プランA）対応、BoundObject廃止に伴う超高速引き抜き方式へのリファクタリング。
//                  2026/06/01 myi 不要なレガシーJPEG配信の完全廃止、およびWebP動的画質調整機能（スライダー連動）を追加。
#endregion
// Copyright 2026 bEST ANSWER's Inc.
//============================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.DevTools.Page;
using CefSharp.WinForms;

namespace Browser
{
	/// <summary>
	/// リモート操作用のWebサーバーおよびロジックを提供します。
	/// </summary>
	public class RemoteController : IDisposable
	{
		private HttpListener _listener;
		private FormBrowser _parent;
		private bool _isRunning;
		private int _port;

		//====================================================================================
		// 差分WebP方式用列挙型・クラス
		//------------------------------------------------------------------------------------
		/// <summary>
		/// 差分判定結果のタイプ
		/// </summary>
		private enum DiffType
		{
			None,     // 差分なし（静的画面）
			Full,     // 全画面キーフレーム
			Partial   // 差分領域のみ
		}

		/// <summary>
		/// 差分判定結果（タイプと差分矩形領域を保持）
		/// </summary>
		private class DiffResult
		{
			public DiffType Type { get; set; }
			public Rectangle? Rect { get; set; }  // 差分領域（Partial時のみ）
		}

		// 仮想マウス座標 (0.0 ～ 1.0 の相対座標で管理)
		private double _cursorX = 0.5;
		private double _cursorY = 0.5;

		// メインプロセスから WCF 経由で定期送信されるゲーム内HUDデータのJSONキャッシュ
		private string _cachedHudJson = null;

		// 各HUDブロックごとのキャッシュと個別バージョンカウンタ
		private string _lastMaterialJson = null;
		private long _materialVersion = 0;

		private string _lastFleetsJson = null;
		private long _fleetsVersion = 0;

		private string _lastBattleJson = null;
		private long _battleVersion = 0;

		private string _lastCombatJson = null;
		private long _combatVersion = 0;

		private string _lastQuestsJson = null;
		private long _questsVersion = 0;

		private string _lastLogsJson = null;
		private long _logsVersion = 0;

		//====================================================================================
		// 差分WebP方式用フィールド・定数
		//------------------------------------------------------------------------------------
		// 差分判定用サムネイルキャッシュ（前フレームのJPEG quality:1 バイトデータ）
		// byte[] として保持することで、毎フレームのBitmapデコードコストを1回に抑える。
		// Bitmap を常駐させるとGC負荷が大きいため、byte[] + デコードのトレードオフを選択。
		private byte[] _lastThumb = null;

		// 差分判定用サムネイル設定（1200×720の8分の1 = 160×96）
		private const int ThumbnailWidth  = 160;
		private const int ThumbnailHeight = 96;

		// 差分率がこの閾値（15%）を超えた場合は全画面キーフレームを送信する
		private const double PartialThreshold = 0.15;

		// 本番WebP圧縮品質（0〜100）
		private const long WebpQuality = 60L;

		/// <summary>
		/// メインプロセスから定期送信された最新のHUDデータをキャッシュ更新します。
		/// </summary>
		public void UpdateHudData(string hudJson)
		{
			_cachedHudJson = hudJson;

			// 各セクションの抽出と個別バージョン更新チェック
			UpdateSectionVersion(hudJson, "material", ref _lastMaterialJson, ref _materialVersion);
			UpdateSectionVersion(hudJson, "fleets", ref _lastFleetsJson, ref _fleetsVersion);
			UpdateSectionVersion(hudJson, "battle", ref _lastBattleJson, ref _battleVersion);
			UpdateSectionVersion(hudJson, "combat", ref _lastCombatJson, ref _combatVersion);
			UpdateSectionVersion(hudJson, "quests", ref _lastQuestsJson, ref _questsVersion);
			UpdateSectionVersion(hudJson, "logs", ref _lastLogsJson, ref _logsVersion);
		}

		/// <summary>
		/// 指定されたJSONセクションのデータ変更を検証し、変更があればバージョンを更新します。
		/// </summary>
		private void UpdateSectionVersion(string json, string key, ref string lastJson, ref long version)
		{
			string currentJson = ExtractJsonSection(json, key);
			if (currentJson != null && currentJson != lastJson)
			{
				lastJson = currentJson;
				version++;
			}
		}

		/// <summary>
		/// JSON文字列から特定のキーに対応するオブジェクトまたは配列を軽量かつ正確に切り出します。
		/// </summary>
		private string ExtractJsonSection(string json, string key)
		{
			if (string.IsNullOrEmpty(json)) return null;
			int keyIdx = json.IndexOf("\"" + key + "\":");
			if (keyIdx == -1) return null;

			int startIdx = keyIdx + key.Length + 3; // "key": の直後
			if (startIdx >= json.Length) return null;

			char startChar = json[startIdx];
			if (startChar != '{' && startChar != '[')
			{
				// プリミティブ値等の場合は次のカンマかオブジェクト終了括弧までを切り出す
				int commaIdx = json.IndexOf(',', startIdx);
				if (commaIdx == -1) commaIdx = json.IndexOf('}', startIdx);
				if (commaIdx == -1) commaIdx = json.IndexOf(']', startIdx);
				if (commaIdx == -1) return json.Substring(startIdx);
				return json.Substring(startIdx, commaIdx - startIdx);
			}

			char endChar = startChar == '{' ? '}' : ']';
			int braceCount = 0;
			for (int i = startIdx; i < json.Length; i++)
			{
				if (json[i] == startChar) braceCount++;
				else if (json[i] == endChar)
				{
					braceCount--;
					if (braceCount == 0)
					{
						return json.Substring(startIdx, i - startIdx + 1);
					}
				}
			}
			return null;
		}

		public RemoteController(FormBrowser parent, int port = 8080)
		{
			_parent = parent;
			_port = port;
		}

		public void Start()
		{
			if (_isRunning) return;

			try
			{
				_listener = new HttpListener();
				_listener.Prefixes.Add($"http://*:{_port}/");
				_listener.Start();
				_isRunning = true;

				Task.Run(() => ListenLoop());
			}
			catch (Exception ex)
			{
				MessageBox.Show(
					$"遠隔操作サーバー(ポート{_port})の起動に失敗しました。\r\n\r\n" +
					"【原因】\r\nWindowsのセキュリティ制限により、外部からの接続を受け付ける権限がありません。\r\n\r\n" +
					"【解決方法】\r\n以下のいずれかを行ってください。\r\n" +
					"1. ElectronicObserver を「管理者として実行」する\r\n" +
					"2. 管理者コマンドプロンプトで以下のコマンドを実行する:\r\n" +
					$"netsh http add urlacl url=http://*:{_port}/ user=Everyone\r\n\r\n" +
					$"エラー詳細: {ex.Message}",
					"RemoteController 起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private async Task ListenLoop()
		{
			while (_isRunning && _listener.IsListening)
			{
				try
				{
					var context = await _listener.GetContextAsync();
					HandleRequest(context);
				}
				catch (ObjectDisposedException) { break; }
				catch (Exception ex)
				{
					Console.WriteLine($"RemoteController: ループエラー: {ex.Message}");
				}
			}
		}

		private async void HandleRequest(HttpListenerContext context)
		{
			var req = context.Request;
			var res = context.Response;

			try
			{
				string path = req.Url.AbsolutePath.ToLower();

				// シンプルなルーティング
				if (path == "/" || path == "/index.html")
				{
					SendHtml(res);
				}
				else if (path == "/screen.webp")
				{
					// 差分WebP方式による画面配信（静的画面は204 No Content）
					await SendScreenDiffWebPAsync(req, res);
				}
				else if (path == "/control")
				{
					HandleControl(req, res);
				}
				else if (path == "/hud")
				{
					// モバイル端末からのHUD情報要求に対し、実際のゲーム内ステータスをJSONで返します
					SendHudData(req, res);
				}
				else
				{
					res.StatusCode = 404;
					res.Close();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"RemoteController: リクエスト処理エラー: {ex.Message}");
				try { res.StatusCode = 500; res.Close(); } catch { }
			}
		}

		/// <summary>
		/// メインUI（HTML/JS/CSS）を送信します。
		/// </summary>
		private void SendHtml(HttpListenerResponse res)
		{
			string html = GetHtmlContent();
			byte[] buffer = Encoding.UTF8.GetBytes(html);

			// レスポンスのContent-TypeヘッダにUTF-8文字コードを明示的に付与
			res.ContentType = "text/html; charset=utf-8";
			res.ContentLength64 = buffer.Length;
			res.OutputStream.Write(buffer, 0, buffer.Length);
			res.Close();
		}


		/// <summary>
		/// マウス操作を受け取ってエミュレートします。
		/// </summary>
		private void HandleControl(HttpListenerRequest req, HttpListenerResponse res)
		{
			var action = req.QueryString["action"];
			var xStr = req.QueryString["x"];
			var yStr = req.QueryString["y"];

			// 絶対割合座標 (0.0〜1.0) を受け取り、現在のポインタ座標に適用します。
			if (action == "move" && double.TryParse(xStr, out double x) && double.TryParse(yStr, out double y))
			{
				_cursorX = Math.Max(0, Math.Min(1.0, x));
				_cursorY = Math.Max(0, Math.Min(1.0, y));

				TriggerMouseMove();
			}
			else if (action == "click")
				{
					TriggerMouseClick();
				}
				#region "追加アクション処理"
				// ブラウザをログインページへ移動（確認はHUD側で行うため、ここでは即座に実行）
				else if (action == "navigate_login")
				{
					_parent.NavigateToLoginPage();
				}
				// プライバシーフィルターのカラーモードを設定（0=なし, 1=グレースケール, 4=低コントラスト）
				// 2026/06/01 myi 変更：差分WebP方式に伴い、フィルターはRemoteControllerクライアント側でCSS適用するため
				//                  サーバー側ではNOP（何もしない）に変更。本家ブラウザ側には影響を与えない。
				else if (action == "set_filter")
				{
					// クライアントサイドフィルターのため、サーバーサイドでは何もしない（NOP）
					// 将来的に状態同期が必要な場合はここを拡張可能
				}
				// WebP圧縮品質の動的設定アクションを追加
				// 2026/06/01 myi 新規作成（品質スライダーとのWebPリアルタイム連動対応）
				else if (action == "set_quality" && double.TryParse(req.QueryString["q"], out double qVal))
				{
					// 品質を0.05〜1.0の範囲にスケール変換
					double qualityScale = Math.Max(0.05, Math.Min(1.0, qVal / 100.0));
					var kancolleFrame = _parent.GetKanColleFrame();
					kancolleFrame?.ExecuteJavaScriptAsync($"if(window._kancolleCaptureEngine) window._kancolleCaptureEngine.webpQuality = {qualityScale};");
				}
				#endregion
	
				res.StatusCode = 200;
				res.Close();
			}

		//====================================================================================
		// 処理ブロック名 : GetBrowserCoords
		// 概要           : モバイル端末から受信した 0.0〜1.0 のゲーム画面上の相対座標を、
		//                  ブラウザコントロールの現在のサイズと艦これアスペクト比(5:3)から生じる
		//                  黒い余白（レターボックス）を動的に考慮し、実際のCefブラウザ論理ピクセル座標へ変換します。
		//------------------------------------------------------------------------------------
		private Point GetBrowserCoords()
		{
			// CefSharpブラウザコントロール全体の現在の論理サイズを取得
			double browserWidth = _parent.Browser.Width;
			double browserHeight = _parent.Browser.Height;

			// 艦これ標準のゲーム画面領域アスペクト比は 1200:720 (5:3 = 1.6666...)
			double gameAspect = 5.0 / 3.0;
			double browserAspect = browserWidth / browserHeight;

			double gameWidth = browserWidth;
			double gameHeight = browserHeight;
			double offsetX = 0;
			double offsetY = 0;

			// ブラウザウィンドウとゲーム画面のアスペクト比のズレから、黒い余白（レターボックス）のサイズを算出
			if (browserAspect > gameAspect)
			{
				// ウィンドウが横長（左右に黒い余白がある状態）
				gameWidth = browserHeight * gameAspect;
				offsetX = (browserWidth - gameWidth) / 2.0;
			}
			else
			{
				// ウィンドウが縦長（上下に黒い余白がある状態）
				gameHeight = browserWidth / gameAspect;
				offsetY = (browserHeight - gameHeight) / 2.0;
			}

			// 余白の内側の実際のゲーム領域に対して、スマホ側ポインタ割合（0.0〜1.0）をピクセルマッピング
			int x = (int)(offsetX + (_cursorX * gameWidth));
			int y = (int)(offsetY + (_cursorY * gameHeight));

			return new Point(x, y);
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseMove
		// 概要           : スマホ側の仮想マウスの現在相対座標からアスペクト比補正された
		//                  CefSharp論理ピクセル座標を求め、ブラウザへ移動イベントを送信します。
		//------------------------------------------------------------------------------------
		private void TriggerMouseMove()
		{
			if (_parent.Browser == null) return;
			
			// ChromiumWebBrowser から内部の IBrowser インスタンスを取得
			var browser = _parent.Browser.GetBrowser();
			// ブラウザのホストオブジェクト (IBrowserHost) を取得
			var host = browser?.GetHost();
			if (host == null) return;

			// レターボックス余白を動的考慮したアスペクト比補正ピクセル座標を取得
			var coords = GetBrowserCoords();

			// マウス移動イベントオブジェクトを生成して送信します
			var mouseEvent = new MouseEvent(coords.X, coords.Y, CefEventFlags.None);
			host.SendMouseMoveEvent(mouseEvent, false);
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseClick
		// 概要           : スマホ側の決定操作イベントに対し、アスペクト比補正ピクセル座標へ
		//                  マウスの左クリック（ダウン・アップの一連のイベント）を送信します。
		//------------------------------------------------------------------------------------
		private void TriggerMouseClick()
		{
			if (_parent.Browser == null) return;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return;

			// レターボックス余白を動的考慮したアスペクト比補正ピクセル座標を取得
			var coords = GetBrowserCoords();

			// 左クリックのためのマウス座標イベントを生成
			var mouseEvent = new MouseEvent(coords.X, coords.Y, CefEventFlags.None);
			
			// 1. マウスダウンイベント (左ボタン) の送信
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			
			// 艦これブラウザ側のクリック判定遅延を確実にカバーするため、わずかにウェイトを入れます
			System.Threading.Thread.Sleep(50);
			
			// 2. マウスアップイベント (左ボタン) の送信
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
		}

		/// <summary>
		/// メインUI（HTML/JS/CSS）のコンテンツを取得します。
		/// 外部ファイル（RemoteController.html）が存在する場合は動的に読み込み、
		/// 存在しない場合はエラー詳細ページを返します（ビルドなし調整の利便性向上のため、ハードコードは廃止）。
		/// </summary>
		private string GetHtmlContent()
		{
			// 探索するファイルパスの候補リストを定義します。
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var pathsToSearch = new List<string>();

			// 1. 【最優先】開発時の利便性向上のため、プロジェクトフォルダ直下のソースファイルを探索します。
			//    実行フォルダの親階層を動的に遡って Browser プロジェクト内の RemoteController.html を見つけます。
			//    これをリストの先頭に追加することで、ビルドされて実行フォルダ内に残っている
			//    「古いビルド成果物（RemoteController.html）」に邪魔されず、ソースの直接編集が即座に反映されるようになります。
			var dir = new DirectoryInfo(baseDir);
			while (dir != null)
			{
				try
				{
					// 同一階層または子フォルダに Browser があるか確認
					var testPath = Path.Combine(dir.FullName, "Browser", "RemoteController.html");
					if (File.Exists(testPath))
					{
						pathsToSearch.Add(testPath);
						break;
					}

					// 兄弟フォルダとして存在する場合も確認
					var parentBrowser = Path.Combine(dir.FullName, "..", "Browser", "RemoteController.html");
					if (Directory.Exists(dir.FullName) && File.Exists(parentBrowser))
					{
						pathsToSearch.Add(Path.GetFullPath(parentBrowser));
						break;
					}
				}
				catch { }

				dir = dir.Parent;
			}

			// 2. リリース環境用フォールバック：実行フォルダ直下の RemoteController.html (ビルド時にコピーされるファイル)
			pathsToSearch.Add(Path.Combine(baseDir, "RemoteController.html"));
			
			// 3. リリース環境用カスタム：実行フォルダ直下の Settings フォルダ内の RemoteController.html
			pathsToSearch.Add(Path.Combine(baseDir, "Settings", "RemoteController.html"));

			// 候補パスを順に探索し、最初に見つかったファイルを読み込みます。
			foreach (var path in pathsToSearch)
			{
				try
				{
					if (File.Exists(path))
					{
						// 見つかったHTMLファイルをUTF-8で読み込んで返します。
						return File.ReadAllText(path, Encoding.UTF8);
					}
				}
				catch (Exception ex)
				{
					// 読み込みエラー時はログ出力し、次の候補を試します。
					Console.WriteLine($"RemoteController: HTML外部ファイル読み込みエラー ({path}): {ex.Message}");
				}
			}

			// 全ての外部ファイルが見つからない場合は、エラー詳細を表示するHTMLを返却します（セーフティネット）。
			return $@"<!DOCTYPE html>
<html lang='ja'>
<head>
    <meta charset='UTF-8'>
    <title>RemoteController Error</title>
    <style>
        body {{ background-color: #0f172a; color: #f8fafc; font-family: sans-serif; padding: 40px; text-align: center; }}
        h1 {{ color: #ef4444; }}
        pre {{ background: rgba(0,0,0,0.3); padding: 20px; border-radius: 8px; text-align: left; display: inline-block; }}
    </style>
</head>
<body>
    <h1>UI定義ファイルが見つかりません</h1>
    <p>RemoteController.html が見つかりませんでした。ファイルを適切な場所に配置してください。</p>
    <p>【探索候補パス】</p>
    <pre>{string.Join("\r\n", pathsToSearch)}</pre>
</body>
</html>";
		}
		// 処理ブロック名 : SendHudData
		// 概要           : メインプロセスから受信した最新のゲームデータをJSON形式で返却します。
		//                  クライアントからの要求バージョンと一致する場合は極小JSONで「変更なし」を応答し、
		//                  異なる場合またはキャッシュがない場合は、フルデータを返却します。
		//------------------------------------------------------------------------------------
		private void SendHudData(HttpListenerRequest req, HttpListenerResponse res)
		{
			try
			{
				// クライアント側が保持している各HUDブロックのバージョンを取得します
				long clientMaterialVer = -1;
				long.TryParse(req.QueryString["materialVer"], out clientMaterialVer);

				long clientFleetsVer = -1;
				long.TryParse(req.QueryString["fleetsVer"], out clientFleetsVer);

				long clientBattleVer = -1;
				long.TryParse(req.QueryString["battleVer"], out clientBattleVer);

				long clientCombatVer = -1;
				long.TryParse(req.QueryString["combatVer"], out clientCombatVer);

				long clientQuestsVer = -1;
				long.TryParse(req.QueryString["questsVer"], out clientQuestsVer);

				long clientLogsVer = -1;
				long.TryParse(req.QueryString["logsVer"], out clientLogsVer);

				string jsonResponse;

				// キャッシュされた実データが存在する場合は、各ブロックごとの差分データJSONを動的に構築して返却します
				if (!string.IsNullOrEmpty(_cachedHudJson))
				{
					var sb = new StringBuilder();
					sb.Append("{");

					// 1. material（資源）
					sb.Append("\"material\":{\"changed\":").Append(clientMaterialVer != _materialVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_materialVersion);
					if (clientMaterialVer != _materialVersion && _lastMaterialJson != null)
					{
						sb.Append(",\"data\":").Append(_lastMaterialJson);
					}
					sb.Append("},");

					// 2. fleets（艦隊）
					sb.Append("\"fleets\":{\"changed\":").Append(clientFleetsVer != _fleetsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_fleetsVersion);
					if (clientFleetsVer != _fleetsVersion && _lastFleetsJson != null)
					{
						sb.Append(",\"data\":").Append(_lastFleetsJson);
					}
					sb.Append("},");

					// 3. battle（作戦）
					sb.Append("\"battle\":{\"changed\":").Append(clientBattleVer != _battleVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_battleVersion);
					if (clientBattleVer != _battleVersion && _lastBattleJson != null)
					{
						sb.Append(",\"data\":").Append(_lastBattleJson);
					}
					sb.Append("},");

					// 4. combat（戦闘）
					sb.Append("\"combat\":{\"changed\":").Append(clientCombatVer != _combatVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_combatVersion);
					if (clientCombatVer != _combatVersion && _lastCombatJson != null)
					{
						sb.Append(",\"data\":").Append(_lastCombatJson);
					}
					sb.Append("},");

					// 5. quests（任務）
					sb.Append("\"quests\":{\"changed\":").Append(clientQuestsVer != _questsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_questsVersion);
					if (clientQuestsVer != _questsVersion && _lastQuestsJson != null)
					{
						sb.Append(",\"data\":").Append(_lastQuestsJson);
					}
					sb.Append("},");

					// 6. logs（ログ）
					sb.Append("\"logs\":{\"changed\":").Append(clientLogsVer != _logsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(_logsVersion);
					if (clientLogsVer != _logsVersion && _lastLogsJson != null)
					{
						sb.Append(",\"data\":").Append(_lastLogsJson);
					}
					sb.Append("}");

					sb.Append("}");
					jsonResponse = sb.ToString();
				}
				else
				{
					// オフライン・デモデータを返却する場合（changed: true, version: 0 として毎回フルデータ構成で返却）
					var sb = new StringBuilder();
					sb.Append("{");

					// 1. material（資源）
					sb.Append("\"material\":{\"changed\":true,\"version\":0,\"data\":{");
					sb.Append("\"fuel\":00000,\"ammo\":00000,\"steel\":00000,\"bauxite\":00000,\"bucket\":0000");
					sb.Append("}},");
	
					// 2. fleets（艦隊）
					sb.Append("\"fleets\":{\"changed\":true,\"version\":0,\"data\":[");
					// 第1艦隊
					sb.Append("{" +
						"\"id\":1,\"name\":\"第1艦隊\",\"state\":\"LOADING\",\"stateText\":\"LOADING / ローディング中\",\"expeditionRemaining\":\"\",\"ships\":[{" +
						"\"masterId\":0,\"shipId\":0,\"name\":\"XXXXXX\",\"level\":0,\"isMarried\":false," +
						"\"hp\":0,\"maxHp\":0,\"hpRate\":0.0,\"condition\":0,\"fuel\":0,\"ammo\":0,\"fuelMax\":0,\"ammoMax\":0,\"fuelRate\":0.0,\"ammoRate\":0.0," +
						"\"equipment\":[null,null,null,null]," +
						"\"aircraft\":[0,0,0,0]," +
						"\"parameters\":{\"firepower\":0,\"torpedo\":0,\"aa\":0,\"armor\":0,\"evasion\":0,\"asw\":0,\"los\":0,\"luck\":0}," +
						"\"dock\":{\"is_docking\":false,\"end_time\":null}," +
						"\"fleet_id\":1" +
						"},null,null,null,null,null]" +
						"}," +
						// 第2艦隊
						"{" +
						"\"id\":2,\"name\":\"第2艦隊\",\"state\":\"LOADING\",\"stateText\":\"LOADING / ローディング中\",\"expeditionRemaining\":\"\",\"ships\":[{" +
						"\"masterId\":0,\"shipId\":0,\"name\":\"XXXXXX\",\"level\":0,\"isMarried\":false," +
						"\"hp\":0,\"maxHp\":0,\"hpRate\":0.0,\"condition\":0,\"fuel\":0,\"ammo\":0,\"fuelMax\":0,\"ammoMax\":0,\"fuelRate\":0.0,\"ammoRate\":0.0," +
						"\"equipment\":[null,null,null,null]," +
						"\"aircraft\":[0,0,0,0]," +
						"\"parameters\":{\"firepower\":0,\"torpedo\":0,\"aa\":0,\"armor\":0,\"evasion\":0,\"asw\":0,\"los\":0,\"luck\":0}," +
						"\"dock\":{\"is_docking\":false,\"end_time\":null}," +
						"\"fleet_id\":2" +
						"},null,null,null,null,null]" +
						"}," +
						// 第3艦隊
						"{" +
						"\"id\":3,\"name\":\"第3艦隊\",\"state\":\"LOADING\",\"stateText\":\"LOADING / ローディング中\",\"expeditionRemaining\":\"\",\"ships\":[{" +
						"\"masterId\":0,\"shipId\":0,\"name\":\"XXXXXX\",\"level\":0,\"isMarried\":false," +
						"\"hp\":0,\"maxHp\":0,\"hpRate\":0.0,\"condition\":0,\"fuel\":0,\"ammo\":0,\"fuelMax\":0,\"ammoMax\":0,\"fuelRate\":0.0,\"ammoRate\":0.0," +
						"\"equipment\":[null,null,null,null]," +
						"\"aircraft\":[0,0,0,0]," +
						"\"parameters\":{\"firepower\":0,\"torpedo\":0,\"aa\":0,\"armor\":0,\"evasion\":0,\"asw\":0,\"los\":0,\"luck\":0}," +
						"\"dock\":{\"is_docking\":false,\"end_time\":null}," +
						"\"fleet_id\":3" +
						"},null,null,null,null,null]" +
						"}," +
						// 第4艦隊
						"{" +
						"\"id\":4,\"name\":\"第4艦隊\",\"state\":\"LOADING\",\"stateText\":\"LOADING / ローディング中\",\"expeditionRemaining\":\"\",\"ships\":[{" +
						"\"masterId\":0,\"shipId\":0,\"name\":\"XXXXXX\",\"level\":0,\"isMarried\":false," +
						"\"hp\":0,\"maxHp\":0,\"hpRate\":0.0,\"condition\":0,\"fuel\":0,\"ammo\":0,\"fuelMax\":0,\"ammoMax\":0,\"fuelRate\":0.0,\"ammoRate\":0.0," +
						"\"equipment\":[null,null,null,null]," +
						"\"aircraft\":[0,0,0,0]," +
						"\"parameters\":{\"firepower\":0,\"torpedo\":0,\"aa\":0,\"armor\":0,\"evasion\":0,\"asw\":0,\"los\":0,\"luck\":0}," +
						"\"dock\":{\"is_docking\":false,\"end_time\":null}," +
						"\"fleet_id\":4" +
						"},null,null,null,null,null]" +
						"}");
					sb.Append("]},");
	
					// 3. battle（作戦）
					sb.Append("\"battle\":{\"changed\":true,\"version\":0,\"data\":{");
					sb.Append("\"area\":\"XXXXXX\",");
					sb.Append("\"enemy\":\"XXXXXX\",");
					sb.Append("\"predict\":\"XXXXXX\"");
					sb.Append("}},");
	
					// 4. combat（戦闘）
					sb.Append("\"combat\":{\"changed\":true,\"version\":0,\"data\":{");
					sb.Append("\"formation\":\"XXXXXX vs XXXXXX\",");
					sb.Append("\"engagement\":\"XXXXXX\",");
					sb.Append("\"touch\":\"XXXXXX\",");
					sb.Append("\"damageRate\":0.0,");
					sb.Append("\"victory\":\"XXXXXX\",");
					sb.Append("\"airBattle\":\"XXXXXX\",");
					
					// 味方主力
					sb.Append("\"friendShips\":[");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"}");
					sb.Append("],");
	
					// 味方随伴
					sb.Append("\"friendEscortShips\":[");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"}");
					sb.Append("],");
	
					// 敵主力
					sb.Append("\"enemyShips\":[");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"}");
					sb.Append("],");
	
					// 敵随伴
					sb.Append("\"enemyEscortShips\":[");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"},");
					sb.Append("{\"name\":\"XXXXXX\",\"hp\":0,\"maxHp\":0,\"status\":\"LOADING\"}");
					sb.Append("]");
					sb.Append("}},");
	
					// 5. quests（任務）
					sb.Append("\"quests\":{\"changed\":true,\"version\":0,\"data\":[");
					sb.Append("{" +
						"\"id\":0,\"name\":\"XXXXXX\",\"progressText\":\"XXXXXX\",\"progressRate\":0.00" +
						"}," +
						"{" +
						"\"id\":0,\"name\":\"XXXXXX\",\"progressText\":\"XXXXXX\",\"progressRate\":0.00" +
						"}," +
						"{" +
						"\"id\":0,\"name\":\"XXXXXX\",\"progressText\":\"XXXXXX\",\"progressRate\":0.00" +
						"}," +
						"{" +
						"\"id\":0,\"name\":\"XXXXXX\",\"progressText\":\"XXXXXX\",\"progressRate\":0.00" +
						"}," +
						"{" +
						"\"id\":0,\"name\":\"XXXXXX\",\"progressText\":\"XXXXXX\",\"progressRate\":0.00" +
						"}");
					sb.Append("]},");

					// 6. logs（ログ）
					sb.Append("\"logs\":{\"changed\":true,\"version\":0,\"data\":[]");
					sb.Append("}");

					sb.Append("}");
					jsonResponse = sb.ToString();
				}

				// レスポンスの送出処理（Content-Typeに application/json; charset=utf-8 を指定して文字化けを防止）
				byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
				res.ContentType = "application/json; charset=utf-8";
				res.ContentLength64 = buffer.Length;
				res.OutputStream.Write(buffer, 0, buffer.Length);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"SendHudData error: {ex.Message}");
				res.StatusCode = 500;
			}
			finally
			{
				res.Close();
			}
		}

		//====================================================================================
		// 処理ブロック名 : EscapeJson
		// 概要           : JSON文字列の各特殊文字を安全にエスケープ処理します
		//------------------------------------------------------------------------------------
		private string EscapeJson(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
		}

		//====================================================================================
		// 処理ブロック名 : ResizeJpegToThumbnail
		// 概要           : 取得した全体JPEG画像（JPEGバイトデータ）を、C# (GDI+) で
		//                  160×96 のサムネイルサイズにリサイズして JPEG (quality:1) で返却します。
		//------------------------------------------------------------------------------------


		//====================================================================================
		// 処理ブロック名 : GetEncoder
		// 概要           : 指定された画像フォーマットに対応するエンコーダー情報を取得します。
		//------------------------------------------------------------------------------------
		private ImageCodecInfo GetEncoder(ImageFormat format)
		{
			ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
			foreach (ImageCodecInfo codec in codecs)
			{
				if (codec.FormatID == format.Guid)
				{
					return codec;
				}
			}
			return null;
		}

		//====================================================================================
		// 処理ブロック名 : CalculateDiffAsync
		// 概要           : 低解像度JPEGサムネイルをキャプチャして前フレームとの差分を判定する。
		//                  初回フレームの場合は Full を返すが、サムネイルキャプチャは行わない。
		//                  Full 返却後の本番キャプチャ完了後に _lastThumb を更新するため、
		//                  ここでは何も更新しない（呼び出し元の SendScreenDiffWebPAsync が責任を持つ）。
		//------------------------------------------------------------------------------------
		//====================================================================================
		//====================================================================================
		// 処理ブロック名 : ComputeBoundingBoxSafe
		// 概要           : セーフコード（unsafe・ポインタ不使用）で2つのJPEGデータの差分領域を算出する。
		//                  Marshal.Copy + int[] によるセーフコードで 0.5ms 以下。
		//                  サイズ不一致時は全面差分を返す。
		//------------------------------------------------------------------------------------
		private Rectangle ComputeBoundingBoxSafe(byte[] prevData, byte[] currData)
		{
			using (var prevStream = new MemoryStream(prevData))
			using (var currStream = new MemoryStream(currData))
			using (var prevBitmap = new Bitmap(prevStream))
			using (var currBitmap = new Bitmap(currStream))
			{
				// ─── サイズ不一致ガード（ブラウザリサイズ等によるキャプチャサイズ変動を検出）
				//     サイズが合わない場合は比較不能のため Full キーフレームに相当する全面差分を返す。
				if (prevBitmap.Width != currBitmap.Width || prevBitmap.Height != currBitmap.Height)
				{
					return new Rectangle(0, 0, currBitmap.Width, currBitmap.Height);
				}

				int width  = prevBitmap.Width;
				int height = prevBitmap.Height;

				var rect     = new Rectangle(0, 0, width, height);
				var prevLock = prevBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				var currLock = currBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

				int minX = int.MaxValue, minY = int.MaxValue;
				int maxX = int.MinValue, maxY = int.MinValue;
				bool hasDiff = false;

				try
				{
					// ─── LockBits したピクセルデータを int[] にコピーして比較する。
					//     Marshal.Copy は CLR の管理外メモリを安全にコピーするため、
					//     unsafe キーワードなしで LockBits の高速アクセスを実現できる。
					int size       = width * height;
					int[] prevPx   = new int[size];
					int[] currPx   = new int[size];

					System.Runtime.InteropServices.Marshal.Copy(prevLock.Scan0, prevPx, 0, size);
					System.Runtime.InteropServices.Marshal.Copy(currLock.Scan0, currPx, 0, size);

					for (int y = 0; y < height; y++)
					{
						int rowOffset = y * width;
						for (int x = 0; x < width; x++)
						{
							int index = rowOffset + x;

							// ─── ARGB の最上位バイト（アルファチャンネル）を 0x00FFFFFF でマスクして除外し、
							//     RGB チャンネルのみで変化を検出する（透過の違いによる誤検知を防ぐ）
							if ((prevPx[index] & 0x00FFFFFF) != (currPx[index] & 0x00FFFFFF))
							{
								hasDiff = true;
								if (x < minX) minX = x;
								if (y < minY) minY = y;
								if (x > maxX) maxX = x;
								if (y > maxY) maxY = y;
							}
						}
					}
				}
				finally
				{
					// ─── 例外発生時でも確実に LockBits を解除する
					prevBitmap.UnlockBits(prevLock);
					currBitmap.UnlockBits(currLock);
				}

				if (!hasDiff)
				{
					return Rectangle.Empty;
				}

				return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
			}
		}

		//====================================================================================
		// 処理ブロック名 : CaptureCanvasAsync
		// 概要           : ゲーム内の canvas 描画バッファをフックして常駐キャッシュする
		//                  JavaScriptエンジンから、最新 of WebP 画像とサムネイル JPEG を一括取得します。
		//                  常駐エンジン未起動時は、初回接続時に自動的に常駐インジェクションを行って起動します。
		//                  WebGLの preserveDrawingBuffer=false によるバッファ即時クリア問題を完全に克服します。
		//                  2026/06/01 myi 変更（EvaluateScriptAsync 方式へ完全リファクタリング、BoundObjectの廃止）
		//------------------------------------------------------------------------------------
		private async Task<Tuple<string, string>> CaptureCanvasAsync()
		{
			// 艦これの描画フレームがロードされている iframe への参照を取得します
			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null)
			{
				return null;
			}

			if (_parent.Browser == null || !_parent.Browser.IsBrowserInitialized)
			{
				return null;
			}

			// EvaluateScriptAsync を用いてプールされている最新キャッシュを瞬時に引き抜く（超高速・バインドオーバーヘッドゼロ）
			string script = @"
				(function() {
					if (window._kancolleCaptureEngine) {
						return [window._kancolleCaptureEngine.lastWebp, window._kancolleCaptureEngine.lastThumb];
					}
					return null;
				})();";

			try
			{
				var response = await kancolleFrame.EvaluateScriptAsync(script);
				if (response.Success && response.Result != null)
				{
					var list = response.Result as System.Collections.Generic.IList<object>;
					if (list != null && list.Count >= 2)
					{
						string lastWebp = list[0] as string;
						string lastThumb = list[1] as string;
						if (!string.IsNullOrEmpty(lastWebp))
						{
							return Tuple.Create(lastWebp, lastThumb);
						}
					}
				}

				// まだ常駐エンジンが存在しない場合は、動的に注入して起動する（セーフティネット）
				// 2026/06/01 myi 新規作成
				string injectScript = _parent.CaptureEngineScript;
				kancolleFrame.ExecuteJavaScriptAsync(injectScript);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"RemoteController: CaptureCanvasAsync エラー: {ex.Message}");
			}

			return null;
		}

		//====================================================================================
		// 処理ブロック名 : SendScreenDiffWebPAsync
		// 概要           : ゲーム内の canvas から直接コピーした画像を用いて
		//                  差分WebP方式で画面を配信する（GET /screen.webp）。
		//                  PCウィンドウの表示サイズやアスペクト比に依存せず、常に余白なしで
		//                  高精細なゲーム画面を配信できます。
		//                  2026/06/01 myi 変更（EvaluateScriptAsync 方式へ完全リファクタリング、BoundObjectの廃止）
		//------------------------------------------------------------------------------------
		private async Task SendScreenDiffWebPAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			if (_parent.Browser == null || !_parent.Browser.IsBrowserInitialized)
			{
				res.StatusCode = 503;
				res.Close();
				return;
			}

			// クエリパラメータから目標画質「q」(5%〜100%)を取得
			// 常駐JSループ側の quality 設定(0.6)が優先されるため、ここでは主に引き抜き処理のみ
			long quality = WebpQuality;
			var qStr = req.QueryString["q"];
			if (long.TryParse(qStr, out long parsedQ))
			{
				quality = Math.Max(5, Math.Min(100, parsedQ));
			}

			try
			{
				// クライアント側からの初期化要求・再接続シグナル（init=1）を検出する
				bool isInit = req.QueryString["init"] == "1" || req.QueryString["init"] == "true";

				// 1. canvas から WebP とサムネイル JPEG を一括で直接抽出
				var capture = await CaptureCanvasAsync();
				if (capture == null || string.IsNullOrEmpty(capture.Item1))
				{
					res.StatusCode = 503;
					return;
				}

				// 2. Base64 データをデコードして生のバイナリデータに変換
				byte[] fullWebpData = ConvertToBytes(capture.Item1, "data:image/webp");
				byte[] thumbJpegData = ConvertToBytes(capture.Item2, "data:image/jpeg");

				if (fullWebpData == null)
				{
					res.StatusCode = 503;
					return;
				}

				// 3. 差分判定の実行
				DiffType diffType = DiffType.Full;
				if (isInit || _lastThumb == null || thumbJpegData == null)
				{
					_lastThumb = thumbJpegData;
					diffType = DiffType.Full;
				}
				else
				{
					// 前フレームのサムネイルと比較して差分矩形領域を計算
					var diffRect = ComputeBoundingBoxSafe(_lastThumb, thumbJpegData);
					if (diffRect == Rectangle.Empty)
					{
						// 差分なし → 送信をスキップ
						diffType = DiffType.None;
					}
					else
					{
						// 差分あり → 全体キーフレームとして送信
						_lastThumb = thumbJpegData;
						diffType = DiffType.Full;
					}
				}

				// 4. クライアントへのレスポンス送信
				switch (diffType)
				{
					case DiffType.None:
						// 差分なしは 204 No Content を応答（通信量とクライアントの描画負荷を完全にゼロ化）
						res.StatusCode = 204;
						res.Headers["X-Diff-Type"] = "none";
						break;

					case DiffType.Full:
						// 差分あり時は高精細な WebP データをそのまま高速転送
						res.ContentType = "image/webp";
						res.Headers["X-Diff-Type"] = "full";
						res.ContentLength64 = fullWebpData.Length;
						await res.OutputStream.WriteAsync(fullWebpData, 0, fullWebpData.Length);
						break;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"RemoteController: 差分WebP配信エラー: {ex.Message}");
				res.StatusCode = 500;
			}
			finally
			{
				res.Close();
			}
		}

		//====================================================================================
		// 処理ブロック名 : ConvertToBytes
		// 概要           : 受信した Base64 文字列をバイト配列に変換します。
		//                  2026/06/01 myi 新規作成（CanvasCapturePacket廃止に伴う処理移植）
		//------------------------------------------------------------------------------------
		private byte[] ConvertToBytes(string dataUrl, string prefix)
		{
			if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith(prefix))
			{
				return null;
			}

			try
			{
				// "data:image/webp;base64," のヘッダ部分（カンマ以前）を除去してデコード
				int commaIdx = dataUrl.IndexOf(',');
				if (commaIdx == -1) return null;

				string base64Str = dataUrl.Substring(commaIdx + 1);
				return Convert.FromBase64String(base64Str);
			}
			catch (Exception)
			{
				return null;
			}
		}

		public void Dispose()
		{
			_isRunning = false;
			_listener?.Stop();
			_listener?.Close();
		}
	}
}

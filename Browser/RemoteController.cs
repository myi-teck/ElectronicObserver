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
//                  2026/05/26 myi デモ用HUDデータ(SendHudData)に受領中任務の検証用ダミーデータ(quests)を追加。
//                  2026/05/26 myi デモ用任務データ(SendHudData)のdescriptionを廃止、進捗・進捗率(progressText, progressRate)を新規定義。
//                  2026/05/27 myi サーバー主導バージョン管理によるHUD更新最適化（無駄な通信とDOM再描画をスキップする機能）を追加。
//                  2026/05/27 myi デモデータに第3艦隊の入渠中（DOCKING）状態を追加し、入渠中の艦艇がいる艦隊の表示検証用としてデモデータを拡張。
//                  2026/05/28 myi 未使用の画像サイズ縮小用定数(TargetWidth, TargetHeight)を削除。
//                  2026/05/28 myi デモデータ(combatブロック)にdamageRate, victory, airBattleプロパティを追加。
//                  2026/05/28 myi /control APIに navigate_login, set_filter, toggle_cover アクションを追加。設定HUDからログインページ移動・プライバシーフィルター操作を可能に。
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
//                  2026/06/02 myi C#側のGDI+差分判定を完全廃止し、ブラウザJS側のオンデマンドキャプチャおよび軽量ピクセル差分判定へ移行。
//                  2026/06/10 myi 遠隔操作サーバー起動時のポート自動登録機能 (UAC昇格対応) の追加。
//                  2026/06/11 myi ゲーム画面（viewport）をタップした位置の絶対座標を直接クリックする機能（touch_clickアクション）を追加。
#endregion
// Copyright 2026 myi
//============================================================================================

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
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
		
		// 2026/06/10 myi 新規作成（リッスン対象のIPアドレス設定値保持用）
		private string _address;

		// 2026/07/14 myi 新規作成（リモート操作サーバーのログインパスワード保持用）
		private string _password;

		// 2026/07/23 追記（パスワード更新処理のロックオブジェクト）
		/// <summary>
		/// パスワード更新および認証チェックのアトミックな処理を保護するロックオブジェクト。
		/// volatileだけでは UpdatePassword 内のセッション破棄とパスワード変更の原子性が
		/// 保証されないため、専用のロックを導入する。
		/// </summary>
		private readonly object _passwordLock = new object();

		// 2026/07/26 myi 新規作成（HUDデータ更新のロックオブジェクト）
		/// <summary>
		/// UpdateHudData と SendHudData 間のHUDキャッシュ（_cachedHudJson, 各バージョン）への
		/// 読み書きを排他制御するためのロックオブジェクト。
		/// </summary>
		private readonly object _hudLock = new object();

		//====================================================================================
		// WebSocket 通信関連フィールド・定数
		//------------------------------------------------------------------------------------
		// 2026/07/27 新規作成（WebSocket セッション管理）
		private readonly ConcurrentDictionary<string, WebSocketSession> _webSocketSessions = new ConcurrentDictionary<string, WebSocketSession>();

		// 2026/07/27 新規作成（バッファプール：GC 負荷削減）
		private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

		// 2026/07/27 新規作成（WebSocket 定数）
		private const int WebSocketHeartbeatIntervalMs = 15000; // 15秒
		private const int WebSocketMaxQueueSize = 1;            // Drop Frame 用キュー上限（最新1枚のみ保持）

		//====================================================================================
		// IPC 最適化 - スクリプト事前構築
		//------------------------------------------------------------------------------------
		// 2026/07/27 新規作成（EvaluateScriptAsync 用スクリプトの事前構築：GC 負荷削減）
		// 高頻度呼び出し（10FPS）の CaptureCanvasAsync で使用するスクリプトを
		// 定数として事前定義し、呼び出し毎の文字列生成によるGC負荷および FormatException を排除する。
		private const string CaptureScriptInit = "(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.pull(true);}})()";
		private const string CaptureScriptNormal = "(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.pull(false);}})()";

		// 2026/07/27 新規作成（set_quality アクション用スクリプトの事前構築）
		// 動的な品質値のみを連結し、固定部分は定数として事前定義する。
		private const string QualityScriptPrefix = "if(window._kancolleCaptureEngine)window._kancolleCaptureEngine.webpQuality=";
		private const char QualityScriptSuffix = ';';

		// 2026/07/27 新規作成（WebSocket Heartbeat 用バイト列の事前構築）
		// 毎回の Encoding.UTF8.GetBytes("heartbeat") による一時バッファ割り当てを排除する。
		private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes("heartbeat");

		// 2026/07/22 myi 新規作成（ブルートフォース対策：IPアドレスごとのログイン失敗追跡）
		private readonly ConcurrentDictionary<string, (int count, DateTime lastAttempt)> _loginAttempts = new ConcurrentDictionary<string, (int count, DateTime lastAttempt)>();
		private const int MaxLoginAttempts = 5;
		private const int LoginBlockMinutes = 15;

		//====================================================================================
		// セッション管理
		//------------------------------------------------------------------------------------
		/// <summary>
		/// セッション情報を保持するクラス
		/// </summary>
		private class SessionInfo
		{
			public string SessionId { get; set; }
			public DateTime CreatedAt { get; set; }
			public DateTime LastAccessedAt { get; set; }

			public SessionInfo(string sessionId)
			{
				SessionId = sessionId;
				CreatedAt = DateTime.UtcNow;
				LastAccessedAt = DateTime.UtcNow;
			}

			public void Touch()
			{
				LastAccessedAt = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// リフレッシュトークンの情報を保持するクラス
		/// </summary>
		private class RefreshTokenInfo
		{
			public string RefreshToken { get; set; }
			public string SessionId { get; set; }      // 関連するセッションID
			public DateTime CreatedAt { get; set; }
			public DateTime ExpiresAt { get; set; }    // 有効期限（発行から30日）

			// 2026/07/22 myi 新規作成（Grace Period: 並行リクエストによるRace Condition対策）
			public DateTime? RevokedAt { get; set; }        // 破棄された日時（null = 未破棄）
			public string ReplacedByToken { get; set; }     // 置き換えられた新トークン

			public RefreshTokenInfo(string refreshToken, string sessionId)
			{
				RefreshToken = refreshToken;
				SessionId = sessionId;
				CreatedAt = DateTime.UtcNow;
				ExpiresAt = DateTime.UtcNow.AddDays(30);  // 30日有効
			}

			public bool IsExpired()
			{
				return DateTime.UtcNow > ExpiresAt;
			}

			// 2026/07/22 myi 新規作成（Grace Period内なら有効とみなす）
			public bool IsValid()
			{
				return !IsExpired() && (RevokedAt == null || DateTime.UtcNow - RevokedAt.Value < TimeSpan.FromSeconds(10));
			}
		}

		// インメモリセッションストア（スレッドセーフ）
		private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new ConcurrentDictionary<string, SessionInfo>();

		// リフレッシュトークンストア（スレッドセーフ）
		private readonly ConcurrentDictionary<string, RefreshTokenInfo> _refreshTokens = new ConcurrentDictionary<string, RefreshTokenInfo>();

		// セッションのアイドルタイムアウト（30分）
		private const int SessionTimeoutMinutes = 30;

		// リフレッシュトークンの有効期間（30日）
		private const int RefreshTokenDays = 30;

		// 2026/07/22 myi 新規作成（クリーンアップ最適化：前回実行時刻を保持）
		private DateTime _lastCleanupTime = DateTime.MinValue;

		//====================================================================================
		// WebSocketSession クラス
		//------------------------------------------------------------------------------------
		/// <summary>
		/// WebSocket 接続セッション情報を保持します。
		/// HttpListener の WebSocket はスレッドセーフではないため、
		/// 送信処理は lock で排他制御し、単一の送信ループタスクで管理します。
		/// </summary>
		private class WebSocketSession : IDisposable
		{
			public System.Net.WebSockets.WebSocket WebSocket { get; }
			public string SessionId { get; }
			public CancellationTokenSource CancellationToken { get; }
			public DateTime LastHeartbeat { get; set; }

			/// <summary>
			/// クライアントが要求するキャプチャ目標間隔（ミリ秒）。
			/// 0 = 無制限 (MAX)、100 = 10FPS、60000 = スリープ (1分に1回)
			/// </summary>
			public volatile int TargetIntervalMs = 100;

			/// <summary>
			/// バッファ操作の排他制御用ロック（SetLatestFrame / GetLatestFrame / ReturnFrame）
			/// </summary>
			private readonly object _bufferLock = new object();
	
			/// <summary>
			/// 送信用の SemaphoreSlim（await 可能な排他制御用。TOCTOU 対策）
			/// </summary>
			public readonly SemaphoreSlim SendSemaphore = new SemaphoreSlim(1, 1);
	
			/// <summary>
			/// 各ループタスクの参照（Dispose 時のクリーンアップ用）
			/// </summary>
			public Task CaptureTask { get; set; }
			public Task SendTask { get; set; }
			public Task ReceiveTask { get; set; }
	
			/// <summary>
			/// Drop Frame 用最新フレームバッファ（ArrayPool からレンタル）
			/// null の場合はフレームなし
			/// </summary>
			private byte[] _latestFrameBuffer;
	
			/// <summary>
			/// 最新フレームの有効データ長
			/// </summary>
			private int _latestFrameLength;
	
			/// <summary>
			/// フレーム更新フラグ（キャプチャタスクが立てる）
			/// </summary>
			private bool _frameUpdated;
	
			/// <summary>
			/// フレーム到着シグナル（非同期 await 対応、OSタイマー解像度制限排除）
			/// </summary>
			private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0, 1);

			public WebSocketSession(System.Net.WebSockets.WebSocket ws, string sessionId)
			{
				WebSocket = ws;
				SessionId = sessionId;
				CancellationToken = new CancellationTokenSource();
				LastHeartbeat = DateTime.UtcNow;
				_latestFrameBuffer = null;
				_latestFrameLength = 0;
			}

			/// <summary>
			/// 最新フレームの到着を非同期待機します（送信タスクから呼び出し）。
			/// 0ms非同期で即座に再開し、OSタイマー制度による10FPS制限を排除する。
			/// </summary>
			public async Task<bool> WaitForFrameAsync(int timeoutMs, CancellationToken token)
			{
				try
				{
					return await _frameSignal.WaitAsync(timeoutMs, token);
				}
				catch (OperationCanceledException)
				{
					return false;
				}
			}

			/// <summary>
			/// 最新フレームをセットします（キャプチャタスクから呼び出し）。
			/// 既存のフレームがある場合は破棄（ArrayPool へ返却）し、新しいフレームで上書き。
			/// 操作全体は _bufferLock で排他制御されておりアトミック。
			/// </summary>
			public void SetLatestFrame(byte[] data)
			{
				byte[] pooledBuffer = _bufferPool.Rent(data.Length);
				Array.Copy(data, pooledBuffer, data.Length);

				lock (_bufferLock)
				{
					// 既存フレームがあれば返却（ArrayPool へ）
					if (_latestFrameBuffer != null)
					{
						_bufferPool.Return(_latestFrameBuffer, clearArray: true);
					}

					_latestFrameBuffer = pooledBuffer;
					_latestFrameLength = data.Length;
					_frameUpdated = true;
				}

				// セマフォが0（待機中）の場合のみReleaseして送信タスクを0ms即時再開
				if (_frameSignal.CurrentCount == 0)
				{
					try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
				}
			}

			/// <summary>
			/// 最新フレームを取得してフラグをリセットします（送信タスクから呼び出し）。
			/// ArrayPool からレンタルされたバッファを ArraySegment としてそのまま返し、
			/// 送信完了後には ReturnFrame で返却することを期待する。
			/// フレームがない場合は default(ArraySegment<byte>) (Count == 0) を返す。
			/// 操作全体は _bufferLock で排他制御されておりアトミック。
			/// </summary>
			public ArraySegment<byte> GetLatestFrame()
			{
				lock (_bufferLock)
				{
					if (!_frameUpdated) return default(ArraySegment<byte>);
					_frameUpdated = false;

					byte[] frame = _latestFrameBuffer;
					int length = _latestFrameLength;

					// 所有権を送信側に完全に移行（null 化して誤返却を防止）
					_latestFrameBuffer = null;
					_latestFrameLength = 0;

					// ArrayPool バッファをそのまま ArraySegment として返す（コピー不要）
					return new ArraySegment<byte>(frame, 0, length);
				}
			}

			/// <summary>
			/// 送信完了後、ArrayPool から借りたフレームバッファを返却します。
			/// </summary>
			public void ReturnFrame(ArraySegment<byte> segment)
			{
				if (segment.Array != null && segment.Count > 0)
				{
					_bufferPool.Return(segment.Array, clearArray: true);
				}
			}

			public void Dispose()
			{
				CancellationToken.Cancel();
				try { _frameSignal.Release(); } catch { } // 待機中の送信タスクを解放

				// 残っているフレームバッファを返却
				lock (_bufferLock)
				{
					if (_latestFrameBuffer != null)
					{
						_bufferPool.Return(_latestFrameBuffer, clearArray: true);
						_latestFrameBuffer = null;
					}
				}

				CancellationToken.Dispose();
				SendSemaphore.Dispose();
				_frameSignal.Dispose();
			}
		}

		//====================================================================================
		// パスワード更新
		//------------------------------------------------------------------------------------
		/// <summary>
		/// リモート操作サーバーのログインパスワードを更新します。
		/// 空文字列を設定すると認証が無効化されます。
		/// パスワード変更時には既存セッション・リフレッシュトークンを全破棄し、
		/// 旧セッションによる不正アクセスを防止します。
		/// </summary>
		/// <param name="newPassword">新しいパスワード（空文字列で認証無効化）</param>
		public void UpdatePassword(string newPassword)
		{
			lock (_passwordLock)
			{
				_password = newPassword;
				// パスワード変更時に既存セッションを全破棄（セッションハイジャック防止）
				_sessions.Clear();
				_refreshTokens.Clear();
			}
		}

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
			lock (_hudLock)
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

		/// <summary>
		/// メインプロセスのログ機能（AddLog）へログメッセージを安全に転送・記録します。
		/// </summary>
		private void Log(int priority, string message)
		{
			try
			{
				Console.WriteLine(message);
				_parent?.AddLog(priority, message);
			}
			catch { }
		}

		/// <summary>
		/// 2026/06/10 myi 新規作成（リッスンアドレスパラメータの追加）
		/// 2026/07/14 myi 変更（パスワードパラメータの追加）
		/// </summary>
		public RemoteController(FormBrowser parent, int port = 8080, string address = "*", string password = "")
		{
			_parent = parent;
			_port = port;
			_address = address;
			_password = password;
		}

		/// <summary>
		/// 遠隔操作サーバーを開始します。設定されたポートおよびリッスンIP（複数可）でHttpListenerを起動します。
		/// </summary>
		public void Start()
		{
			if (_isRunning) return;

			Log(2, $"[RemoteController] Start() 呼び出し: Address={_address}, Port={_port}");

			// エラーメッセージのnetsh案内用コマンドリスト
			var netshCommands = new List<string>();

			try
			{
				_listener = new HttpListener();

				// 2026/06/10 myi 新規作成（複数IPアドレス・IPv6対応のプレフィックス構築とバインド）
				// アドレス設定文字列をセミコロン（;）またはカンマ（,）で分割
				var addresses = (_address ?? "*")
					.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(a => a.Trim())
					.ToList();

				// アドレス指定が何も無い場合はデフォルトとして「*」を追加
				if (addresses.Count == 0)
				{
					addresses.Add("*");
				}

				foreach (var addr in addresses)
				{
					string formattedAddr = addr;

					// IPアドレスかパースを試み、IPv6アドレスであれば大括弧「[...]」でエスケープ
					if (System.Net.IPAddress.TryParse(addr, out System.Net.IPAddress ip))
					{
						if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
						{
							if (!addr.StartsWith("[") && !addr.EndsWith("]"))
							{
								formattedAddr = $"[{addr}]";
							}
						}
					}

					// HttpListener に接続プレフィックスを登録
					_listener.Prefixes.Add($"http://{formattedAddr}:{_port}/");
					
					// 2026/06/10 myi 新規作成（IPv6の netsh 登録エラー50回避のための置き換え案内）
					// netsh http add urlacl は [::] や [::1] などの IPv6 形式を直接指定すると「エラー50：この要求はサポートされていません」になるため、
					// 代替ワイルドカードとして「+」を用いて案内します。
					string netshAddr = formattedAddr;
					if (addr == "::" || addr == "::1" || addr == "[::]" || addr == "[::1]")
					{
						netshAddr = "+";
					}
					netshCommands.Add($"netsh http add urlacl url=http://{netshAddr}:{_port}/ user=Everyone");
				}

				_listener.Start();
				_isRunning = true;
				Log(2, $"[RemoteController] 遠隔操作サーバーが正常に起動しました: http://{_address}:{_port}/");

				Task.Run(() => ListenLoop());
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] 指定アドレス '{_address}:{_port}' での HttpListener 起動失敗: {ex.Message}");

				// ワイルドカード/特定アドレス指定での起動失敗時、まず localhost/127.0.0.1 への自動フォールバックを試行
				try
				{
					Log(2, $"[RemoteController] http://localhost:{_port}/ へのフォールバック起動を試みます...");
					var fallbackListener = new HttpListener();
					fallbackListener.Prefixes.Add($"http://localhost:{_port}/");
					fallbackListener.Prefixes.Add($"http://127.0.0.1:{_port}/");
					fallbackListener.Start();

					_listener = fallbackListener;
					_isRunning = true;
					Log(2, $"[RemoteController] localhost フォールバック起動に成功しました (http://localhost:{_port}/)");

					Task.Run(() => ListenLoop());
					return;
				}
				catch (Exception fallbackEx)
				{
					Log(3, $"[RemoteController] localhost フォールバック起動も失敗しました: {fallbackEx.Message}");
				}

				// 2026/06/10 myi 新規作成（[::]使用時のエラー原因についての親切な案内文生成）
				string errorNotice = "";
				if (ex.Message.Contains("サポートされていません") && 
				    (_address != null && (_address.Contains("::") || _address.Contains("[::]"))))
				{
					errorNotice = "【重要】\r\nすべてのIPv6アドレスを示す「::」や「[::]」は、Windows OSの制限により直接リッスン用のアドレスプレフィックスとして登録できません。\r\n" +
					              "すべてのインターフェースで待ち受ける場合は、リッスンIPとして「*」を指定してください。\r\n" +
					              "IPv6限定にしたい場合は、「::1」（ローカルホスト）や、お使いのPCの「物理IPv6アドレス」を指定してください。\r\n\r\n";
				}

				// エラーがアクセス拒否（HttpListenerException で ErrorCode が 5）であるか特定します
				bool isAccessDenied = false;
				if (ex is HttpListenerException hex && hex.ErrorCode == 5)
				{
					isAccessDenied = true;
				}

				// アクセス拒否であり、かつ netsh コマンドが設定されていれば、UAC自動昇格を提案します
				if (isAccessDenied && netshCommands.Count > 0)
				{
					var result = MessageBox.Show(
						$"遠隔操作サーバーの起動に失敗しました（外部接続の権限がありません）。\r\n\r\n" +
						errorNotice +
						"【解決方法】\r\n" +
						"管理者権限で netsh コマンドを実行し、外部からの接続用ポートを自動的に登録できます。\r\n" +
						"「はい」を選択すると、ユーザーアカウント制御（UAC）画面が表示され、自動設定を実行します。\r\n\r\n" +
						"※自動設定を実行しますか？",
						"RemoteController ポート自動登録",
						MessageBoxButtons.YesNo,
						MessageBoxIcon.Question);

					if (result == DialogResult.Yes)
					{
						// 管理者権限での自動設定処理を呼び出します
						if (RunNetshAsAdmin(netshCommands))
						{
							MessageBox.Show(
								"ポートの登録が正常に完了しました。遠隔操作サーバーの起動を再試行します。",
								"RemoteController 自動設定完了",
								MessageBoxButtons.OK,
								MessageBoxIcon.Information);

							// HttpListener のインスタンスをリセットして再度起動します
							_listener = null;
							Start();
							return;
						}
						else
						{
							MessageBox.Show(
								"自動登録に失敗したか、またはユーザーによってキャンセルされました。\r\n" +
								"手動で実行するか、ElectronicObserverを「管理者として実行」してください。",
								"RemoteController 自動設定失敗",
								MessageBoxButtons.OK,
								MessageBoxIcon.Warning);
						}
					}
				}

				// 自動設定を行わなかった、または失敗した場合のフォールバック表示（手動コマンドの提示）
				MessageBox.Show(
					$"遠隔操作サーバーの起動に失敗しました。\r\n\r\n" +
					errorNotice +
					"【原因】\r\nWindowsのセキュリティ制限により外部からの接続権限がない、または指定されたポート/IPアドレスが他のアプリで使用されています。\r\n\r\n" +
					"【解決方法】\r\n以下のいずれかを行ってください。\r\n" +
					"1. ElectronicObserver を「管理者として実行」する\r\n" +
					"2. 管理者コマンドプロンプトで以下のコマンドを実行する:\r\n" +
					string.Join("\r\n", netshCommands) + "\r\n\r\n" +
					$"エラー詳細: {ex.Message}",
					"RemoteController 起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		//====================================================================================
		// 処理ブロック名 : RunNetshAsAdmin
		// 概要           : 管理者権限で netsh コマンドを実行し、HttpListener のプレフィックス
		//                  登録を自動で行います。UAC画面を立ち上げて昇格実行します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/06/10
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private bool RunNetshAsAdmin(List<string> commands)
		{
			try
			{
				foreach (var cmd in commands)
				{
					// "netsh " の文字列部分を切り除き、引数のみを取得します
					if (cmd.StartsWith("netsh ", StringComparison.OrdinalIgnoreCase))
					{
						string args = cmd.Substring("netsh ".Length);

						// 起動用情報を作成
						var psi = new System.Diagnostics.ProcessStartInfo
						{
							FileName = "netsh",
							Arguments = args,
							Verb = "runas", // UAC画面を立ち上げて昇格実行を要求
							UseShellExecute = true,
							WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
						};

						// プロセスを開始し、終了まで同期的に待機します
						using (var process = System.Diagnostics.Process.Start(psi))
						{
							process?.WaitForExit();
							// プロセスが正常に終了しなかった場合は処理を中断し、失敗を返します
							if (process == null || process.ExitCode != 0)
							{
								return false;
							}
						}
					}
				}
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"RemoteController: netshの管理者実行に失敗しました: {ex.Message}");
				return false;
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

				// 期限切れセッションのクリーンアップ（5分間隔で実行）
				CleanupExpiredSessionsIfNeeded();

				// ── 公開エンドポイント（認証不要） ──────────────────────

				// /login-page : ログインページHTMLを返却
				if (path == "/login-page")
				{
					SendLoginPage(res);
					return;
				}

				// /login : ログイン認証処理（POST専用）
				if (path == "/login")
				{
					if (req.HttpMethod == "POST")
					{
						HandleLogin(req, res);
					}
					else
					{
						res.StatusCode = 405;
						res.Close();
					}
					return;
				}
	
				// /logout : ログアウト処理（POST専用）
				if (path == "/logout")
				{
					if (req.HttpMethod == "POST")
					{
						HandleLogout(req, res);
					}
					else
					{
						res.StatusCode = 405;
						res.Close();
					}
					return;
				}
	
				// ── 認証ガード（認証が必要） ─────────────────────────────

				// 認証が必要か確認
				if (!IsAuthenticated(req, res))
				{
					// APIリクエストには401を返す（クライアントでハンドリングしやすくするため）
					if (path == "/screen.webp" || path == "/hud" || path == "/control")
					{
						res.StatusCode = 401;
						res.Close();
						return;
					}

					// HTMLページへのアクセスはログインページへ302リダイレクト
					Redirect(res, "/login-page");
					return;
				}

				// ── 認証済みリクエストのルーティング ─────────────────────

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
				else if (path == "/ws")
				{
					// WebSocket アップグレード要求
					await HandleWebSocketAsync(context);
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

		//====================================================================================
		// 処理ブロック名 : IsAuthenticated
		// 概要           : リクエストの認証状態を検証します。
		//                  Cookie (session_id) に基づいてセッションの有効性を確認します。
		//                  パスワードが空の場合は認証なし（常にtrue）を返します。
		//                  session_id が期限切れの場合、refresh_token で自動再発行を試行します。
		//                  Grace Period: 旧トークンは破棄されてから10秒間は有効とみなし、
		//                  並行リクエストによるRace Conditionを防ぎます。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/14
		// version        : 4.00 (Grace Period対応, 無効トークン検知時のCookie消去対応)
		//------------------------------------------------------------------------------------
		private bool IsAuthenticated(HttpListenerRequest req, HttpListenerResponse res)
		{
			// パスワードが設定されていない場合は認証なし（常に通過）
			bool passwordIsEmpty;
			lock (_passwordLock)
			{
				passwordIsEmpty = string.IsNullOrEmpty(_password);
			}
			if (passwordIsEmpty)
				return true;

			string sessionId = GetCookieValue(req, "session_id");

			// session_id がある場合は通常検証
			if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out SessionInfo session))
			{
				if (DateTime.UtcNow - session.LastAccessedAt <= TimeSpan.FromMinutes(SessionTimeoutMinutes))
				{
					session.Touch();
					return true;
				}
				// session_id は期限切れ → 削除してリフレッシュを試行
				_sessions.TryRemove(sessionId, out _);
			}

			// session_id が無効な場合、refresh_token で自動再発行を試行
			string refreshToken = GetCookieValue(req, "refresh_token");
			if (!string.IsNullOrEmpty(refreshToken))
			{
				if (_refreshTokens.TryGetValue(refreshToken, out RefreshTokenInfo refreshTokenInfo))
				{
					if (refreshTokenInfo.IsValid())
					{
						// Grace Period 内なら、旧トークンとして有効（並行リクエスト許容）
						if (refreshTokenInfo.RevokedAt.HasValue)
						{
							// 旧トークンだがGrace Period内 → 新トークンに置き換えて認証OK
							SetSessionCookie(res, refreshTokenInfo.SessionId);
							SetRefreshTokenCookie(res, refreshTokenInfo.ReplacedByToken);
							return true;
						}

						// 有効なトークン → トークンローテーション（Grace Period対応）
						// 旧トークンのRevokedAtを設定して即削除せず、Grace Periodを設ける
						refreshTokenInfo.RevokedAt = DateTime.UtcNow;

						// 新しいトークンペアを発行
						string newSessionId = Guid.NewGuid().ToString("N");
						string newRefreshToken = Guid.NewGuid().ToString("N");
						_sessions.TryAdd(newSessionId, new SessionInfo(newSessionId));

						// 新トークンのReplacedByTokenに自身を設定（循環参照防止のためnull）
						_refreshTokens.TryAdd(newRefreshToken, new RefreshTokenInfo(newRefreshToken, newSessionId));

						// 旧トークンのReplacedByTokenに新トークンを設定
						refreshTokenInfo.ReplacedByToken = newRefreshToken;

						// 旧リフレッシュトークンに紐づく旧セッションを削除
						_sessions.TryRemove(refreshTokenInfo.SessionId, out _);

						// 新しいCookieをレスポンスに付与
						SetSessionCookie(res, newSessionId);
						SetRefreshTokenCookie(res, newRefreshToken);

						return true;
					}

					// refresh_token も期限切れ（またはGrace Period外） → 削除
					_refreshTokens.TryRemove(refreshToken, out _);

					// 2026/07/22 myi 新規追加（P0: 無効トークン検知時のCookie消去）
					// ブラウザの無効Cookieを消去して、次回以降の同じ401エラーの繰り返しを防ぐ
					ClearCookie(res, "session_id");
					ClearCookie(res, "refresh_token");
				}
				else
				{
					// 2026/07/22 myi 修正（P0: ストアにトークンが存在しない場合のCookie消去）
					// クライアントが保持するCookieはサーバー側のストアに存在しない無効なトークンであるため、
					// ブラウザのCookieを消去して次回以降の同じ401エラーの繰り返しを防ぐ
					ClearCookie(res, "session_id");
					ClearCookie(res, "refresh_token");
				}
			}

			return false;
		}

		//====================================================================================
		// 処理ブロック名 : GetCookieValue
		// 概要           : リクエストのCookieから指定された名前の値を抽出します。
		//------------------------------------------------------------------------------------
		private string GetCookieValue(HttpListenerRequest req, string name)
		{
			var cookie = req.Cookies[name];
			return cookie?.Value;
		}

		//====================================================================================
		// 処理ブロック名 : Redirect
		// 概要           : HTTP 302 リダイレクト応答を送信します。
		//------------------------------------------------------------------------------------
		private void Redirect(HttpListenerResponse res, string url)
		{
			res.StatusCode = 302;
			res.Headers["Location"] = url;
			res.Close();
		}

		//====================================================================================
		// 処理ブロック名 : SetSessionCookie
		// 概要           : セッションIDをCookieとして設定するSet-Cookieヘッダを付与します。
		//                  HttpOnlyおよびSameSite=Strictを設定し、XSS・CSRFを緩和します。
		//------------------------------------------------------------------------------------
		private void SetSessionCookie(HttpListenerResponse res, string sessionId)
		{
			var cookie = new System.Net.Cookie("session_id", sessionId)
				{
					HttpOnly = true,
					Path = "/",
					// Expires を設定しない → セッションCookie化（ブラウザを閉じると消える）
					// Note: SameSite は .NET Framework 4.8 の System.Net.Cookie ではサポートされていない
				};
			res.AppendCookie(cookie);
		}

		//====================================================================================
		// 処理ブロック名 : SetRefreshTokenCookie
		// 概要           : リフレッシュトークンをCookieとして設定するSet-Cookieヘッダを付与します。
		//                  HttpOnlyおよびSameSite=Strictを設定し、XSS・CSRFを緩和します。
		//------------------------------------------------------------------------------------
		private void SetRefreshTokenCookie(HttpListenerResponse res, string refreshToken)
		{
			var cookie = new System.Net.Cookie("refresh_token", refreshToken)
				{
					HttpOnly = true,
					Path = "/",
					Expires = DateTime.UtcNow.AddDays(RefreshTokenDays)  // 30日間有効
					// Note: SameSite は .NET Framework 4.8 の System.Net.Cookie ではサポートされていない
				};
			res.AppendCookie(cookie);
		}

		//====================================================================================
		// 処理ブロック名 : ClearCookie
		// 概要           : 指定されたCookieを消去するSet-Cookieヘッダを付与します。
		//                  Expiresを過去に設定することでブラウザがCookieを削除します。
		//------------------------------------------------------------------------------------
		private void ClearCookie(HttpListenerResponse res, string name)
		{
			var cookie = new System.Net.Cookie(name, "")
				{
					HttpOnly = true,
					Path = "/",
					Expires = DateTime.UtcNow.AddDays(-1)  // 過去に設定して消去
					// Note: SameSite は .NET Framework 4.8 の System.Net.Cookie ではサポートされていない
				};
			res.AppendCookie(cookie);
		}

		//====================================================================================
		// 処理ブロック名 : HandleLogout
		// 概要           : /logout エンドポイントを処理します。
		//                  POST メソッドのみで、セッションIDとリフレッシュトークンの両方を破棄します。
		//                  破棄後、Cookieを消去してログインページへ302リダイレクトします。
		//------------------------------------------------------------------------------------
		private void HandleLogout(HttpListenerRequest req, HttpListenerResponse res)
		{
			// session_id を破棄
			string sessionId = GetCookieValue(req, "session_id");
			if (!string.IsNullOrEmpty(sessionId))
			{
				_sessions.TryRemove(sessionId, out _);
			}

			// refresh_token を破棄
			string refreshToken = GetCookieValue(req, "refresh_token");
			if (!string.IsNullOrEmpty(refreshToken))
			{
				_refreshTokens.TryRemove(refreshToken, out _);
			}

			// Cookie を消去
			ClearCookie(res, "session_id");
			ClearCookie(res, "refresh_token");

			// ログインページへリダイレクト
			Redirect(res, "/login-page");
		}

		//====================================================================================
		// 処理ブロック名 : CleanupExpiredSessionsIfNeeded
		// 概要           : 前回実行から5分以上経過している場合にのみCleanupExpiredSessions()を実行します。
		//                  /screen.webp や /hud などの高頻度エンドポイントで無駄な走査を防ぎます。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/22
		// version        : 1.00 (5分間隔のスキップ条件追加)
		//------------------------------------------------------------------------------------
		private void CleanupExpiredSessionsIfNeeded()
		{
			if ((DateTime.UtcNow - _lastCleanupTime).TotalMinutes < 5)
				return;

			_lastCleanupTime = DateTime.UtcNow;
			CleanupExpiredSessions();
		}

		//====================================================================================
		// 処理ブロック名 : CleanupExpiredSessions
		// 概要           : 期限切れのセッションおよびリフレッシュトークンをストアから削除します。
		//                  CleanupExpiredSessionsIfNeeded() から呼び出してください。
		//------------------------------------------------------------------------------------
		private void CleanupExpiredSessions()
		{
			// セッションのクリーンアップ
			var cutoff = DateTime.UtcNow.AddMinutes(-SessionTimeoutMinutes);
			var expiredSessionKeys = new List<string>();
			foreach (var kvp in _sessions)
			{
				if (kvp.Value.LastAccessedAt < cutoff)
				{
					expiredSessionKeys.Add(kvp.Key);
				}
			}
			foreach (var key in expiredSessionKeys)
			{
				_sessions.TryRemove(key, out _);
			}

			// リフレッシュトークンのクリーンアップ
			var expiredRefreshKeys = new List<string>();
			foreach (var kvp in _refreshTokens)
			{
				// 2026/07/22 myi 修正（P2: Grace Periodトークンのクリーンアップ実装）
				// 有効期限切れのトークンに加え、RevokedAtが設定されており10秒以上経過した
				// グレース期間トークンもクリーンアップ対象に含める（メモリリーク防止）
				bool isExpired = kvp.Value.IsExpired();
				bool isRevokedAndPastGracePeriod = kvp.Value.RevokedAt.HasValue &&
					(DateTime.UtcNow - kvp.Value.RevokedAt.Value) > TimeSpan.FromSeconds(10);
				if (isExpired || isRevokedAndPastGracePeriod)
				{
					expiredRefreshKeys.Add(kvp.Key);
				}
			}
			foreach (var key in expiredRefreshKeys)
			{
				_refreshTokens.TryRemove(key, out _);
			}
		}

		//====================================================================================
		// 処理ブロック名 : HandleLogin
		// 概要           : /login エンドポイントを処理します。
		//                  POST メソッドのみで、POSTボディからパスワードを受信します。
		//                  クエリパラメータからのパスワード取得はセキュリティの観点から廃止しました。
		//                  成功時：セッションIDを発行し、Cookieを設定して302リダイレクト。
		//                  失敗時：エラーJSONを返却。
		//                  ブルートフォース対策として、IPアドレスごとの失敗回数を追跡し、
		//                  一定回数を超えると一定時間リクエストをブロックします。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/14
		// version        : 3.00 (クエリパラメータ認証廃止, ブルートフォース対策, 定数時間比較導入)
		//------------------------------------------------------------------------------------
		private void HandleLogin(HttpListenerRequest req, HttpListenerResponse res)
		{
			// 認証無効（パスワード空）の場合はエラー
			if (string.IsNullOrEmpty(_password))
			{
				SendJsonResponse(res, "{\"success\": false, \"error\": \"認証は有効ではありません\"}");
				return;
			}

			// ブルートフォース対策：IPアドレスの取得
			string clientIp = req.RemoteEndPoint.Address.ToString();

			// ブロックチェック
			if (IsIpBlocked(clientIp))
			{
				SendJsonResponse(res, "{\"success\": false, \"error\": \"ログイン失敗が多いため一時的にブロックされています\"}");
				return;
			}

			// POSTボディからパスワードを取得（クエリパラメータからの取得は廃止）
			string pw = null;
			try
			{
				using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
				{
					string body = reader.ReadToEnd();
					// application/x-www-form-urlencoded の場合
					if (!string.IsNullOrEmpty(body))
					{
						var pairs = body.Split('&');
						foreach (var pair in pairs)
						{
							var kv = pair.Split('=');
							if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == "password")
							{
								pw = Uri.UnescapeDataString(kv[1]);
								break;
							}
						}
					}
				}
			}
			catch { /* ボディ解析エラー */ }

			// パスワードが空の場合は失敗としてカウント
			if (string.IsNullOrEmpty(pw))
			{
				RecordLoginFailure(clientIp);
				SendJsonResponse(res, "{\"success\": false, \"error\": \"パスワードが間違っています\"}");
				return;
			}

			// 定数時間比較でパスワード検証（タイミング攻撃対策）
			// ロックを使用してパスワード検証とセッション発行をアトミックに実行し、
			// UpdatePassword とのレースコンディションを防止する。
				bool isPasswordMatch;
				string newSessionId = null;
				string newRefreshToken = null;
	
				lock (_passwordLock)
				{
					isPasswordMatch = ConstantTimeEquals(pw, _password);
					if (isPasswordMatch)
					{
						newSessionId = Guid.NewGuid().ToString("N");
						newRefreshToken = Guid.NewGuid().ToString("N");
						_sessions.TryAdd(newSessionId, new SessionInfo(newSessionId));
						_refreshTokens.TryAdd(newRefreshToken, new RefreshTokenInfo(newRefreshToken, newSessionId));
					}
				}
	
				if (isPasswordMatch)
				{
					// 成功時は失敗履歴をクリア
					_loginAttempts.TryRemove(clientIp, out _);
	
					// Cookieを設定し、302リダイレクト
					SetSessionCookie(res, newSessionId);
					SetRefreshTokenCookie(res, newRefreshToken);
					Redirect(res, "/");
				}
				else
				{
					RecordLoginFailure(clientIp);
					SendJsonResponse(res, "{\"success\": false, \"error\": \"パスワードが間違っています\"}");
				}
		}

		//====================================================================================
		// 処理ブロック名 : ConstantTimeEquals
		// 概要           : 2つの文字列を定数時間で比較します。
		//                  タイミング攻撃を防ぐために、すべての文字を走査します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/22
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private static bool ConstantTimeEquals(string a, string b)
		{
			if (a == null && b == null) return true;
			if (a == null || b == null) return false;
			if (a.Length != b.Length) return false;

			int result = 0;
			for (int i = 0; i < a.Length; i++)
			{
				result |= a[i] ^ b[i];
			}
			return result == 0;
		}

		//====================================================================================
		// 処理ブロック名 : IsIpBlocked
		// 概要           : 指定されたIPアドレスがブルートフォースブロック対象かどうかを確認します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/22
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private bool IsIpBlocked(string ip)
		{
			if (!_loginAttempts.TryGetValue(ip, out var attempt))
				return false;

			// ブロック時間が経過している場合はカウンタをリセット
			if (DateTime.UtcNow - attempt.lastAttempt > TimeSpan.FromMinutes(LoginBlockMinutes))
			{
				_loginAttempts.TryRemove(ip, out _);
				return false;
			}

			// 最大失敗回数を超えている場合はブロック
			return attempt.count >= MaxLoginAttempts;
		}

		//====================================================================================
		// 処理ブロック名 : RecordLoginFailure
		// 概要           : 指定されたIPアドレスのログイン失敗を記録します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/22
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private void RecordLoginFailure(string ip)
		{
			var now = DateTime.UtcNow;
			var existing = _loginAttempts.AddOrUpdate(
					ip,
					key => (1, now),
					(key, oldValue) => (oldValue.count + 1, now)
				);
		}

		//====================================================================================
		// 処理ブロック名 : SendJsonResponse
		// 概要           : JSON レスポンスを簡単に送信するヘルパーです。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/14
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private void SendJsonResponse(HttpListenerResponse res, string json)
		{
			byte[] buffer = Encoding.UTF8.GetBytes(json);
			res.StatusCode = 200;
			res.ContentType = "application/json; charset=utf-8";
			res.ContentLength64 = buffer.Length;
			res.OutputStream.Write(buffer, 0, buffer.Length);
			res.Close();
		}

		//====================================================================================
		// 処理ブロック名 : ApplySecurityHeaders
		// 概要           : レスポンスに基本的なセキュリティヘッダーを付与します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/22
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private void ApplySecurityHeaders(HttpListenerResponse res)
		{
			res.Headers["X-Frame-Options"] = "DENY";
			res.Headers["X-Content-Type-Options"] = "nosniff";
			res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self' blob: data:;";
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
			ApplySecurityHeaders(res);
			res.ContentLength64 = buffer.Length;
			res.OutputStream.Write(buffer, 0, buffer.Length);
			res.Close();
		}

		/// <summary>
		/// ログインページ（login.html）を送信します。
		/// </summary>
		private void SendLoginPage(HttpListenerResponse res)
		{
			string html = GetLoginPageContent();
			byte[] buffer = Encoding.UTF8.GetBytes(html);

			res.ContentType = "text/html; charset=utf-8";
			ApplySecurityHeaders(res);
			res.ContentLength64 = buffer.Length;
			res.OutputStream.Write(buffer, 0, buffer.Length);
			res.Close();
		}

		/// <summary>
		/// ログインページ（login.html）のコンテンツを取得します。
		/// 外部ファイルが存在する場合はそれを優先し、存在しない場合はインラインHTMLを返します。
		/// </summary>
		private string GetLoginPageContent()
		{
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;

			// 探索候補パス
			var pathsToSearch = new List<string>
			{
				Path.Combine(baseDir, "login.html"),
				Path.Combine(baseDir, "Settings", "login.html"),
			};

			// ソースフォルダからの探索（RemoteController.htmlと同様のロジック）
			var dir = new DirectoryInfo(baseDir);
			while (dir != null)
			{
				try
				{
					var testPath = Path.Combine(dir.FullName, "Browser", "login.html");
					if (File.Exists(testPath))
					{
						pathsToSearch.Insert(0, testPath);
						break;
					}
				}
				catch { }
				dir = dir.Parent;
			}

			foreach (var path in pathsToSearch)
			{
				try
				{
					if (File.Exists(path))
					{
						return File.ReadAllText(path, Encoding.UTF8);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"RemoteController: ログインページ読み込みエラー ({path}): {ex.Message}");
				}
			}

			// ファイルが見つからない場合はエラーHTMLを返す
			return $@"<!DOCTYPE html>
<html lang='ja'>
<head>
		  <meta charset='UTF-8'>
		  <title>Login Error</title>
		  <style>
		      body {{ background-color: #0f172a; color: #f8fafc; font-family: sans-serif; padding: 40px; text-align: center; }}
		      h1 {{ color: #ef4444; }}
		  </style>
</head>
<body>
		  <h1>ログインページが見つかりません</h1>
		  <p>login.html を適切な場所に配置してください。</p>
</body>
</html>";
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
			// 2026/06/11 myi 新規作成（ゲーム画面タッチクリック）
			// ゲーム画面（viewport）をタップした位置の絶対座標を直接クリックする
			else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					_cursorX = Math.Max(0, Math.Min(1.0, tx));
					_cursorY = Math.Max(0, Math.Min(1.0, ty));
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
					// IPC 最適化: 文字列補間ではなく事前構築された定数 + 連結を使用し、GC負荷を削減
					var script = QualityScriptPrefix + qualityScale.ToString(System.Globalization.CultureInfo.InvariantCulture) + QualityScriptSuffix;
					kancolleFrame?.ExecuteJavaScriptAsync(script);
				}
					#endregion
					// 2026/07/14 myi 新規作成（ドラッグ操作対応）
					// マウスダウン（ドラッグ開始）
					else if (action == "mousedown")
					{
						TriggerMouseDown();
					}
					// マウスアップ（ドラッグ終了）
					else if (action == "mouseup")
					{
						TriggerMouseUp();
					}
		
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

		//====================================================================================
		// 処理ブロック名 : TriggerMouseDown
		// 概要           : ドラッグ開始時、現在のカーソル位置で左ボタンダウンを送信
		//                  CefEventFlags.None を使用（公式推奨パターン）
		//------------------------------------------------------------------------------------
		private void TriggerMouseDown()
		{
			if (_parent.Browser == null) return;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return;

			var coords = GetBrowserCoords();
			// CefEventFlags.None を使用（公式推奨）
			// isUp=false でダウンイベントを指定
			var mouseEvent = new MouseEvent(coords.X, coords.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			Console.WriteLine($"RemoteController: TriggerMouseDown @ {coords.X},{coords.Y}");
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseUp
		// 概要           : ドラッグ終了時、現在のカーソル位置で左ボタンアップを送信
		//                  CefEventFlags.None を使用（公式推奨パターン）
		//------------------------------------------------------------------------------------
		private void TriggerMouseUp()
		{
			if (_parent.Browser == null) return;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return;

			var coords = GetBrowserCoords();
			// CefEventFlags.None を使用（公式推奨）
			// isUp=true でアップイベントを指定
			var mouseEvent = new MouseEvent(coords.X, coords.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
			Console.WriteLine($"RemoteController: TriggerMouseUp @ {coords.X},{coords.Y}");
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
				// スレッドセーフのために lock で囲む
				string cachedHudJson;
				string lastMaterialJson, lastFleetsJson, lastBattleJson, lastCombatJson, lastQuestsJson, lastLogsJson;
				long materialVersion, fleetsVersion, battleVersion, combatVersion, questsVersion, logsVersion;

				lock (_hudLock)
				{
					cachedHudJson = _cachedHudJson;
					lastMaterialJson = _lastMaterialJson;
					lastFleetsJson = _lastFleetsJson;
					lastBattleJson = _lastBattleJson;
					lastCombatJson = _lastCombatJson;
					lastQuestsJson = _lastQuestsJson;
					lastLogsJson = _lastLogsJson;
					materialVersion = _materialVersion;
					fleetsVersion = _fleetsVersion;
					battleVersion = _battleVersion;
					combatVersion = _combatVersion;
					questsVersion = _questsVersion;
					logsVersion = _logsVersion;
				}

				if (!string.IsNullOrEmpty(cachedHudJson))
				{
					var sb = new StringBuilder();
					sb.Append("{");

					// 1. material（資源）
					sb.Append("\"material\":{\"changed\":").Append(clientMaterialVer != materialVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(materialVersion);
					if (clientMaterialVer != materialVersion && lastMaterialJson != null)
					{
						sb.Append(",\"data\":").Append(lastMaterialJson);
					}
					sb.Append("},");

					// 2. fleets（艦隊）
					sb.Append("\"fleets\":{\"changed\":").Append(clientFleetsVer != fleetsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(fleetsVersion);
					if (clientFleetsVer != fleetsVersion && lastFleetsJson != null)
					{
						sb.Append(",\"data\":").Append(lastFleetsJson);
					}
					sb.Append("},");

					// 3. battle（作戦）
					sb.Append("\"battle\":{\"changed\":").Append(clientBattleVer != battleVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(battleVersion);
					if (clientBattleVer != battleVersion && lastBattleJson != null)
					{
						sb.Append(",\"data\":").Append(lastBattleJson);
					}
					sb.Append("},");

					// 4. combat（戦闘）
					sb.Append("\"combat\":{\"changed\":").Append(clientCombatVer != combatVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(combatVersion);
					if (clientCombatVer != combatVersion && lastCombatJson != null)
					{
						sb.Append(",\"data\":").Append(lastCombatJson);
					}
					sb.Append("},");

					// 5. quests（任務）
					sb.Append("\"quests\":{\"changed\":").Append(clientQuestsVer != questsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(questsVersion);
					if (clientQuestsVer != questsVersion && lastQuestsJson != null)
					{
						sb.Append(",\"data\":").Append(lastQuestsJson);
					}
					sb.Append("},");

					// 6. logs（ログ）
					sb.Append("\"logs\":{\"changed\":").Append(clientLogsVer != logsVersion ? "true" : "false");
					sb.Append(",\"version\":").Append(logsVersion);
					if (clientLogsVer != logsVersion && lastLogsJson != null)
					{
						sb.Append(",\"data\":").Append(lastLogsJson);
					}
					sb.Append("}");

					sb.Append("}");
					jsonResponse = sb.ToString();
				}
				else
				{
					// データ未同期時は204 No Contentを返却
					res.StatusCode = 204;
					return;
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
		// 処理ブロック名 : CaptureCanvasAsync
		// 概要           : 外部（C#）からの要求に応じて、ブラウザ側の JavaScript エンジを呼び出し、
		//                  キャッシュされた最新フレームデータの引き抜きを行います。
		//                  2026/06/02 myi 変更（WebGLバッファクリア対策としてJS側同期キャッシュから pull で引き抜く設計に変更）
		//------------------------------------------------------------------------------------
		private async Task<Tuple<string, string>> CaptureCanvasAsync(bool isInit)
		{
			// 艦これの描画フレームがロードされている iframe への参照を取得します
			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null)
			{
				return Tuple.Create("error", "Kancolle frame not found");
			}

			if (_parent.Browser == null || !_parent.Browser.IsBrowserInitialized)
			{
				return Tuple.Create("error", "Browser not initialized");
			}

			// IPC 最適化: 事前構築されたスクリプト定数を使用し、呼び出し毎の文字列生成によるGC負荷を排除
			string script = isInit ? CaptureScriptInit : CaptureScriptNormal;

			try
			{
				var response = await kancolleFrame.EvaluateScriptAsync(script);
				if (response.Success && response.Result != null)
				{
					var list = response.Result as System.Collections.Generic.IList<object>;
					if (list != null && list.Count >= 2)
					{
						string type = list[0] as string;
						string data = list[1] as string;
						return Tuple.Create(type, data);
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
				return Tuple.Create("error", ex.Message);
			}

			return Tuple.Create("error", "Capture engine not ready");
		}

		//====================================================================================
		// 処理ブロック名 : SendScreenDiffWebPAsync
		// 概要           : クライアントからのリクエスト受信時に、JS側のキャプチャおよび軽量差分判定を呼び出し、
		//                  差分がない場合は 204 No Content を、ある場合は WebP 画像を応答します（GET /screen.webp）。
		//                  PCウィンドウの表示サイズやアスペクト比に依存せず、常に余白なしで
		//                  高精細なゲーム画面を配信できます。
		//                  2026/06/02 myi 変更（C#側のGDI+デコードおよび差分判定を完全廃止し、JS側判定に一本化）
		//------------------------------------------------------------------------------------
		private async Task SendScreenDiffWebPAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			if (_parent.Browser == null || !_parent.Browser.IsBrowserInitialized)
			{
				res.StatusCode = 503;
				res.Close();
				return;
			}

			try
			{
				// クライアント側からの初期化要求・再接続シグナル（init=1）を検出する
				bool isInit = req.QueryString["init"] == "1" || req.QueryString["init"] == "true";

				// 1. canvas からのオンデマンドキャプチャおよびJS側差分判定を実行
				var capture = await CaptureCanvasAsync(isInit);
				string type = capture.Item1;
				string dataUrl = capture.Item2;

				if (type == "error")
				{
					res.StatusCode = 503;
					return;
				}

				// 2. 差分判定結果に応じたレスポンス制御
				if (type == "none")
				{
					// 差分なしは 204 No Content を応答（通信量とクライアントの描画負荷を完全にゼロ化）
					res.StatusCode = 204;
					res.Headers["X-Diff-Type"] = "none";
				}
				else if (type == "full" && !string.IsNullOrEmpty(dataUrl))
				{
					// 差分あり時は高精細な WebP データをバイナリにデコードして高速転送
					byte[] fullWebpData = ConvertToBytes(dataUrl, "data:image/webp");
					if (fullWebpData == null)
					{
						res.StatusCode = 503;
						return;
					}

					res.ContentType = "image/webp";
					res.Headers["X-Diff-Type"] = "full";
					res.ContentLength64 = fullWebpData.Length;
					await res.OutputStream.WriteAsync(fullWebpData, 0, fullWebpData.Length);
				}
				else
				{
					res.StatusCode = 503;
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
			_sessions.Clear();
			_refreshTokens.Clear();

			// WebSocket セッションのクリーンアップ
			// CancellationToken の Cancel を先に呼び出し、各ループタスクを停止を通知
			foreach (var kvp in _webSocketSessions)
			{
				kvp.Value.CancellationToken.Cancel();
			}

			// 各ループタスクの完了を待機（最大5秒）
			var allTasks = new List<Task>();
			foreach (var kvp in _webSocketSessions)
			{
				var session = kvp.Value;
				if (session.CaptureTask != null) allTasks.Add(session.CaptureTask);
				if (session.SendTask != null) allTasks.Add(session.SendTask);
				if (session.ReceiveTask != null) allTasks.Add(session.ReceiveTask);
			}

			if (allTasks.Count > 0)
			{
				try
				{
					Task.WaitAll(allTasks.ToArray(), TimeSpan.FromSeconds(5));
				}
				catch (AggregateException ex)
				{
					// 各タスクの例外をログ出力（OperationCanceledException は正常終了とみなす）
					foreach (var inner in ex.InnerExceptions)
					{
						if (!(inner is OperationCanceledException))
						{
							Console.WriteLine($"RemoteController: ループタスク終了時の例外: {inner.Message}");
						}
					}
				}
			}

			// セッションの Dispose（CancellationTokenSource, SemaphoreSlim, バッファ返却等）
			foreach (var kvp in _webSocketSessions)
			{
				kvp.Value.Dispose();
			}
			_webSocketSessions.Clear();
		}

		//====================================================================================
		// 処理ブロック名 : HandleWebSocketAsync
		// 概要           : WebSocket アップグレード要求を処理し、バイナリストリーミングを開始します。
		//                  キャプチャタスクと送信タスクを分離し、SendAsyncのawaitによる遅延蓄積を防ぎます。
		//                  例外発生時は Abort() により確実にソケットリソースを破棄します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 2.00 (WebSocket.Abort 導入, 例外ハンドリング強化)
		//------------------------------------------------------------------------------------
		private async Task HandleWebSocketAsync(HttpListenerContext context)
		{
			var req = context.Request;
			var res = context.Response;
			System.Net.WebSockets.WebSocket ws = null;

			// 認証チェック
			if (!IsAuthenticated(req, res))
			{
				Log(2, $"[RemoteController] WebSocket 接続拒否: 未認証 (IP: {req.RemoteEndPoint})");
				res.StatusCode = 401;
				res.Close();
				return;
			}

			try
			{
				// WebSocket アップグレード
				var acceptResult = await context.AcceptWebSocketAsync(null);
				ws = acceptResult.WebSocket;

				// セッションIDを生成（クッキーから取得、または新規発行）
				string sessionId = GetCookieValue(req, "session_id") ?? Guid.NewGuid().ToString("N");
				if (string.IsNullOrEmpty(GetCookieValue(req, "session_id")))
				{
					_sessions.TryAdd(sessionId, new SessionInfo(sessionId));
				}

				var session = new WebSocketSession(ws, sessionId);
				_webSocketSessions.TryAdd(sessionId, session);

				Log(2, $"[RemoteController] WebSocket 接続確立: SessionID={sessionId}, IP={req.RemoteEndPoint}");

				// 3つの独立タスクを開始（キャプチャ / 送信 / 受信）
				session.CaptureTask = Task.Run(() => WebSocketCaptureLoopAsync(session));
				session.SendTask = Task.Run(() => WebSocketSendLoopAsync(session));
				session.ReceiveTask = Task.Run(() => WebSocketReceiveLoopAsync(session));
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket アップグレードエラー (IP: {req.RemoteEndPoint}): {ex.GetType().Name} - {ex.Message}");
				
				// リソースリーク防止のため強制的にソケットを破棄
				try { ws?.Abort(); } catch { }
				try { res.StatusCode = 500; res.Close(); } catch { }
			}
		}

		//====================================================================================
		// 処理ブロック名 : WebSocketCaptureLoopAsync
		// 概要           : 画面キャプチャを専用タスクで実行し、最新1枚をセッションにセットします。
		//                  SendAsyncのawaitによる遅延蓄積を防ぐため、送信タスクとは分離しています。
		//                  Drop Frame: 最新1枚以外は破棄して送信しない。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private async Task WebSocketCaptureLoopAsync(WebSocketSession session)
		{
			var ws = session.WebSocket;
			var token = session.CancellationToken.Token;
			var swTotal = new System.Diagnostics.Stopwatch();
			var swCap = new System.Diagnostics.Stopwatch();
			var swSend = new System.Diagnostics.Stopwatch();
	
			int frameCounter = 0;
			long totalCapTime = 0;
			long totalSendTime = 0;
			DateTime lastLogTime = DateTime.UtcNow;
	
			// ドリフト補正タイマー: 絶対時刻ベースの目標管理
			long nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			long ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;
			int lastTargetInterval = session.TargetIntervalMs;

			try
			{
				while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
				{
					swTotal.Restart();
					swCap.Restart();

					// 画面キャプチャ
					var capture = await CaptureCanvasAsync(false);
					swCap.Stop();

					string type = capture.Item1;
					string dataUrl = capture.Item2;

					if (type == "full" && !string.IsNullOrEmpty(dataUrl))
					{
						byte[] webpData = ConvertToBytes(dataUrl, "data:image/webp");
						if (webpData != null && webpData.Length > 0)
						{
							swSend.Restart();
							await session.SendSemaphore.WaitAsync(token);
							try
							{
								if (ws.State == WebSocketState.Open)
								{
									var segment = new ArraySegment<byte>(webpData);
									await ws.SendAsync(segment, WebSocketMessageType.Binary, true, token);
								}
							}
							finally
							{
								session.SendSemaphore.Release();
							}
							swSend.Stop();

							frameCounter++;
							totalCapTime += swCap.ElapsedMilliseconds;
							totalSendTime += swSend.ElapsedMilliseconds;
						}
					}

					swTotal.Stop();
					long totalMs = swTotal.ElapsedMilliseconds;

					// 3秒ごとに直列送信プロファイリング結果を出力
					// if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 3.0)
					// {
					// 	double sec = (DateTime.UtcNow - lastLogTime).TotalSeconds;
					// 	double fps = frameCounter / sec;
					// 	double avgCap = frameCounter > 0 ? (double)totalCapTime / frameCounter : 0;
					// 	double avgSend = frameCounter > 0 ? (double)totalSendTime / frameCounter : 0;

					// 	Log(2, $"[RemoteProfiler] Direct WS FPS: {fps:F1}, AvgCap: {avgCap:F1}ms, AvgSend: {avgSend:F1}ms, TargetInterval: {session.TargetIntervalMs}ms");

					// 	frameCounter = 0;
					// 	totalCapTime = 0;
					// 	totalSendTime = 0;
					// 	lastLogTime = DateTime.UtcNow;
					// }

					int targetInterval = session.TargetIntervalMs;
	
					// MAX設定時は60FPS制限を適用（約16.67ms → 16ms）
					if (targetInterval <= 0)
					{
						targetInterval = 16;
					}
	
					// FPS設定変更時のリセット: 次回目標時刻を現在時刻にリセット
					if (targetInterval != lastTargetInterval)
					{
						nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
						lastTargetInterval = targetInterval;
					}
	
					// ドリフト補正タイマー: 絶対時刻ベースの目標管理
					nextTargetTicks += targetInterval * ticksPerMs;
					long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
					long remainingTicks = nextTargetTicks - currentTicks;
					int remainingMs = (int)(remainingTicks / ticksPerMs);
	
					if (remainingMs > 0)
					{
						await Task.Delay(remainingMs, token);
					}
					else
					{
						// 処理遅延: 次回目標を現在時刻にリセット（スパイラル防止）
						nextTargetTicks = currentTicks;
					}
				}
			}
			catch (OperationCanceledException) { /* 正常終了 */ }
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket キャプチャ/送信ループエラー (Session: {session.SessionId}): {ex.Message}");
			}
		}

		//====================================================================================
		// 処理ブロック名 : WebSocketSendLoopAsync
		// 概要           : Heartbeat 送信専用のループタスク。
		//------------------------------------------------------------------------------------
		private async Task WebSocketSendLoopAsync(WebSocketSession session)
		{
			var ws = session.WebSocket;
			var token = session.CancellationToken.Token;

			try
			{
				while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
				{
					await Task.Delay(WebSocketHeartbeatIntervalMs, token);

					if (ws.State != WebSocketState.Open) break;

					// Heartbeat 処理
					if ((DateTime.UtcNow - session.LastHeartbeat).TotalMilliseconds > WebSocketHeartbeatIntervalMs)
					{
						await session.SendSemaphore.WaitAsync(token);
						try
						{
							if (ws.State == WebSocketState.Open)
							{
								var segment = new ArraySegment<byte>(HeartbeatBytes);
								await ws.SendAsync(segment, WebSocketMessageType.Text, true, token);
								session.LastHeartbeat = DateTime.UtcNow;
							}
						}
						finally
						{
							session.SendSemaphore.Release();
						}
					}
				}
			}
			catch (OperationCanceledException) { /* 正常終了 */ }
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket Heartbeat ループエラー (Session: {session.SessionId}): {ex.Message}");
			}
		}

		//====================================================================================
		// 処理ブロック名 : WebSocketReceiveLoopAsync
		// 概要           : WebSocket からクライアントの操作イベントを受信するループ。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private async Task WebSocketReceiveLoopAsync(WebSocketSession session)
		{
			var ws = session.WebSocket;
			var token = session.CancellationToken.Token;

			// バッファプールから受信バッファを割り当て
			byte[] receiveBuffer = _bufferPool.Rent(4096);

			try
			{
				while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
				{
					var result = await ws.ReceiveAsync(
						new ArraySegment<byte>(receiveBuffer), token);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						Log(2, $"[RemoteController] WebSocket 切断リクエスト受信: SessionID={session.SessionId}");
						await ws.CloseAsync(
							WebSocketCloseStatus.NormalClosure,
							"Closing", token);
						break;
					}

					if (result.MessageType == WebSocketMessageType.Binary
						|| result.MessageType == WebSocketMessageType.Text)
					{
						// 受信データをコピーしてから処理（バッファは再利用されるため）
						byte[] messageBytes = new byte[result.Count];
						Array.Copy(receiveBuffer, messageBytes, result.Count);
						string message = Encoding.UTF8.GetString(messageBytes, 0, result.Count);
						ProcessWebSocketControlMessage(session, message);
					}
				}
			}
			catch (OperationCanceledException) { /* 正常終了 */ }
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket 受信ループエラー (Session: {session.SessionId}): {ex.Message}");
			}
			finally
			{
				// 受信バッファを必ず返却
				_bufferPool.Return(receiveBuffer, clearArray: true);
			}
		}

		//====================================================================================
		// 処理ブロック名 : ProcessWebSocketControlMessage
		// 概要           : WebSocket 経由で受信した制御メッセージを処理します。
		//                  形式: JSON { "action": "init" }, { "action": "move", "x": 0.5, "y": 0.5 }
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 2.00 (action == "init" 初期画面キーフレーム要求対応)
		//------------------------------------------------------------------------------------
		private void ProcessWebSocketControlMessage(WebSocketSession session, string message)
		{
			try
			{
				string action = ExtractJsonString(message, "action");
				string xStr = ExtractJsonString(message, "x");
				string yStr = ExtractJsonString(message, "y");

				if (action == "init")
				{
					Log(2, $"[RemoteController] WebSocket init 要求を受領: 初期化フルフレームを生成します (Session: {session.SessionId})");
					Task.Run(async () =>
					{
						try
						{
							var capture = await CaptureCanvasAsync(true); // isInit = true
							if (capture.Item1 == "full" && !string.IsNullOrEmpty(capture.Item2))
							{
								byte[] webpData = ConvertToBytes(capture.Item2, "data:image/webp");
								if (webpData != null && webpData.Length > 0)
								{
									session.SetLatestFrame(webpData);
									Log(2, $"[RemoteController] WebSocket init フルフレーム生成成功 ({webpData.Length} bytes)");
								}
							}
						}
						catch (Exception ex)
						{
							Log(3, $"[RemoteController] WebSocket init キャプチャエラー: {ex.Message}");
						}
					});
				}
				else if (action == "set_fps")
				{
					string intervalStr = ExtractJsonString(message, "intervalMs");
					if (int.TryParse(intervalStr, out int intervalMs))
					{
						session.TargetIntervalMs = Math.Max(0, Math.Min(300000, intervalMs));
						int jsInterval = (session.TargetIntervalMs <= 0) ? 16 : Math.Max(16, session.TargetIntervalMs);
						Log(2, $"[RemoteController] WebSocket 目標間隔変更受領: {session.TargetIntervalMs}ms (Session: {session.SessionId})");

						// ブラウザJS側の常駐キャプチャタイマーも動的更新
						try
						{
							var kancolleFrame = _parent.GetKanColleFrame();
							kancolleFrame?.ExecuteJavaScriptAsync($"if(window._kancolleCaptureEngine)window._kancolleCaptureEngine.intervalMs={jsInterval};");
						}
						catch { }
					}
				}
				else if (action == "move" && double.TryParse(xStr, out double x) && double.TryParse(yStr, out double y))
				{
					_cursorX = Math.Max(0, Math.Min(1.0, x));
					_cursorY = Math.Max(0, Math.Min(1.0, y));
					TriggerMouseMove();
				}
				else if (action == "click")
				{
					TriggerMouseClick();
				}
				else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					_cursorX = Math.Max(0, Math.Min(1.0, tx));
					_cursorY = Math.Max(0, Math.Min(1.0, ty));
					TriggerMouseClick();
				}
				else if (action == "mousedown")
				{
					TriggerMouseDown();
				}
				else if (action == "mouseup")
				{
					TriggerMouseUp();
				}
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket 制御メッセージ処理エラー: {ex.Message}");
			}
		}

		//====================================================================================
		// 処理ブロック名 : ExtractJsonString
		// 概要           : JSON 文字列から指定されたキーの値を抽出します（軽量パース）。
		//                  エスケープ文字 (\") に対応し、キー名の境界チェックを追加。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 1.00
	//------------------------------------------------------------------------------------
		private string ExtractJsonString(string json, string key)
		{
			// キー名の境界チェック（例: "action" を検索する際、"faction" と混同しない）
			string search = $"\"{key}\"";
			int idx = -1;
			int pos = 0;
			while ((pos = json.IndexOf(search, pos)) != -1)
			{
				// キー名の前後が { , または空白であれば正当なキー
				bool leftOk = pos == 0 || json[pos - 1] == '{' || json[pos - 1] == ',';
				if (leftOk)
				{
					idx = pos;
					break;
				}
				pos++;
			}
			if (idx == -1) return null;

			// コロンを検索
			int colonIdx = json.IndexOf(':', idx + search.Length);
			if (colonIdx == -1) return null;

			// コロン後の空白をスキップ
			int startIdx = colonIdx + 1;
			while (startIdx < json.Length && (json[startIdx] == ' ' || json[startIdx] == '\t' || json[startIdx] == '\n' || json[startIdx] == '\r'))
				startIdx++;

			if (startIdx >= json.Length) return null;

			if (json[startIdx] == '"')
			{
				// 文字列値（エスケープ文字対応）
				var sb = new StringBuilder();
				int i = startIdx + 1;
				while (i < json.Length)
				{
					if (json[i] == '\\' && i + 1 < json.Length)
					{
						// エスケープシーケンス
						char next = json[i + 1];
						switch (next)
						{
							case '"': sb.Append('"'); i += 2; break;
							case '\\': sb.Append('\\'); i += 2; break;
							case '/': sb.Append('/'); i += 2; break;
							case 'n': sb.Append('\n'); i += 2; break;
							case 'r': sb.Append('\r'); i += 2; break;
							case 't': sb.Append('\t'); i += 2; break;
							case 'b': sb.Append('\b'); i += 2; break;
							case 'f': sb.Append('\f'); i += 2; break;
							default: sb.Append(json[i]); i++; break; // 不明なエスケープはそのまま
						}
					}
					else if (json[i] == '"')
					{
						// 文字列の終端
						break;
					}
					else
					{
						sb.Append(json[i]);
						i++;
					}
				}
				return sb.ToString();
			}
			else
			{
				// 数値値・ブール値・null
				int endIdx = startIdx;
				while (endIdx < json.Length && json[endIdx] != ',' && json[endIdx] != '}' && json[endIdx] != ' ' && json[endIdx] != '\n' && json[endIdx] != '\r' && json[endIdx] != '\t')
					endIdx++;
				return json.Substring(startIdx, endIdx - startIdx);
			}
		}

		//====================================================================================
		// 処理ブロック名 : SendHeartbeatAsync
		// 概要           : WebSocket の Heartbeat (Ping) を送信します。
		//                  .NET Framework 4.8 の WebSocket は Ping/Pong メッセージをサポートしないため、
		//                  アプリケーションレベルの Heartbeat として Text メッセージを送信します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 1.00
	//------------------------------------------------------------------------------------
		private async Task SendHeartbeatAsync(System.Net.WebSockets.WebSocket ws, CancellationToken token)
		{
			try
			{
				// IPC 最適化: 事前構築されたバイト列を使用し、毎回の Encoding.UTF8.GetBytes によるGC負荷を排除
				await ws.SendAsync(
					new ArraySegment<byte>(HeartbeatBytes),
					WebSocketMessageType.Text,
					true, token);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"RemoteController: Heartbeat 送信エラー: {ex.Message}");
			}
		}
	}
}

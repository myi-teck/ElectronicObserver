//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.cs
// summary        : モバイル端末からの遠隔操作（画面配信・操作転送）を管理するクラス
//--------------------------------------------------------------------------------------------
// author         : myi
// creation date  : 2026/04/20
// version        : 2.00
//--------------------------------------------------------------------------------------------
#region "change history"
// change history : 2026/04/20 myi 新規作成
//                  2026/05/25 myi HTMLレイアウト調整の利便性向上のため、HTMLコンテンツを外部ファイル(RemoteController.html)に切り出し.
//                  2026/07/29 myi HTTPヘッダーフォールバック処理の改善、WebSocket/HTTP間シーケンス番号競合対策、
//                                           SendSemaphore保持時間最小化、init処理のシーケンス番号管理適正化を実施。
//                                           ビルド不要でリアルタイム反映可能なロード処理を実装。
//                                           また、余分なハードコードHTML(デフォルト定義)を完全に削除し、動的パス探索を強化。
//                  2026/07/29 myi フェーズ5：中央キャプチャ・ファンアウト方式を導入。各セッション独立キャプチャを単一中央タスクに統合し、
//                                           CefSharp負荷を O(N) → O(1) に削減。SharedFrameBuffer, CentralCaptureLoopAsync,
//                                           WebSocketFanoutLoopAsync, GetMaxFpsIntervalMs, CleanupZombieSessions を実装。
//                  2026/07/29 myi フェーズ6：接続管理とクリーンアップの実装。
//                                           1. 最大接続数制限のアトミック管理（Interlocked 使用、レースコンディション排除）
//                                           2. RemoveWebSocketSession によるセッション即時削除（ゾンビ防止）
//                                           3. WebSocket切断状態の即時検知（WebSocketState チェック追加）
//                                           4. SharedFrameBuffer 参照カウント解放の整合性確認・Dispose 時クリーンアップ
//                                           5. WebSocketSession 二重解放保護（_disposed フラグ）
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
//                  2026/08/18 myi /control APIに logout_blank アクションを追加。
//                                           ブラウザ操作セクションから本体ブラウザを空白ページへ
//                                           ログアウト（Cookie維持）する機能を追加。
#endregion
// Copyright 2026 myi
//============================================================================================

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Browser
{
	/// <summary>
	/// リモート操作用のWebサーバーおよびロジックを提供します。
	/// </summary>
	public partial class RemoteController : IDisposable
	{
		private HttpListener _listener;
		private FormBrowser _parent;
		// メインスレッド（Start/Dispose）と Task.Run スレッド（ListenLoop）で共有。
		// 可視性は Start 内書き込み後の Task.Run 呼び出しで保証され、
		// ループ終了は Dispose の _listener.Stop() 起因の ObjectDisposedException で駆動される。
		private bool _isRunning;
		private int _port;
		
		// （リッスン対象のIPアドレス設定値保持用）
		private string _address;

		// （リモート操作サーバーのログインパスワード保持用）
		private string _password;

		// （パスワード更新処理のロックオブジェクト）
		/// <summary>
		/// パスワード更新および認証チェックのアトミックな処理を保護するロックオブジェクト。
		/// volatileだけでは UpdatePassword 内のセッション破棄とパスワード変更の原子性が
		/// 保証されないため、専用のロックを導入する。
		/// </summary>
		private readonly object _passwordLock = new object();

		// （HUDデータ更新のロックオブジェクト）
		/// <summary>
		/// UpdateHudData と SendHudData 間のHUDキャッシュ（_cachedHudJson, 各バージョン）への
		/// 読み書きを排他制御するためのロックオブジェクト。
		/// </summary>
		private readonly object _hudLock = new object();

		//====================================================================================
		// WebSocket 通信関連フィールド・定数
		//------------------------------------------------------------------------------------
		// （WebSocket セッション管理）
		private readonly ConcurrentDictionary<string, WebSocketSession> _webSocketSessions = new ConcurrentDictionary<string, WebSocketSession>();

		// （バッファプール：GC 負荷削減）
		private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

		// （WebSocket 定数）
		private const int WebSocketHeartbeatIntervalMs = 15000; // 15秒
		private const int WebSocketMaxQueueSize = 1;            // Drop Frame 用キュー上限（最新1枚のみ保持）

		// （接続数制限のアトミック管理用）
		private int _activeConnectionCount; // Interlocked で管理
	
		// （認証ガード対象パスリスト）
		/// <summary>
		/// 認証が必要で、未認証時に401を返すAPIパスのリスト。
		/// ハードコードされたif文ではなく静的HashSetで管理し、保守性を向上させる。
		/// JSON API パスのみ。HTML ページ（/logout-page 等）は認証ガードの対象外であり、
		/// 未認証アクセスは /login-page への 302 リダイレクトで処理される。
		/// </summary>
		private static readonly HashSet<string> s_authenticatedApiPaths = new HashSet<string>
		{
			"/screen.webp",
			"/hud",
			"/control",
			"/capture-mode",
			"/capture-config",
			"/login-state",
			"/event-log",
			"/log-toggle",
		};

		// （グローバルシーケンス番号：WebSocket/HTTP共通の単調増加int型）
		// （seq 型整合性の明文化）
		/// <summary>
		/// グローバルシーケンス番号（WebSocket/HTTP共通、単調増加）
		/// Interlocked.Increment でスレッドセーフなインクリメント
		///
		/// 【設計書整合性: int 型採用の理由】
		/// 設計書では uint (32bit単調増加) が規定されているが、C# の Interlocked.Increment は uint 型を
		/// サポートしていないため、int 型 (32bit 符号付き) を使用している。
		/// ヘッダー書き込み時（BuildWebCodecsHeader）で (uint) キャストを行い、
		/// Big-Endian バイト列に変換する。これにより設計書の uint 仕様に準拠した通信が行われる。
		///
		/// ラップアラウンド対応: 符号付き int として -2147483648 〜 +2147483647 の範囲で単調増加し、
		/// 差分計算（int 演算）で過去フレームの検出を行う。符号付き整数の差分でラップアラウンド後も
		/// 過去フレームの検出が正しく機能する（24日超過時のフレーム配信停止を防止）。
		/// </summary>
		private int _globalSequenceNumber;

		// （フェーズ5：中央キャプチャ・ファンアウト方式）
		/// <summary>
		/// 中央キャプチャからの共有フレームバッファ（最新の1フレームのみ保持）
		/// 参照カウント付きで、全セッションが送信完了後にメモリを解放
		/// </summary>
		private readonly SharedFrameBuffer _sharedFrameBuffer;

		// （フェーズ5：動的FPS制御用キャッシュ）
		private int _cachedMaxFpsInterval = 100;
		private volatile int _cachedSessionCount = 0;

		// （IPCプロファイリング）
		/// <summary>
		/// CaptureCanvasAsync の実行遅延統計を保持するリングバッファ（最大30サンプル）
		/// 移動平均/最大/最小値を算出し、500ms超過時に警告ログを出力
		/// 容量30は、10FPS(100ms/フレーム)で約3秒分、1FPS(1000ms/フレーム)で約30秒分の統計を保持。
		/// 古いサンプルは Enqueue 時に自動的に Dequeue される（RecordCaptureLatency 内で実装）。
		/// </summary>
		private readonly Queue<long> _captureLatencyHistory = new Queue<long>(30);

		// （プロファイリングログの頻度抑制用）
		/// <summary>
		/// 正常系プロファイリングログの出力頻度を抑制するためのフレームカウンター
		/// 100フレームに1回のみ出力（ログI/O負荷軽減）
		/// </summary>
		private int _sendFrameCounter;

		// （二重レート計測ゲート: 配信本数/固有本数/重複本数の 10 秒窓集計）
		// 読み書きは中央キャプチャループのスレッドのみ（RecordCaptureLatency も同ループから呼ばれる）ため追加のロックは不要
		/// <summary>エンジン stats encodeCount の前回サンプル値（-1=未サンプリング）</summary>
		private long _t6bLastEncodeCount = -1;
		/// <summary>エンジン stats rafCount の前回サンプル値（-1=未サンプリング）</summary>
		private long _t6bLastRafCount = -1;
		/// <summary>窓内でセッションへ配信（ファンアウト）したフレーム本数</summary>
		private long _t6bDeliveries;
		/// <summary>集計窓の開始時刻（10 秒ごとに確定しサマリ文字列へ反映）</summary>
		private DateTime _t6bWindowStart = DateTime.UtcNow;
		/// <summary>直近確定窓のサマリ（[Profile] まとめログへ併記。窓未確定時は空）</summary>
		private string _t6bSummary = "";

		// （フェーズ5：中央キャプチャループのキャンセルトークン）
		// volatile 宣言により、スレッド間の変数可視性を保証
		// ダブルチェックロックパターンが正しく機能するために必須
		private volatile CancellationTokenSource _centralCaptureCts;
	
	
		// （フェーズ5：中央キャプチャループのタスク参照）
		// volatile 宣言により、スレッド間の変数可視性を保証
		private volatile Task _centralCaptureTask;

		// 複数クライアントの同時接続時に _centralCaptureCts / _centralCaptureTask の
		// 再初期化が競合するのを防ぐためのロックオブジェクト
		private static readonly object _captureStartLock = new object();

		// 再起動時 / 新規接続時にフルキーフレームを強制取得するフラグ
		// ※ CefSharp JS側が「画面変化なし」として type == "none" を返す場合、
		//   _sharedFrameBuffer が更新されず、新規接続クライアントの画面がブラックアウトするのを防ぐ
		private volatile bool _forceNextKeyFrame = false;

		// クライアントプロトコルバージョン管理
		// 各WebSocketセッションのクライアントプロトコルバージョンをスレッドセーフに管理
		// null = バージョン未決状態（接続確立後5秒以内に PROTOCOL_VERSION 応答待ち）
		private readonly ConcurrentDictionary<string, int?> _clientVersions = new ConcurrentDictionary<string, int?>();

		// バージョン検出タイムアウト（5秒）
		private const int ProtocolVersionTimeoutMs = 5000;

		// （ゾンビクリーンアップ間隔：5秒）
		private DateTime _lastZombieCleanupTime = DateTime.MinValue;

		/// <summary>
		/// 非推奨（後方互換用の参照値）: 旧経路（POST /capture-mode）で最後に要求されたモード。
		/// パイプラインの起動・停止はゲートせず、実効的な配信経路はセッション単位の
		/// WebSocketSession.ClientMode で判定する（WebP/WCS の混合接続対応のため）。
		/// </summary>
		private volatile CaptureMode _currentCaptureMode = CaptureMode.WebP;

		/// <summary>
		/// 最終 HTTP ポーリング（GET /screen/diff）受信時刻（UtcNow.Ticks）。
		/// HTTP ハンドラスレッドが書き込み、キャプチャループ／起動判定スレッドが読む。
		/// long は volatile 修飾不可のため Volatile.Read/Write で可視性と 64bit アトミック性を保証する
		/// （32bit プロセスでの分割読み取り防止）。
		/// </summary>
		private long _lastHttpPollTicks = 0;

		/// <summary>
		/// HTTP ポーリングの活性窓（10秒）。WS 非対応の旧クライアントが HTTP ポーリングだけで
		/// 視聴している間に中央キャプチャループを停止させないための猶予。
		/// </summary>
		private const long HttpPollActiveWindowTicks = TimeSpan.TicksPerSecond * 10;

		// WebCodecs 状態管理
		private volatile int _webCodecsBitrate = 800000; // 800 kbps 標準
		private volatile int _webCodecsFps = 30;

		// WebCodecs 中央キャプチャループのタスク参照
		private volatile Task _webCodecsCaptureTask;

		// WebCodecs キャプチャループの CancellationTokenSource
		private CancellationTokenSource _webCodecsCaptureCts;

		// InitializeWebCodecsEncoderAsync の並行呼び出し直列化用シグナル
		// force=true 経路が複数スレッドから同時呼ばれる場合、_isWebCodecsInitialized の書き込みと
		// 初期化処理（initEncoder() 呼び出し）の間に他スレッドが侵入して二重初期化（initEncoder 多重呼び出し、
		// SPS/PPS 再送の二重化）が起き得るため、SemaphoreSlim(1,1) でメソッド全体を直列化する。
		// 宣言位置: _webCodecsCaptureCts の直後（WebCodecs 状態管理フィールド群と一括）
		private readonly SemaphoreSlim _webCodecsInitSemaphore = new SemaphoreSlim(1, 1);

		// Dispose フラグ（_webCodecsInitSemaphore の Dispose 後の WaitAsync 呼び出しを防止する）
		// Dispose() で true に設定され、InitializeWebCodecsEncoderAsync の入口でチェックされる。
		// volatile で宣言し、Dispose スレッドと初期化呼び出しスレッド間で即座に可視化する。
		private volatile bool _webCodecsDisposed;

		// 最終受信した SPS/PPS (description) バイト列のキャッシュ
		// 新接続クライアント（DescriptionSent=false）への追隨送信、および reinit_decoder 受信時の
		// 再送信に使用する。JS 側の SPS/PPS は同一セッション内で不変のためキャッシュ再利用が可能
		// 不変性保証: volatile は参照の可視性のみ保証し byte[] 中身は保証しないため、
		// キャッシュ登録時に防御コピー（WebCodecsCaptureLoopAsync 内）を行い、登録後は読み取り専用として扱う。
		// 送信側はローカルスナップショット参照 + BlockCopy コピー送信により、キャッシュ差し替えと無関係に安全
		private volatile byte[] _lastWebCodecsDescription;


		// インメモリセッションストア（スレッドセーフ）
		private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new ConcurrentDictionary<string, SessionInfo>();

		// リフレッシュトークンストア（スレッドセーフ）
		private readonly ConcurrentDictionary<string, RefreshTokenInfo> _refreshTokens = new ConcurrentDictionary<string, RefreshTokenInfo>();

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

		// （セッション分離対応）
		// 共有のカーソル座標フィールドを廃止し、各セッション固有のカーソル管理へ移行
		// HTTPモード用のカーソル状態（セッションIDベース）
		private readonly ConcurrentDictionary<string, HttpCursorState> _httpCursorStates = new ConcurrentDictionary<string, HttpCursorState>();
		private DateTime _lastHttpCursorCleanupTime = DateTime.UtcNow;

		/// <summary>
		/// 期限切れのHTTPカーソルセッションを削除します。
		/// 最終アクセスから5分経過したセッションを淘汰します。
		/// </summary>
		private void CleanupExpiredHttpCursorSessions()
		{
			var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
			foreach (var kvp in _httpCursorStates)
			{
				if (kvp.Value.LastAccessTime < cutoffTime)
				{
					_httpCursorStates.TryRemove(kvp.Key, out _);
				}
			}
		}

		/// <summary>
		/// 必要に応じてHTTPカーソルセッションのクリーンアップを実行します。
		/// 前回のクリーンアップから1分以上経過している場合に実行。
		/// </summary>
		private void CleanupExpiredHttpCursorSessionsIfNeeded()
		{
			if ((DateTime.UtcNow - _lastHttpCursorCleanupTime).TotalMinutes >= 1)
			{
				CleanupExpiredHttpCursorSessions();
				_lastHttpCursorCleanupTime = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// HTTPリクエストのセッションIDからカーソル状態を取得または作成します。
		/// </summary>
		private HttpCursorState GetOrCreateHttpCursorState(string sessionId)
		{
			return _httpCursorStates.GetOrAdd(sessionId, _ => new HttpCursorState());
		}

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
		// フェーズ5: 中央キャプチャ・ファンアウト方式
		//------------------------------------------------------------------------------------
		/// <summary>
		/// 接続中のセッションの最高FPS（最小TargetIntervalMs）を取得する
		/// 結果をキャッシュし、接続数変更時のみ再計算する
		/// 0 = 無制限（60FPS制限を適用）
		/// </summary>
		private int GetMaxFpsIntervalMs()
		{
			// キャッシュヒット: 接続数が変更されていない場合はキャッシュ値を使用
			int currentCount = Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0);
			if (currentCount == _cachedSessionCount)
				return _cachedMaxFpsInterval;

			// 接続数が変更された: 再計算
			int minInterval = int.MaxValue;

			// .ToArray() による不要なアロケーションを排除
			// ConcurrentDictionary.Values の列挙はスナップショットではないが、
			// 列挙中にセッションが Dispose されても TargetIntervalMs の読み取りは安全（volatile）
			// セッションが破棄された場合はループの次の反復で自然に除外される
			foreach (var session in _webSocketSessions.Values)
			{
				int interval = session.TargetIntervalMs;
				if (interval <= 0) interval = 16; // 60FPS制限
				if (interval < minInterval)
					minInterval = interval;
			}

			// セッションなしの場合はデフォルト100ms (10FPS)
			int result = minInterval == int.MaxValue ? 100 : minInterval;

			// キャッシュを更新
			_cachedMaxFpsInterval = result;
			_cachedSessionCount = currentCount;

			return result;
		}

		/// <summary>
		/// WebP 経路（ClientMode=0）のセッション数を返す。
		/// 中央キャプチャループのコンシューマ判定に使用する（列挙中にセッションが Dispose されても
		/// ClientMode の読み取りは安全（volatile））
		/// </summary>
		private int CountWebpClientSessions()
		{
			int count = 0;
			foreach (var session in _webSocketSessions.Values)
			{
				if (session.ClientMode == 0) count++;
			}
			return count;
		}

		/// <summary>
		/// WebCodecs 経路（ClientMode=1）のセッションが1つ以上あるかを返す。
		/// WCS パイプラインのコンシューマ判定に使用する
		/// </summary>
		private bool HasWcsClientSessions()
		{
			foreach (var session in _webSocketSessions.Values)
			{
				if (session.ClientMode == 1) return true;
			}
			return false;
		}

		/// <summary>
		/// HTTP ポーリングが活性中（直近 10 秒以内に受信）かを返す。
		/// WS を開かない旧クライアントの視聴中にパイプラインを止めないための判定
		/// </summary>
		private bool IsHttpPollingActive()
		{
			long last = Volatile.Read(ref _lastHttpPollTicks);
			if (last == 0) return false;
			return (DateTime.UtcNow.Ticks - last) <= HttpPollActiveWindowTicks;
		}

		/// <summary>
		/// WebP パイプラインのコンシューマ（WS の WebP セッション or 活性中の HTTP ポーリング）
		/// が存在するかを返す。コンシューマ不在なら中央キャプチャループは起動不要
		/// </summary>
		private bool HasWebpConsumers()
		{
			return CountWebpClientSessions() > 0 || IsHttpPollingActive();
		}

		/// <summary>
		/// アクティブなWebSocketセッションの数を返す
		/// </summary>
		private int GetActiveSessionCount()
		{
			return _webSocketSessions.Count;
		}

		/// <summary>
		/// Heartbeatタイムアウト（30秒）およびWebSocket切断状態のゾンビセッションをクリーンアップする。
		/// CentralCaptureLoopAsync で集約実行（各セッションループからの呼び出しは不要）。
		/// WebSocketState をチェックして即時切断を検知し、
		/// RemoveWebSocketSession を使用してリソースを確実に解放する。
		/// </summary>
		private void CleanupZombieSessions()
		{
			// 5秒に1回の実行で十分
			if ((DateTime.UtcNow - _lastZombieCleanupTime).TotalSeconds < 5)
				return;

			_lastZombieCleanupTime = DateTime.UtcNow;

			var cutoffTime = DateTime.UtcNow.AddSeconds(-30);

			// .ToList() によりスレッドセーフなコピーを作成してから列挙・削除
			// 列挙中にセッションが Dispose されて ConcurrentDictionary から削除されても安全
			foreach (var kvp in _webSocketSessions.ToList())
			{
				var session = kvp.Value;
				bool shouldRemove = false;

				// 1. Heartbeat タイムアウト（30秒経過）
				if (session.LastHeartbeat < cutoffTime)
				{
					Log(2, $"[RemoteController] ゾンビセッション切断 (Heartbeatタイムアウト): SessionID={session.SessionId}");
					shouldRemove = true;
				}

				// 2. WebSocketの即時切断検知（StateがOpen以外）
				// 既にファンアウト/受信ループが終了しているはずだが、念のため
				if (session.WebSocket.State != WebSocketState.Open && session.WebSocket.State != WebSocketState.Connecting)
				{
					Log(2, $"[RemoteController] ゾンビセッション切断 (WebSocket非Open): SessionID={session.SessionId}, State={session.WebSocket.State}");
					shouldRemove = true;
				}

				// 3. ProtocolVersionTimeoutMs によるフォールバック
				// 設計書 §5.3.3 定義: 接続確立後5秒以内に PROTOCOL_VERSION 応答待ち
				// タイムアウト時は Version 1 (6Bヘッダー) フォールバック
				//
				// TOCTOU 競合解消:
				// 旧実装では GetClientVersion → SetClientVersion の2ステップで TOCTOU (Time-of-Check to Time-of-Use)
				// 競合が存在し、クライアントが PROTOCOL_VERSION メッセージを送信した直後にフォールバックが
				// 実行されると Version 2 が Version 1 で上書きされる可能性があった。
				// 修正案: TrySetClientVersionIfNull による原子操作に置換。null の時のみ Version 1 を設定。
				if (!shouldRemove)
				{
					var elapsed = (DateTime.UtcNow - session.ConnectionStartTime).TotalMilliseconds;
					if (elapsed >= ProtocolVersionTimeoutMs)
					{
						// 原子的にバージョンが未設定（null）の時のみ Version 1 を設定
						// 既に PROTOCOL_VERSION メッセージを受信済み（値が設定済み）の場合は既存値を維持
						bool wasNull = TrySetClientVersionIfNull(session.SessionId, 1);
						if (wasNull)
						{
							Log(2, $"[RemoteController] プロトコルバージョン検出タイムアウト → Version 1 フォールバック (SessionID={session.SessionId}, 経過={elapsed}ms)");
						}
					}
				}

				if (shouldRemove)
				{
					RemoveWebSocketSession(session.SessionId);
				}
			}
		}

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
			bool becameValid;
			lock (_hudLock)
			{
				// 空→有効への遷移判定をキャッシュ更新前に捕捉する
				bool wasValid = IsHudJsonValid(_cachedHudJson);
				_cachedHudJson = hudJson;
				becameValid = !wasValid && IsHudJsonValid(hudJson);

				// 各セクションの抽出と個別バージョン更新チェック
				UpdateSectionVersion(hudJson, "material", ref _lastMaterialJson, ref _materialVersion);
				UpdateSectionVersion(hudJson, "fleets", ref _lastFleetsJson, ref _fleetsVersion);
				UpdateSectionVersion(hudJson, "battle", ref _lastBattleJson, ref _battleVersion);
				UpdateSectionVersion(hudJson, "combat", ref _lastCombatJson, ref _combatVersion);
				UpdateSectionVersion(hudJson, "quests", ref _lastQuestsJson, ref _questsVersion);
				UpdateSectionVersion(hudJson, "logs", ref _lastLogsJson, ref _logsVersion);
			}

			// HUD データが空→有効へ遷移した瞬間のみ、全接続セッションへ init_hud を 1 回ブロードキャストする。
			// データ存在前に接続したセッションは data:null マーカーを受信済みのため、
			// この遷移通知が無いと再接続するまで HUD パネルが空のままになる（毎秒の再送はしない）
			if (becameValid)
			{
				string packet = "{\"type\":\"init_hud\",\"data\":" + hudJson + "}";
				// 本メソッドは同期 IPC エントリであり await できないため fire-and-forget。
				// broadcast はセッション単位で例外を内部 catch するため、このタスクは fault しない
				_ = BroadcastControlToAllSessionsAsync(packet);
			}
		}

		/// <summary>
		/// HUD キャッシュの有効性を判定します（非空かつ JSON オブジェクト形式 "{...}"）。
		/// init_hud パケット構築と遷移判定で同一基準を共有します。
		/// </summary>
		private static bool IsHudJsonValid(string hudJson)
		{
			if (string.IsNullOrEmpty(hudJson)) return false;
			var trimmed = hudJson.Trim();
			return trimmed.StartsWith("{") && trimmed.EndsWith("}");
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
				// 専用ファイルへ記録（本体ブラウザログ eolog.log と分離）
				RemoteDedicatedLog.Controller.Append(
					$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][P{priority}] {message}");
				// 注: _parent?.AddLog(...) を呼び出さない（eolog.log への混在・UIログウィンドウへの
				// 出力を防止する意図）。トレードオフは Q1、ログ参照手順は §12（運用）参照。
			}
			catch (Exception ex)
			{
				// ログ出力失敗（Console 失敗・専用ファイル書き込み失敗）をサイレントに握りつぶさず記録。
				try
				{
					Console.Error.WriteLine($"[RemoteController] Log出力失敗: {ex.GetType().Name}: {ex.Message}");
				}
				catch (Exception innerEx)
				{
					// 最終フォールバック: Console 出力すら失敗した場合（極めて稀）は Debug 出力で記録。
					// この階層まで失敗するとフォールバック先が存在しないため、Debug.WriteLine までで終端とする。
					System.Diagnostics.Debug.WriteLine(
						$"[RemoteController] Log出力失敗: {ex.Message} / フォールバック失敗: {innerEx.Message}");
				}
			}
		}

		/// <summary>
		/// （リッスンアドレスパラメータの追加）
		/// （パスワードパラメータの追加）
		/// （WebCodecs 設定パラメータの追加）
		/// </summary>
		public RemoteController(FormBrowser parent, int port = 8080, string address = "*", string password = "", int webCodecsBitrate = 800000, int webCodecsFps = 30)
		{
			// 専用ログ初期化（ログディレクトリ作成・保持期間外ファイルのクリーンアップ）。
			// コンストラクタ先頭（_sharedFrameBuffer 生成のラムダが Log を呼ぶ可能性より前）に配置。
			RemoteDedicatedLog.Init();
			_parent = parent;
			_port = port;
			_address = address;
			_password = password;
			_sharedFrameBuffer = new SharedFrameBuffer(
				negativeCount => Log(3, $"[RemoteController] 参照カウント不整合: _referenceCount = {negativeCount} (二重解放の可能性があります)"));

			// WebCodecs 設定値をコンストラクタ引数から読み込む
			_webCodecsBitrate = webCodecsBitrate;
			_webCodecsFps = webCodecsFps;
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

				// （複数IPアドレス・IPv6対応のプレフィックス構築とバインド）
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
					
					// （IPv6の netsh 登録エラー50回避のための置き換え案内）
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

				// （[::]使用時のエラー原因についての親切な案内文生成）
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
					// ハードコードされたif文を静的HashSet(s_authenticatedApiPaths)で管理
					if (s_authenticatedApiPaths.Contains(path))
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
					await HandleControlAsync(req, res);
				}
				// クライアント中継イベントログ API（HTTP モード時の gesture/action/console/move_summary 送信先）
				else if (path == "/event-log")
				{
					await HandleEventLogAsync(req, res);
				}
				else if (path == "/hud")
				{
					// モバイル端末からのHUD情報要求に対し、実際のゲーム内ステータスをJSONで返します
					SendHudData(req, res);
				}
				// WebCodecs モード管理エンドポイント
				// 認証チェックは一元化されたガードで実施済み（重複排除）
				else if (path == "/capture-mode")
				{
					await HandleCaptureModeAsync(req, res);
				}
				else if (path == "/capture-config")
				{
					SendCaptureConfig(res);
				}
				else if (path == "/login-state")
				{
					// ログイン状態（ログアウト空白ページ状態）を JSON で返却
					SendLoginState(res);
				}
				// サーバ側ログ記録 ON/OFF API
				else if (path == "/log-toggle")
				{
					await HandleLogToggleAsync(req, res);
				}
				else if (path == "/logout-page")
				{
					// ログアウト中画面（自ホスト logout.html、remote モード）を配信
					SendLogoutPage(res);
				}
				else if (path == "/ws")
				{
					// WebSocket アップグレード要求
					await HandleWebSocketAsync(context);
				}
				// 静的ファイルサーブ（CSS/JS）
				else if (path.StartsWith("/css/") || path.StartsWith("/js/"))
				{
					// パスから相対パスを抽出（../ によるディレクトリトラバーサル防止）
					string relativePath = path.TrimStart('/', '\\');
	
					// 許可されたディレクトリ配下のファイルのみサーブ
					string basePath = GetStaticFileBasePath();
						// debug_static.log を廃止し、専用ファイル（remotecontroller_YYYYMMDD.log）へ統合
						if (string.IsNullOrEmpty(basePath))
						{
							Log(1, $"[StaticFile] GetStaticFileBasePath returned null for: {path}");
							res.StatusCode = 500;
							res.Close();
							return;
						}
					string fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
	
					// セキュリティチェック：basePath 配下か確認
					string basePathFull = Path.GetFullPath(basePath);
					// basePathFull は既に末尾にパス区切り文字を含む場合があるため、Normalize
					string searchPrefix = basePathFull.EndsWith(Path.DirectorySeparatorChar.ToString())
						? basePathFull
						: basePathFull + Path.DirectorySeparatorChar;
					bool isSafe = fullPath.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase) ||
						fullPath.Equals(basePathFull, StringComparison.OrdinalIgnoreCase);
					Log(1, $"[StaticFile] Static file: path={path}, basePathFull={basePathFull}, fullPath={fullPath}, isSafe={isSafe}");
					if (isSafe)
					{
						SendStaticFile(req, res, fullPath);
					}
					else
					{
						Log(1, $"[StaticFile] Static file BLOCKED: {path}");
						res.StatusCode = 403;
						res.Close();
					}
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

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : SetClientVersion
		// 概要           : クライアントのプロトコルバージョンを設定する。
		//------------------------------------------------------------------------------------
		private void SetClientVersion(string sessionId, int version)
		{
			_clientVersions[sessionId] = version;
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : GetClientVersion
		// 概要           : クライアントのプロトコルバージョンを取得する（null = 未決）。
		//------------------------------------------------------------------------------------
		private int? GetClientVersion(string sessionId)
		{
			_clientVersions.TryGetValue(sessionId, out int? version);
			return version;
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : RemoveClientVersion
		// 概要           : クライアントのプロトコルバージョンエントリを削除する。
		//                  WebSocket 切断時（OnClose/OnError）に呼び出す。
		//------------------------------------------------------------------------------------
		private void RemoveClientVersion(string sessionId)
		{
			_clientVersions.TryRemove(sessionId, out int? _);
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : TrySetClientVersionIfNull
		// 概要           : バージョンが未設定（null）の時のみ、指定されたバージョンを原子的に設定する。
		// CleanupZombieSessions と SetClientVersion 間の TOCTOU
		//                  (Time-of-Check to Time-of-Use) 競合を解消するため、Get→Set の2ステップ
		//                  を ConcurrentDictionary.AddOrUpdate による単一原子操作に置換。
		//                  設計書 §5.3.3 定義: フォールバック時のトランザクション保護
		//------------------------------------------------------------------------------------
		/// <summary>
		/// セッションのバージョンが null の時のみ、指定されたバージョンを原子的に設定する。
		/// 既にバージョンが設定されている場合は既存値を維持する。
		/// </summary>
		/// <param name="sessionId">セッションID</param>
		/// <param name="version">設定するバージョン（null 時のフォールバック値）</param>
		/// <returns>バージョンが設定された（true = 未設定だったため更新、false = 既に設定済み）</returns>
		private bool TrySetClientVersionIfNull(string sessionId, int version)
		{
			// AddOrUpdate による原子操作：
			//   1. エントリが存在しない → version を設定 (addValue)
			//      ※ 接続直後のエントリは null 初期化されているため、
			//        実際にはこのパスは通らない（既にエントリが存在するため）。
			//        安全のため、null ではなく version を設定する（フォールバック値として機能）。
			//   2. エントリが存在する → 既存値が null の場合のみ version に更新、既に値ありは維持 (updateValueFactory)
			//
			//   AddOrUpdate の戻り値は「設定された新しい値」であり、更新が成功したかどうかっていうわけではない。
			//   version は int 型（null ではない）なので、result != null は常に true になってしまい、
			//   既にバージョンが設定されていた場合でも true を返してしまう不具合があった。
			//   修正案: TryUpdate と同じロジックを使用し、既存値が null だったかを確認する。
			
			// 既存値を取得（null かどうかを確認）
			int? existingVersion = GetClientVersion(sessionId);
			if (existingVersion != null)
			{
				// 既にバージョンが設定されている場合は何もしない
				return false;
			}
			
			// 既存値が null の場合のみ、AddOrUpdate でバージョンを設定
			_clientVersions.AddOrUpdate(
				sessionId,
				addValue: version,
				updateValueFactory: (key, existing) => existing == null ? version : existing
			);
			return true;
		}

		public void Dispose()
		{
			_isRunning = false;
			_listener?.Stop();
			_listener?.Close();
			_sessions.Clear();
			_refreshTokens.Clear();

			// 中央キャプチャループのキャンセル（フェーズ5）
			// UIスレッドでの .Wait() による同期ブロックは厳禁
			// CancellationTokenSource.Cancel() による非同期キャンセルのみに変更
			if (_centralCaptureCts != null && !_centralCaptureCts.IsCancellationRequested)
			{
				_centralCaptureCts.Cancel();
				_centralCaptureCts.Dispose();
			}

			// WebCodecs キャプチャループのキャンセル
			if (_webCodecsCaptureCts != null && !_webCodecsCaptureCts.IsCancellationRequested)
			{
				_webCodecsCaptureCts.Cancel();
				_webCodecsCaptureCts.Dispose();
			}

			// WebCodecs 初期化セマフォ（_webCodecsInitSemaphore）の Dispose:
			// SemaphoreSlim は IDisposable を実装しており、公式ドキュメントに従い破棄して内部リソースを解放する。
			// 先に _webCodecsDisposed を true（volatile）にして以降の InitializeWebCodecsEncoderAsync 入口をブロックし、
			// フラグチェックを通過済み・WaitAsync 直前という TOCTOU 競合は InitializeWebCodecsEncoderAsync 入口の
			// ObjectDisposedException catch（ログ出力・握りつぶしなし）で吸収する。
			_webCodecsDisposed = true;
			_webCodecsInitSemaphore.Dispose();

			// タスク参照をローカル変数にキャプチャしてから監視
			var task = _centralCaptureTask;
			if (task != null)
			{
				// 5秒以内にタスクが完了するのを非同期で確認（UIスレッドをブロックしない）
				var completionCheck = Task.Run(async () =>
				{
					try
					{
						if (await Task.WhenAny(task, Task.Delay(5000)) == task)
						{
							if (task.IsFaulted && task.Exception != null)
							{
								Log(3, $"[RemoteController] 中央キャプチャループが異常終了しました: {task.Exception.InnerException?.Message}");
							}
							else
							{
								Log(2, "[RemoteController] 中央キャプチャループが正常に終了しました");
							}
						}
						else
						{
							Log(2, "[RemoteController] 中央キャプチャループの終了がタイムアウトしました（プロセス終了により解放される）");
						}
					}
					catch (Exception ex)
					{
						Log(3, $"[RemoteController] タスク終了確認中の例外: {ex.Message}");
					}
				});
			}

			// _webCodecsCaptureTask の終了監視を追加
			// _centralCaptureTask と同様に 5秒以内に完了するのを非同期で確認
			var webCodecsTask = _webCodecsCaptureTask;
			if (webCodecsTask != null)
			{
				var webCodecsCompletionCheck = Task.Run(async () =>
				{
					try
					{
						if (await Task.WhenAny(webCodecsTask, Task.Delay(5000)) == webCodecsTask)
						{
							if (webCodecsTask.IsFaulted && webCodecsTask.Exception != null)
							{
								Log(3, $"[RemoteController] WebCodecs キャプチャループが異常終了しました: {webCodecsTask.Exception.InnerException?.Message}");
							}
							else
							{
								Log(2, "[RemoteController] WebCodecs キャプチャループが正常に終了しました");
							}
						}
						else
						{
							Log(2, "[RemoteController] WebCodecs キャプチャループの終了がタイムアウトしました（プロセス終了により解放される）");
						}
					}
					catch (Exception ex)
					{
						Log(3, $"[RemoteController] WebCodecs タスク終了確認中の例外: {ex.Message}");
					}
				});
			}

			// WebSocket セッションのクリーンアップ
			// .ToList() でスレッドセーフなコピーを作成してから処理
			foreach (var kvp in _webSocketSessions.ToList())
			{
				RemoveWebSocketSession(kvp.Key);
			}

			// _activeConnectionCount をリセット（念のため）
			Interlocked.Exchange(ref _activeConnectionCount, 0);

			// SharedFrameBuffer のクリーンアップ（フェーズ6）
			// 参照カウントが0になっていないフレームも含めて強制的に ArrayPool へ返却
			_sharedFrameBuffer.Dispose();

			// _webSocketSessions は RemoveWebSocketSession で既に空になっていることを確認
			_webSocketSessions.Clear();
		}

		//====================================================================================
		// 処理ブロック名 : CentralCaptureLoopAsync
		// 概要           : 単一の中央キャプチャタスクでCefSharpから画面をキャプチャし、
		//                  共有フレームバッファに格納してから全セッションに通知する。
		//                  CefSharpへのキャプチャ要求は常に1回/フレーム（O(1)負荷）。
		//------------------------------------------------------------------------------------
		//------------------------------------------------------------------------------------
		private async Task CentralCaptureLoopAsync(CancellationToken token)
		{
			Log(2, "[RemoteController] 中央キャプチャループを開始しました");
			bool isFirstFrameAfterStart = true;

			var sw = new System.Diagnostics.Stopwatch();
			long nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			// [FPS Fix] ミリ秒→ティック変換の丸め誤差を排除
			// Frequency は通常 10,000,000 Hz 程度
			// double で計算することで、整数除算の切り捨て誤差を排除
			double ticksPerMs = (double)System.Diagnostics.Stopwatch.Frequency / 1000.0;
			int lastTargetInterval = GetMaxFpsIntervalMs();

			try
			{
				while (!token.IsCancellationRequested)
				{
					// 省電力化: 接続数が0台になったら即座にループを抜ける
					// ※ 接続数チェックと _centralCaptureTask のクリアを lock 内でアトミックに実行し TOCTOU 競合を完全防止
					if (Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0) <= 0)
					{
						lock (_captureStartLock)
						{
							if (Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0) <= 0)
							{
								// CTS も Cancel して「ループ不在」状態を
								// (cts==null || cts.Cancelled || task==null || task.Completed) に統一する。
								// これにより起動判定（B-2 のヘルパー）が確実に再起動をトリガーする。
								// 旧 CTS の Dispose は省略し GC に委譲
								// （dotnet/runtime#105811: Dispose 後 .Token アクセスが ObjectDisposedException を
								// 投げ得るため、finally 側は try-catch で保護済み）
								Log(2, "[RemoteController] 接続クライアントが0台になったため、中央キャプチャループを停止します (CTS Cancel)");
								_centralCaptureTask = null;
								_centralCaptureCts?.Cancel();
								break;
							}
						}
					}

					// 再起動後初回、または強制フラグが立っている場合は isInit = true でフルフレームを取得
					// ※ CefSharp JS側が差分キャッシュを保持しており、接続直後に type == "none" を返すケースを防ぐ
					bool isInit = isFirstFrameAfterStart || _forceNextKeyFrame;
					if (isInit)
					{
						_forceNextKeyFrame = false;
						isFirstFrameAfterStart = false;
					}

					sw.Restart();
	
					// 画面キャプチャ（CefSharpへの要求: 1回/フレーム）
					// CaptureCanvasAsync の実行時間を Stopwatch で計測
					var swCapture = System.Diagnostics.Stopwatch.StartNew();
					var capture = await CaptureCanvasAsync(isInit);
					swCapture.Stop();
					long captureElapsedMs = swCapture.ElapsedMilliseconds;
					RecordCaptureLatency(captureElapsedMs);
					string type = capture.Item1;
					string dataUrl = capture.Item2;

					if (type == "full" && !string.IsNullOrEmpty(dataUrl))
					{
						// ArrayPool を使用して GC 負荷を低減
						// ConvertToBytesOptimized は (Buffer, ActualLength) のタプルを返し、
						// ActualLength は WebP データの正確なバイト数（ArrayPoolのバッファサイズとは異なる）
						var (webpData, actualDataLength) = ConvertToBytesOptimized(dataUrl, "data:image/webp");
						if (webpData != null && actualDataLength > 0)
						{
							// 配信本数: セッションへファンアウトしたフレーム本数（内容非変化の重複配信を含む）
							_t6bDeliveries++;
							// グローバルシーケンス番号インクリメント（int型、Interlocked.Increment対応）
							int seq = Interlocked.Increment(ref _globalSequenceNumber);
	
							// 共有バッファにセット（参照カウントは GetFrame/ReleaseFrame で管理）
							// SetFrameは内部でBuffer.BlockCopyするため、独立した内部バッファを持つ
							_sharedFrameBuffer.SetFrame(webpData, actualDataLength, seq);
	
							// [厳格な寿命管理] SetFrameは内部でBuffer.BlockCopyするため、
							// 直ちにArrayPoolへ返却可能（ファンアウトとのバッファ競合を防止）
							_bufferPool.Return(webpData);
	
							// 全WebSocketセッションにシグナルをキック
							// .ToList() によりスレッドセーフなコピーを作成してから列挙
							// 列挙中にセッションが Dispose されて _webSocketSessions から削除されても安全
							// ToArray による毎フレームのアロケーションを排除し、直接 .Values を列挙
							foreach (var session in _webSocketSessions.Values)
							{
								if (session.WebSocket.State == WebSocketState.Open)
								{
									// フレーム到着シグナル（Channelを使用し、例外発生を防止）
									// TryWrite はチャンネルが一杯でも例外を投げず、false を返す
									// [Critical修正] 設計書 §5.1.2 規定: TryWrite 失敗時はフレームドロップとして処理
									if (!session._frameSignal.Writer.TryWrite(seq))
									{
										// session._droppedFrameCount を使用（セッションスコープでドロップ回数を累積）
										// 設計書ではメソッドスコープの droppedCount を規定しているが、
										// セッション全体のドロップ回数の累積統計は運用上のデバッグ・監視に有用なため、
										// セッションスコープのフィールドとして実装している（設計書整合性の明記）
										session._droppedFrameCount++;
										// 設計書 §5.3.2 規定: キュー溢れ警告ログ出力（M1対応）
										Log(3, $"[RemoteController] _frameSignal queue full, dropping frame {seq} (session={session.SessionId}, dropped={session._droppedFrameCount})");
									}
								}
							}
						}
					}

					// 二重レート計測ゲート: 10 秒窓で配信/固有/重複を確定する（エンジン stats の IPC は窓あたり 1 回）
					if ((DateTime.UtcNow - _t6bWindowStart).TotalSeconds >= 10)
					{
						await UpdateDualRateWindowAsync();
					}

					sw.Stop();

					// 動的FPS制御: 接続中の最高FPSをキャプチャFPSとする
					int targetInterval = GetMaxFpsIntervalMs();

					// FPS設定変更時のリセット
					if (targetInterval != lastTargetInterval)
					{
						nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
						lastTargetInterval = targetInterval;
					}

					// ドリフト補正タイマー
					// [FPS Fix] double → long キャストで高精度に計算（整数除算の切り捨て誤差を排除）
					nextTargetTicks += (long)(targetInterval * ticksPerMs);
					long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
					long remainingTicks = nextTargetTicks - currentTicks;
					int remainingMs = (int)(remainingTicks / ticksPerMs);

					// [FPS Fix] targetIntervalTicks を計算（リセット条件に使用）
					long targetIntervalTicks = (long)(targetInterval * ticksPerMs);

					if (remainingMs > 2)
					{
						// 残り時間が 2ms 超の場合のみ Task.Delay を呼び出す（Windowsタイマー粒度対策）
						await Task.Delay(remainingMs, token);
					}
					else if (remainingTicks < -targetIntervalTicks * 2)
					{
						// 2フレーム分以上の極端な処理遅延が発生した場合のみ目標時刻をリセット（スパイラル防止）
						// 微小な遅延（1~2ms）ではリセットせず、次フレームで自動補正させる
						nextTargetTicks = currentTicks;
					}
					// else: remainingMs <= 2 かつ、遅延が2フレーム分未満 → 次フレームで自動補正させる（何もしない）

					// ゾンビセッションクリーンアップ（中央ループで集約実行）
					CleanupZombieSessions();
				}
			}
			catch (OperationCanceledException)
			{
				// キャンセル要求による正常終了
			}
			catch (Exception ex)
			{
				// CefSharpの一時的な応答不可等の一時的なエラーでもループを継続
				Log(3, $"[RemoteController] 中央キャプチャループで例外発生（ループ継続）: {ex.Message}");
			}
			finally
			{
				// 自分のトークン（token 引数）が現在有効な CTS のトークンと一致する場合のみ null にクリアする
				// （新タスク起動で _centralCaptureCts が差し替えられた場合、旧タスクの finally はクリアしない）
				// これにより、旧タスクの遅延 finally が新タスク参照を null 上書きして
				// 「ループ稼働中なのに task==null」状態になるのを防ぐ（S3 解消）
				lock (_captureStartLock)
				{
					// null チェック先行: _centralCaptureCts == null の場合、
					// && の短絡評価で .Token アクセスが回避され、タスク参照は**クリアしない**
					// .Token アクセスを try-catch で保護
					// （dotnet/runtime#105811: Dispose 後 .Token アクセスが ObjectDisposedException を投げ得る）
					try
					{
						if (_centralCaptureCts != null && _centralCaptureCts.Token == token)
						{
							_centralCaptureTask = null;
							Log(2, "[RemoteController] 中央キャプチャループ終了: token一致でタスク参照をクリア");
						}
						else
						{
							Log(2, "[RemoteController] 中央キャプチャループ終了: token不一致（またはCTS null）でタスク参照をクリアしない");
						}
					}
					catch (ObjectDisposedException)
					{
						// 例外をサイレントに握りつぶさない（Log で記録）
						Log(3, "[RemoteController] 中央キャプチャループ終了: CTSがDispose済みのためtoken比較をスキップ");
					}
				}
				Log(2, "[RemoteController] 中央キャプチャループが完全に終了しました");
			}
		}
		
		//====================================================================================
		// 処理ブロック名 : RecordCaptureLatency
		// 概要           : CaptureCanvasAsync の実行遅延統計を記録し、
		//                  移動平均が500msを超過した場合に警告ログを出力する。
		//------------------------------------------------------------------------------------
		private void RecordCaptureLatency(long elapsedMs)
		{
			lock (_captureLatencyHistory)
			{
				_captureLatencyHistory.Enqueue(elapsedMs);
				if (_captureLatencyHistory.Count > 30)
				{
					_captureLatencyHistory.Dequeue();
				}
	
				var avg = _captureLatencyHistory.Average();
				var max = _captureLatencyHistory.Max();
				var min = _captureLatencyHistory.Min();
		
				// FPS推定値算出
				var theoreticalMaxFps = avg > 0 ? 1000.0 / avg : 0;
		
				if (avg > 500)
				{
					Log(3, $"[Profile] High IPC latency: avg={avg:F0}ms, min={min}ms, max={max}ms, theoreticalMaxFPS={theoreticalMaxFps:F1}");
				}
				else
				{
					// 正常系ログを100フレームに1回に抑制（ログI/O負荷軽減）
					if (Interlocked.Increment(ref _sendFrameCounter) % 100 == 0)
					{
						// 直近確定窓の二重レート集計（配信/固有/重複）を併記して実測裏取りを可能にする
						Log(3, $"[Profile] CaptureCanvasAsync: {elapsedMs}ms, avg={avg:F0}ms, min={min}ms, max={max}ms, theoreticalMaxFPS={theoreticalMaxFps:F1}, samples={_captureLatencyHistory.Count}{_t6bSummary}");
					}
				}
			}
		}

		//====================================================================================
		// 処理ブロック名 : UpdateDualRateWindowAsync
		// 概要           : 二重レート計測ゲート。10 秒窓ごとにエンジン stats の encodeCount 差分（固有本数＝
		//                  内容更新された lastWebp 生成回数）と窓内の配信本数から重複本数を算出し、
		//                  [Profile] まとめログへ併記するサマリ文字列を確定する。
		//                  中央キャプチャループからのみ呼ばれる（単一スレッド前提・ロック不要）。
		//------------------------------------------------------------------------------------
		private async Task UpdateDualRateWindowAsync()
		{
			var now = DateTime.UtcNow;
			double windowSec = (now - _t6bWindowStart).TotalSeconds;
			var stats = await GetCaptureEngineStatsAsync();
			if (stats != null && windowSec > 0.5)
			{
				long encodeCount = stats.Item1;
				long rafCount = stats.Item2;
				if (_t6bLastEncodeCount >= 0 && encodeCount >= _t6bLastEncodeCount)
				{
					long unique = encodeCount - _t6bLastEncodeCount;
					long rafDelta = _t6bLastRafCount >= 0 && rafCount >= _t6bLastRafCount ? rafCount - _t6bLastRafCount : -1;
					long duplicate = _t6bDeliveries - unique;
					if (duplicate < 0) duplicate = 0;
					// raf 差分を併記し、内容新鮮度低下の内訳（toBlob 律速: raf 正常・encode 低 / canvas 再描画律速: raf 自体が低）を切り分け可能にする
					_t6bSummary = $", 配信={_t6bDeliveries} 固有={unique} 重複={duplicate} (raf={rafDelta}, 窓={windowSec:F1}s)";
				}
				_t6bLastEncodeCount = encodeCount;
				_t6bLastRafCount = rafCount;
			}
			_t6bDeliveries = 0;
			_t6bWindowStart = now;
		}
	
	}
}

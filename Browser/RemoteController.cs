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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

		// 2026/07/29 新規作成（WebSocketプロトコルバージョン定数）
		// バージョン番号は将来の仕様が互換性なく変更される場合にインクリメント
		// Version 2: 7Bヘッダー (Version + Type + Flags + Seq)
		private const int WebSocketProtocolVersion = 2;

		// 2026/07/29 新規作成（最大同時接続数制限）
		private const int MaxConcurrentClients = 5;
	
		// 2026/07/29 追記（接続数制限のアトミック管理用）
		private int _activeConnectionCount; // Interlocked で管理
	
		// 2026/08/12 myi 新規作成（認証ガード対象パスリスト）
		/// <summary>
		/// 認証が必要で、未認証時に401を返すAPIパスのリスト。
		/// ハードコードされたif文ではなく静的HashSetで管理し、保守性を向上させる。
		/// </summary>
		private static readonly HashSet<string> s_authenticatedApiPaths = new HashSet<string>
		{
			"/screen.webp",
			"/hud",
			"/control",
			"/capture-mode",
			"/capture-config",
		};

		// 2026/07/29 新規作成（グローバルシーケンス番号：WebSocket/HTTP共通の単調増加int型）
		// 2026/08/13 修正（【二次レビュー Minor 修正】 seq 型整合性の明文化）
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

		// 2026/07/29 新規作成（フェーズ5：中央キャプチャ・ファンアウト方式）
		/// <summary>
		/// 中央キャプチャからの共有フレームバッファ（最新の1フレームのみ保持）
		/// 参照カウント付きで、全セッションが送信完了後にメモリを解放
		/// </summary>
		private readonly SharedFrameBuffer _sharedFrameBuffer;

		// 2026/07/29 新規作成（フェーズ5：動的FPS制御用キャッシュ）
		private int _cachedMaxFpsInterval = 100;
		private volatile int _cachedSessionCount = 0;

		// 2026/07/29 新規作成（IPCプロファイリング：案G-1/2/3）
		/// <summary>
		/// CaptureCanvasAsync の実行遅延統計を保持するリングバッファ（最大30サンプル）
		/// 移動平均/最大/最小値を算出し、500ms超過時に警告ログを出力
		/// 容量30は、10FPS(100ms/フレーム)で約3秒分、1FPS(1000ms/フレーム)で約30秒分の統計を保持。
		/// 古いサンプルは Enqueue 時に自動的に Dequeue される（RecordCaptureLatency 内で実装）。
		/// </summary>
		private readonly Queue<long> _captureLatencyHistory = new Queue<long>(30);

		// 2026/07/31 新規作成（二次レビュー修正：プロファイリングログの頻度抑制用）
		/// <summary>
		/// 正常系プロファイリングログの出力頻度を抑制するためのフレームカウンター
		/// 100フレームに1回のみ出力（ログI/O負荷軽減）
		/// </summary>
		private int _sendFrameCounter;

		// 2026/07/29 新規作成（フェーズ5：中央キャプチャループのキャンセルトークン）
		// [Task05-対策1-volatile] volatile 宣言により、スレッド間の変数可視性を保証
		// ダブルチェックロックパターンが正しく機能するために必須
		private volatile CancellationTokenSource _centralCaptureCts;
	
	
		// 2026/07/29 新規作成（フェーズ5：中央キャプチャループのタスク参照）
		// [Task05-対策1-volatile] volatile 宣言により、スレッド間の変数可視性を保証
		private volatile Task _centralCaptureTask;

		// [Task05-対策1-排他制御] 複数クライアントの同時接続時に _centralCaptureCts / _centralCaptureTask の
		// 再初期化が競合するのを防ぐためのロックオブジェクト
		private static readonly object _captureStartLock = new object();

		// [Task05-外部レビュー 指摘1] 再起動時 / 新規接続時にフルキーフレームを強制取得するフラグ
		// ※ CefSharp JS側が「画面変化なし」として type == "none" を返す場合、
		//   _sharedFrameBuffer が更新されず、新規接続クライアントの画面がブラックアウトするのを防ぐ
		private volatile bool _forceNextKeyFrame = false;

		// task28 Phase 0: クライアントプロトコルバージョン管理
		// 【ver 2.9 修正】 Critical Review 指摘6 対応: ConcurrentDictionary へ変更
		// 各WebSocketセッションのクライアントプロトコルバージョンをスレッドセーフに管理
		// null = バージョン未決状態（接続確立後5秒以内に PROTOCOL_VERSION 応答待ち）
		private readonly ConcurrentDictionary<string, int?> _clientVersions = new ConcurrentDictionary<string, int?>();

		// task28 Phase 0: バージョン検出タイムアウト（5秒）
		private const int ProtocolVersionTimeoutMs = 5000;

		// 2026/07/29 新規作成（ゾンビクリーンアップ間隔：5秒）
		private DateTime _lastZombieCleanupTime = DateTime.MinValue;

		//====================================================================================
		// IPC 最適化 - スクリプト事前構築
		//------------------------------------------------------------------------------------
		// 2026/07/27 新規作成（EvaluateScriptAsync 用スクリプトの事前構築：GC 負荷削減）

		// task28 Phase 0: WebCodecs pull 用スクリプト（ver 2.9 対応: Base64 文字列受信方式）
		private const string WebCodecsPullScript =
			"(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.pull_encoded();}})()";

		// task28 Phase 0: WebCodecs エンコーダ停止スクリプト
		private const string WebCodecsStopScript =
			"(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.stopEncoder();}})()";

		// [task19_v1 Critical修正] WebCodecs エンコーダ初期化スクリプトテンプレート（固有トークン: __CODEC__ / __BITRATE__ / __FPS__）
		// string.Format で構築しない理由: string.Format は JS リテラルブレース（{codec:'...'} 等）を
		// フォーマット項目と解釈し FormatException（「入力文字列の形式が正しくありません」）を投げるため、
		// JS に到達せず WebCodecs モードが常に WebP にフォールバックしていた。
		// verbatim 文字列 + トークン置換（string.Replace）で構築するため、ブレースのエスケープが不要で検証しやすい。
		// [task19_v1 Critical#2修正] EvaluateScriptAsPromiseAsync はスクリプトを (async function(){ <script> })() で
		// ラップし、Promise.resolve(...).then() で解決値を回収する。async function 内の式文は return しないと
		// undefined を解決するため、テンプレート先頭に return を付与し initEncoder() の Promise を回収する。
		private const string WebCodecsInitEncoderTemplate =
			@"return (function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.initEncoder({codec:'__CODEC__',width:1200,height:720,bitrate:__BITRATE__,framerate:__FPS__});}})();";

		// [task19_v1 Critical#3修正] WebCodecs encodeFrame() 呼び出しスクリプトテンプレート（固有トークン: __IS_KEY_FRAME__）
		// encodeFrame() は同期関数（Promise ではなくオブジェクトを直接返却）のため、非 Promise 版 EvaluateScriptAsync を使用。
		// 結果は pull_encoded()（_encodedChunks キュー）で回収されるため、返却値は失敗検知のみに使用。
		// （AGENTS.md: 空 catch 禁止 — engine 未初期化・例外時は警告ログを出力し、そのフレームをスキップしてループ継続）
		// canvas はスクリプト内で document.querySelector('canvas') により取得（start() / pull() と同一ソース）。
		// __IS_KEY_FRAME__ は true/false に置換（string.Replace）。
		private const string WebCodecsEncodeFrameTemplate =
			"(function(){if(window._kancolleCaptureEngine){var canvas=document.querySelector('canvas');if(!canvas){return{success:false,error:'Canvas not found'};}return window._kancolleCaptureEngine.encodeFrame(canvas,__IS_KEY_FRAME__);}})();";

		// task28 Phase 1: モード管理
		public enum CaptureMode { WebP, WebCodecs, HttpPolling }

		private volatile CaptureMode _currentCaptureMode = CaptureMode.WebP;
		private readonly object _modeLock = new object();

		// task28 Phase 1: WebCodecs 状態管理
		private volatile bool _isWebCodecsInitialized = false;
		private volatile int _webCodecsBitrate = 800000; // 800 kbps 標準
		private volatile int _webCodecsFps = 30;
		private volatile string _webCodecsCodec = "avc1.42001f"; // H.264 Baseline Level 3.1

		// task28 Phase 1: WebCodecs 中央キャプチャループのタスク参照
		private volatile Task _webCodecsCaptureTask;

		// task28 Phase 1: WebCodecs キャプチャループの CancellationTokenSource
		private CancellationTokenSource _webCodecsCaptureCts;

		// [task19 P2/P3] 最終受信した SPS/PPS (description) バイト列のキャッシュ
		// 新接続クライアント（DescriptionSent=false）への追隨送信、および reinit_decoder 受信時の
		// 再送信に使用する。JS 側の SPS/PPS は同一セッション内で不変のためキャッシュ再利用が可能
		// [task19 レビュー指摘 C1] 不変性保証: volatile は参照の可視性のみ保証し byte[] 中身は保証しないため、
		// キャッシュ登録時に防御コピー（WebCodecsCaptureLoopAsync 内）を行い、登録後は読み取り専用として扱う。
		// 送信側はローカルスナップショット参照 + BlockCopy コピー送信により、キャッシュ差し替えと無関係に安全
		private volatile byte[] _lastWebCodecsDescription;

		// task28 Phase 2: WebCodecs キャプチャループのキーフレーム間隔（2秒）
		private const int WebCodecsKeyFrameIntervalMs = 2000;

		// [task19 レビュー指摘 M1] SPS/PPS 送信バックオフ定数
		// 連続失敗回数閾値超過後、最終試行から一定期間だけ再送を抑制し、
		// ネットワーク断続時の無限再送ループ・ログスパイクを防ぐ
		private const int DescriptionSendBackoffFailThreshold = 5;
		private const int DescriptionSendBackoffMs = 5000;

		// [task19 レビュー指摘 M2/M2b] 制御メッセージレートリミット定数（reinit_decoder / switch_mode）
		// クライアント側には 5秒スロット（_clampReinitIntervalMs）が存在するが、
		// サーバー側もバグ・悪意あるクライアントの連打に対して防御する（帯域浪費・キャプチャループ再起動ストーム防止）
		private const int ReinitDecoderMinIntervalMs = 5000;
		private const int SwitchModeMinIntervalMs = 5000;

		// [task19 レビュー指摘 M3] Version 1 クライアント警告ログのダウンスープリング間隔（ms）
		// 毎フレーム出力するとログスパイクになるため、一定間隔でのみ出力する
		private const int Version1WarnIntervalMs = 10000;

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

		//====================================================================================
		// 処理ブロック名 : SharedFrameBuffer
		// 概要           : 中央キャプチャからの共有フレームバッファ（最新の1フレームのみ保持）。
		//                  参照カウント付きで、全セッションが送信完了後にメモリを解放する。
		//                  ArrayPool<byte> からレンタルされたバッファを管理し、
		//                  参照カウントが0になった瞬間に ArrayPool へ返却する。
		//                  旧フレームはペンディングリストに保持し、参照カウントが0になるまで
		//                  ArrayPool へ返却しない（Use-After-Free 防止）。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/29
		// version        : 3.00 (Use-After-Free修正, ダブルフリー防止, ペンディングフレーム追跡)
		//------------------------------------------------------------------------------------
		/// <summary>
		/// フレームバッファの情報を保持する内部クラス。
		/// 各フレームは独立した参照カウントを持ち、SetFrame で旧フレームが置き換えられた後も
		/// 参照カウントが0になるまでペンディングリストに保持される。
		/// </summary>
		private class FrameInfo
		{
			public byte[] Data;
			public int DataLength;  // 実際のデータ長（ArrayPoolのバッファサイズとは異なる）
			public int Sequence;
			public volatile int ReferenceCount;
	
			public FrameInfo(byte[] data, int dataLength, int sequence)
			{
				Data = data;
				DataLength = dataLength;
				Sequence = sequence;
				ReferenceCount = 0;
			}
		}
	
		/// <summary>
		/// 中央キャプチャからの共有フレームバッファ（最新の1フレームのみ保持）
		/// 参照カウント付きで、全セッションが送信完了後にメモリを解放
		/// ArrayPool<byte> からレンタルされたバッファを管理し、
		/// 参照カウントが0になった瞬間に ArrayPool へ返却する
		/// </summary>
		private class SharedFrameBuffer
		{
			private readonly object _lock = new object();
			private FrameInfo _current;
			// 旧フレームのリスト（参照カウントが0になるまで保持）
			private readonly List<FrameInfo> _pending = new List<FrameInfo>();
			private readonly Action<int> _onReferenceCountNegative;
			// SharedFrameBuffer 専用 ArrayPool（WebSocket送信用バッファとのメモリ領域を分離）
			private static readonly ArrayPool<byte> _framePool = ArrayPool<byte>.Create();
	
			/// <summary>
			/// SharedFrameBuffer を初期化します。
			/// </summary>
			/// <param name="onReferenceCountNegative">参照カウントが負の値になった際に呼び出されるコールバック（デバッグ用）。</param>
			public SharedFrameBuffer(Action<int> onReferenceCountNegative)
			{
				_onReferenceCountNegative = onReferenceCountNegative;
			}
	
			/// <summary>
			/// 最新のフレームデータを取得し、参照カウントを +1 します（読み取り専用）。
			/// </summary>
			/// <remarks>
			/// <para><b>役割:</b> 中央キャプチャが <see cref="SetFrame"/> で設定した最新のフレームデータをファンアウトセッションが読み取る。
			/// 中央キャプチャループは <see cref="SetFrame"/> によりフレームを設定し、本メソッドは各ファンアウトセッションがそのフレームを取得してクライアントへ送信するために使用する。</para>
			/// <para><b>スレッドセーフ性:</b> <c>lock (_lock)</c> で排他制御されており、中央キャプチャスレッドからの <see cref="SetFrame"/> と複数のファンアウトセッションからの同時呼び出し間で競合は発生しない。</para>
			/// <para><b>参照カウントの管理責任:</b> 本メソッドが参照カウントを +1 する（インクリメント側）。
			/// 呼び出し側は取得したフレームの使用完了後、必ず <see cref="ReleaseFrame(byte[])"/> を呼び出して参照カウントを -1 しなければならない（デクリメント側）。
			/// 未解放のまま <see cref="SetFrame"/> が呼ばれると、旧フレームの参照カウントが0にならずメモリーリークの原因となる。</para>
			/// <para>フレームが設定されていない場合は (null, 0) を返し、参照カウントはインクリメントしない。</para>
			/// </remarks>
			/// <returns>フレームデータ配列とシーケンス番号のタプル。</returns>
			public (byte[] Data, int DataLength, int Sequence) GetFrame()
			{
				lock (_lock)
				{
					if (_current != null)
					{
						Interlocked.Increment(ref _current.ReferenceCount);
					}
					return (_current?.Data, _current?.DataLength ?? 0, _current != null ? _current.Sequence : 0);
				}
			}
	
			/// <summary>
			/// 新しいフレームデータを設定し、旧フレームの参照カウントをデクリメントします。
			/// 旧フレームの参照カウントが0になった場合のみ ArrayPool へ返却する。
			/// 参照カウントが0より大きい場合は、旧フレームをペンディングリストに保持する。
			/// </summary>
			/// <remarks>
			/// <para><b>役割:</b> 中央キャプチャループが新規キャプチャを取得した際、共有バッファにフレームを設定する。
			/// 旧フレームがまだファンアウトセッションに使用されている場合は、ペンディングリストに保持して安全に参照カウントが0になるまで待つ。</para>
			/// <para><b>スレッドセーフ性:</b> <c>lock (_lock)</c> で排他制御されており、<see cref="GetFrame"/> や <see cref="ReleaseFrame(byte[])"/> との競合は発生しない。</para>
			/// <para><b>Use-After-Free 防止:</b> 旧フレームは即座に ArrayPool へ返却せず、
			/// 参照カウントが0になるまでペンディングリストに保持する。これにより、<see cref="GetFrame"/> で旧フレームを取得している
			/// セッションが安全に使用完了するまで待ち、解放済みメモリへのアクセスを防ぐ。</para>
			/// <para><b>参照カウントの管理責任:</b> 新フレームの参照カウントは 0 に初期化する。
			/// 参照カウントのインクリメントは <see cref="GetFrame"/> 側が担当する。
			/// 旧フレームの参照カウントは変更しない（既存セッションがそのまま使用する）。</para>
			/// <para><b>ArrayPoolバッファの独立性保証:</b> 渡されたdataをBuffer.BlockCopyで内部バッファにコピーし、
			/// 渡されたバッファを直接参照してはならない。これにより、呼び出し側はSetFrame完了後直ちに
			/// ArrayPoolへバッファを返却可能となり、ファンアウトセッションとのバッファ競合を防止する。</para>
			/// </remarks>
			/// <param name="data">ArrayPoolからレンタルされたフレームデータ</param>
			/// <param name="dataLength">実際のデータ長（dataの配列サイズより小さい可能性がある）</param>
			/// <param name="sequence">グローバルシーケンス番号</param>
			/// <returns>診断情報（SetFrame直後の内部バッファ状態）。呼び出し側が出力頻度を制御する。</returns>
			/// 返値: (hex, dataLength, bufferLength, sequence, riffOk)
			public void SetFrame(byte[] data, int dataLength, int sequence)
			{
				lock (_lock)
				{
					var oldFrame = _current;
				
					// 専用 ArrayPool (_framePool) からレンタルし、Buffer.BlockCopy で高速コピー
					// ArrayPoolから独立したメモリ領域を確保し、直接参照を排除
					// これにより呼び出し側はSetFrame完了後直ちにArrayPoolへ返却可能
					byte[] internalBuffer = _framePool.Rent(dataLength);
					Buffer.BlockCopy(data, 0, internalBuffer, 0, dataLength);
		
					// 新フレームを設定（コピーされた独立バッファを使用）
					_current = new FrameInfo(internalBuffer, dataLength, sequence);
		
#if DEBUG
					// [整合性チェック] RIFF ヘッダー検証（デバッグ用常設）
					if (dataLength >= 4 && (internalBuffer[0]!='R' || internalBuffer[1]!='I' || internalBuffer[2]!='F' || internalBuffer[3]!='F'))
					{
						Debug.WriteLine($"[SharedFrameBuffer] RIFF header corrupted: {internalBuffer[0]:X2}{internalBuffer[1]:X2}{internalBuffer[2]:X2}{internalBuffer[3]:X2}, seq={sequence}, dataLength={dataLength}");
					}
#endif
				
					// 旧フレームの処理
					if (oldFrame != null)
					{
						// 旧フレームの参照カウントが0より大きい = まだ使用中のセッションが存在
						// ペンディングリストに保持（ReleaseFrame で count==0 になったら返却）
						if (oldFrame.ReferenceCount > 0)
						{
							// 重複追加防止：既に _pending に存在する場合は追加しない
							if (!_pending.Contains(oldFrame))
							{
								_pending.Add(oldFrame);
							}
						}
						else
						{
							// 参照カウントが0 = 使用中のセッションが存在しない
							// 安全に専用 ArrayPool へ返却できる（clearArray: false）
							_framePool.Return(oldFrame.Data);
						}
					}
				}
			}
	
			/// <summary>
			/// フレーム使用完了後、指定されたデータ参照のフレームの参照カウントを -1 します（スレッドセーフ）。
			/// 参照カウントが0になった場合のみ ArrayPool へバッファを返却する。
			/// </summary>
			/// <remarks>
			/// <para><b>役割:</b> <see cref="GetFrame"/> で取得したフレームの使用完了を通知し、参照カウントを減らす。
			/// 参照カウントが0になった瞬間に ArrayPool へバッファを返却し、メモリーリークを防止する。</para>
			/// <para><b>スレッドセーフ性:</b> <c>lock (_lock)</c> で排他制御されており、<see cref="SetFrame"/> や <see cref="GetFrame"/> との競合は発生しない。</para>
			/// <para><b>参照カウントの管理責任:</b> 本メソッドが参照カウントを -1 する（デクリメント側）。
			/// <see cref="GetFrame"/> が +1 する（インクリメント側）ので、ペアとして使用される。</para>
			/// <para><b>Use-After-Free 防止:</b> 解放対象のフレームをデータ参照で特定する。これにより、<see cref="GetFrame"/> で取得したフレームが
			/// <see cref="SetFrame"/> で置き換えられた後も、正しいフレームの参照カウントがデクリメントされる。</para>
			/// <para><b>ダブルフリー防止:</b> 参照カウントが 0 に正確に等しい場合のみ ArrayPool へ返却する。
			/// 参照カウントが負の値になった場合は警告ログの出力のみとし、ArrayPool.Return を呼び出さない。</para>
			/// </remarks>
			/// <param name="data">GetFrameで取得したフレームデータ。null の場合は何もしない。</param>
			public void ReleaseFrame(byte[] data)
			{
				if (data == null)
					return;
	
				lock (_lock)
				{
					FrameInfo frame = null;
					bool isPending = false;
	
					// 現在のフレームと一致するかチェック
					if (_current != null && _current.Data == data)
					{
						frame = _current;
					}
					else
					{
						// ペンディングリストから検索（旧フレーム）
						for (int i = 0; i < _pending.Count; i++)
						{
							if (_pending[i].Data == data)
							{
								frame = _pending[i];
								isPending = true;
								break;
							}
						}
					}
	
					if (frame != null)
					{
						// 参照カウントをデクリメント
						int count = Interlocked.Decrement(ref frame.ReferenceCount);
	
						if (count < 0)
						{
							// 参照カウントが負 = 二重解放などの不整合
							// 警告ログのみ出力し、ArrayPool.Return は呼び出さない（ダブルフリー防止）
							// コールバックの例外をロック保護下で吸収
							try { _onReferenceCountNegative?.Invoke(count); } catch { /* ログ出力のみ。ロック解放を妨害しない */ }
						}
						else if (count == 0)
						{
							// 参照カウントが正確に0 = 安全に ArrayPool へ返却できる
							_framePool.Return(frame.Data);
	
							// ペンディングリストから削除
							if (isPending)
							{
								_pending.Remove(frame);
							}
						}
					}
	
					// ペンディングリストのクリーンアップ
					CleanupPending();
				}
			}
	
			/// <summary>
			/// ペンディングリストから参照カウントが0になった（または異常な負値の）フレームを ArrayPool へ返却する。
			/// </summary>
			/// <remarks>
			/// <para><b>役割:</b> ペンディングリストに保持された旧フレームのうち、
			/// 参照カウントが0になったものを ArrayPool へ返却する。</para>
			/// <para><b>呼び出し元:</b> <see cref="ReleaseFrame(byte[])"/> の末尾（lock 保護下の内部メソッド）から呼び出される。
			/// <see cref="SetFrame"/> からは直接呼び出されない。</para>
			/// <para><b>ReleaseFrame との連携:</b> <see cref="ReleaseFrame(byte[])"/> がペンディングリスト上のフレームの参照カウントを -1 した際、
			/// count == 0 ならそのフレームを即時 ArrayPool へ返却し _pending から削除する。
			/// <see cref="CleanupPending"/> は、これとは別に残存する _pending リストのフレームをスキャンし、
			/// 参照カウントが0または負の値のフレームを ArrayPool へ返却する。</para>
			/// <para><b>負値の処理:</b> 参照カウントが負の値になった場合は、二重解放などの不整合を示す。
			/// 警告ログを出力しつつ、ArrayPool へ返却してメモリを回収し、状態を正常化する。</para>
			/// </remarks>
			private void CleanupPending()
			{
				for (int i = _pending.Count - 1; i >= 0; i--)
				{
					var frame = _pending[i];
					int count = frame.ReferenceCount;
					if (count <= 0)
					{
						if (count < 0)
						{
							// 参照カウントが負 = 二重解放などの不整合状態
							// 警告ログを出力しつつ、ArrayPool へ返却してメモリを回収し状態を正常化する
							// コールバックの例外をロック保護下で吸収
							try { _onReferenceCountNegative?.Invoke(count); } catch { /* ログ出力のみ。ロック解放を妨害しない */ }
						}
						_framePool.Return(frame.Data);
						_pending.RemoveAt(i);
					}
				}
			}
	
			/// <summary>
			/// 現在のフレームとペンディングリストの全フレームを ArrayPool へ返却する。
			/// Dispose 時に呼び出され、メモリリークを確実に防止する。
			/// </summary>
			/// <remarks>
			/// <para><b>役割:</b> RemoteController.Dispose() 時に呼ばれ、
			/// 参照カウントが0になっていないフレームも含めて強制的に ArrayPool へ返却する。</para>
			/// <para><b>注意:</b> 本メソッドは最後にのみ呼び出すべきであり、
			/// 呼び出し後は本 SharedFrameBuffer の使用は不可能となる。</para>
			/// </remarks>
			public void Dispose()
			{
				lock (_lock)
				{
					// 現在のフレームを返却
					if (_current != null)
					{
						_framePool.Return(_current.Data);
						_current = null;
					}
	
					// ペンディングリストの全フレームを返却
					foreach (var frame in _pending)
					{
						_framePool.Return(frame.Data);
					}
					_pending.Clear();
				}
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
			/// WebSocket 接続確立時刻。ProtocolVersionTimeoutMs によるフォールバック判定に使用。
			/// 2026/08/13 myi 追記（task28 二次レビュー修正：バージョン検出タイムアウト対応）
			/// </summary>
			public DateTime ConnectionStartTime { get; }

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
			/// init処理専用のセマフォ（init要求の排他制御用）
			/// 1つのセッションに対して同時1つのinit処理のみ許可
			/// </summary>
			public readonly SemaphoreSlim InitSemaphore = new SemaphoreSlim(1, 1);
	
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
			/// フレームドロップカウント（TryWrite 失敗時にインクリメント、統計・ログに反映）
			/// 設計書 §5.1.2 規定によるフレームドロップの可視化用
			/// ※ 設計書 §1740-1731 ではメソッドスコープの droppedCount を規定しているが、
			///   セッション全体のドロップ回数の累積統計は運用上のデバッグ・監視に有用なため、
			///   セッションスコープのフィールドとして実装している（設計書整合性の明記）
			/// </summary>
			public int _droppedFrameCount;
	
			/// <summary>
			/// フレーム到着シグナル（Channel使用、例外ベースフロー制御排除）
			/// 【Major修正】 設計書 ver 3.9 §1728-1731 規定: キューサイズ 30 に統一
			/// 30fps で約1秒分のバッファリング。30フレーム超過で TryWrite 失敗→フレームドロップ
			/// </summary>
			public readonly Channel<int> _frameSignal = Channel.CreateBounded<int>(new BoundedChannelOptions(30)
			{
				FullMode = BoundedChannelFullMode.DropWrite, // キュー超過時は TryWrite が false を返し、ドロップ処理に委譲
				SingleReader = true,
			});
	
			/// <summary>
			/// WebSocket送信用の7バイトヘッダーバッファ（事前割り当て、再利用）
			/// Version 1byte + Type 1byte + Flags 1byte + Seq 4bytes
			/// ループ内で new byte[7] による毎フレームアロケーションを排除
			/// </summary>
			internal readonly byte[] _headerBuffer = new byte[7];
	
			//================================================================================
			// セッション固有のカーソル座標（スレッドセーフ）
			//--------------------------------------------------------------------------------
			/// <summary>
			/// このセッション固有のカーソルX座標（0.0〜1.0の相対座標）
			/// double 型はC#で volatile 非対応かつ Interlocked.CompareExchange では競合条件が存在するため、
			/// BitConverter.DoubleToInt64Bits / Int64BitsToDouble を用いた Interlocked.Exchange でアトミックに更新
			/// </summary>
			private long _cursorXBits;
	
			/// <summary>
			/// このセッション固有のカーソルY座標（0.0〜1.0の相対座標）
			/// </summary>
			private long _cursorYBits;
	
			/// <summary>
			/// カーソルX座標（読み取り専用）
			/// </summary>
			public double CursorX => BitConverter.Int64BitsToDouble(Volatile.Read(ref _cursorXBits));
	
			/// <summary>
			/// カーソルY座標（読み取り専用）
			/// </summary>
			public double CursorY => BitConverter.Int64BitsToDouble(Volatile.Read(ref _cursorYBits));
	
			/// <summary>
			/// カーソルX座標を設定する（スレッドセーフ）
			/// </summary>
			public void SetCursorX(double value)
			{
				Interlocked.Exchange(ref _cursorXBits, BitConverter.DoubleToInt64Bits(value));
			}
	
			/// <summary>
			/// カーソルY座標を設定する（スレッドセーフ）
			/// </summary>
			public void SetCursorY(double value)
			{
				Interlocked.Exchange(ref _cursorYBits, BitConverter.DoubleToInt64Bits(value));
			}

			/// <summary>
			/// [task19 P2] このセッションに SPS/PPS (description) を送信済みかどうかのフラグ
			/// WebCodecs モードでキーフレーム送信前に Type=0x02 の Description フレームを送信済みか管理する
			/// reinit_decoder 受信時に false にリセットして再送信を行う
			/// </summary>
			public volatile bool DescriptionSent;

			// [task19 レビュー指摘 M1] SPS/PPS 送信バックオフ状態（Interlocked/Volatile 管理）
			// 送信失敗の連続回数が閾値に達した場合、一定期間だけ再送を抑制する（P4a と同様の ticks 管理）
			private int _descSendFailCount;
			private long _descLastSendTicks;  // 最終 SPS/PPS 送信試行時刻（DateTime.UtcNow.Ticks）
			// [task19 レビュー指摘 M2] 最終 reinit_decoder 受信時刻（レートリミット用）
			private long _lastReinitTicks;
			// [task19 レビュー指摘 M2b] 最終 switch_mode 受信時刻（レートリミット用）
			private long _lastSwitchModeTicks;
			// [task19 レビュー指摘 M3] 最終 Version 1 警告ログ出力時刻（ダウンスープリング用）
			private long _lastV1WarnTicks;

			public WebSocketSession(System.Net.WebSockets.WebSocket ws, string sessionId)
			{
				WebSocket = ws;
				SessionId = sessionId;
				CancellationToken = new CancellationTokenSource();
				LastHeartbeat = DateTime.UtcNow;
				ConnectionStartTime = DateTime.UtcNow;
				_latestFrameBuffer = null;
				_latestFrameLength = 0;
			}

			/// <summary>
			/// [task19 レビュー指摘 M1] SPS/PPS 送信成功を記録する（失敗カウントをリセット）
			/// </summary>
			public void MarkDescriptionSentSuccess()
			{
				Interlocked.Exchange(ref _descSendFailCount, 0);
				Interlocked.Exchange(ref _descLastSendTicks, DateTime.UtcNow.Ticks);
			}

			/// <summary>
			/// [task19 レビュー指摘 M1] SPS/PPS 送信失敗を記録する（失敗カウントをインクリメント）
			/// </summary>
			public void MarkDescriptionSentFailure()
			{
				Interlocked.Increment(ref _descSendFailCount);
				Interlocked.Exchange(ref _descLastSendTicks, DateTime.UtcNow.Ticks);
			}

			/// <summary>
			/// [task19 レビュー指摘 M1] SPS/PPS の再送を許可しているかを判定する（バックオフ制御）
			/// 連続失敗が DescriptionSendBackoffFailThreshold 回に達した場合、
			/// 最終試行から DescriptionSendBackoffMs 経過するまで再送を抑制する
			/// </summary>
			public bool CanSendDescription()
			{
				int failCount = Volatile.Read(ref _descSendFailCount);
				if (failCount < DescriptionSendBackoffFailThreshold) return true;
				long lastTicks = Volatile.Read(ref _descLastSendTicks);
				return (DateTime.UtcNow.Ticks - lastTicks) >= (long)DescriptionSendBackoffMs * TimeSpan.TicksPerMillisecond;
			}

			/// <summary>
			/// [task19 レビュー指摘 M1/M2] SPS/PPS 送信状態（失敗カウント・最終試行時刻）をリセットする
			/// reinit_decoder / switch_mode(WebCodecs) 受信時に呼び、即時再送を許可する
			/// </summary>
			public void ResetDescriptionSendState()
			{
				Interlocked.Exchange(ref _descSendFailCount, 0);
				Interlocked.Exchange(ref _descLastSendTicks, 0);
			}

			/// <summary>
			/// [task19 レビュー指摘 M2] reinit_decoder のレートリミット判定（直近 ReinitDecoderMinIntervalMs 以内の連続受信を拒否）
			/// 許可された場合は時刻を記録して true を返す
			/// </summary>
			public bool TryAcceptReinitDecoder()
			{
				long nowTicks = DateTime.UtcNow.Ticks;
				long lastTicks = Volatile.Read(ref _lastReinitTicks);
				if (lastTicks != 0 && (nowTicks - lastTicks) < (long)ReinitDecoderMinIntervalMs * TimeSpan.TicksPerMillisecond)
				{
					return false;
				}
				Interlocked.Exchange(ref _lastReinitTicks, nowTicks);
				return true;
			}

			/// <summary>
			/// [task19 レビュー指摘 M2b] switch_mode のレートリミット判定（直近 SwitchModeMinIntervalMs 以内の連続受信を拒否）
			/// SetCaptureModeAsync がキャプチャループの停止・起動を伴うため、連打時の再起動ストームを防止する
			/// </summary>
			public bool TryAcceptSwitchMode()
			{
				long nowTicks = DateTime.UtcNow.Ticks;
				long lastTicks = Volatile.Read(ref _lastSwitchModeTicks);
				if (lastTicks != 0 && (nowTicks - lastTicks) < (long)SwitchModeMinIntervalMs * TimeSpan.TicksPerMillisecond)
				{
					return false;
				}
				Interlocked.Exchange(ref _lastSwitchModeTicks, nowTicks);
				return true;
			}

			/// <summary>
			/// [task19 レビュー指摘 M3] Version 1 クライアント警告ログのダウンスープリング判定
			/// 前回のログ出力から Version1WarnIntervalMs 以上経過した場合のみ true を返して時刻を記録する
			/// </summary>
			public bool TryLogVersion1Warning()
			{
				long nowTicks = DateTime.UtcNow.Ticks;
				long lastTicks = Volatile.Read(ref _lastV1WarnTicks);
				if (lastTicks != 0 && (nowTicks - lastTicks) < (long)Version1WarnIntervalMs * TimeSpan.TicksPerMillisecond)
				{
					return false;
				}
				Interlocked.Exchange(ref _lastV1WarnTicks, nowTicks);
				return true;
			}

			/// <summary>
			/// 最新フレームの到着を非同期待機します（送信タスクから呼び出し）。
			/// Channelを使用し、例外ベースのフロー制御を排除する。
			/// [Phase 5: Central Capture] 将来の中央キャプチャ機能で使用予定
			/// </summary>
			public async Task<bool> WaitForFrameAsync(int timeoutMs, CancellationToken token)
			{
				CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
				try
				{
					cts.CancelAfter(timeoutMs);
					try
					{
						await _frameSignal.Reader.ReadAsync(cts.Token);
						return true;
					}
					catch (OperationCanceledException)
					{
						return false; // タイムアウト
					}
				}
				finally
				{
					cts.Dispose();
				}
			}

			/// <summary>
			/// 最新フレームをセットします（キャプチャタスクから呼び出し）。
			/// 既存のフレームがある場合は破棄（ArrayPool へ返却）し、新しいフレームで上書き。
			/// 操作全体は _bufferLock で排他制御されておりアトミック。
			/// ChannelのTryWriteを使用し、例外ベースのフロー制御を排除する。
			/// [Phase 5: Central Capture] 将来の中央キャプチャ機能で使用予定
			/// </summary>
			public void SetLatestFrame(byte[] data)
			{
				byte[] pooledBuffer = _bufferPool.Rent(data.Length);
				Buffer.BlockCopy(data, 0, pooledBuffer, 0, data.Length);

				lock (_bufferLock)
				{
					// 既存フレームがあれば返却（ArrayPool へ）
					if (_latestFrameBuffer != null)
					{
						_bufferPool.Return(_latestFrameBuffer);
					}

					_latestFrameBuffer = pooledBuffer;
					_latestFrameLength = data.Length;
					_frameUpdated = true;
				}

				// ChannelのTryWriteでシグナルを送信（チャンネルが一杯でも例外を投げず、最新のみ保持）
				_frameSignal.Writer.TryWrite(1);
			}

			/// <summary>
			/// 最新フレームを取得してフラグをリセットします（送信タスクから呼び出し）。
			/// ArrayPool からレンタルされたバッファを ArraySegment としてそのまま返し、
			/// 送信完了後には ReturnFrame で返却することを期待する。
			/// フレームがない場合は default(ArraySegment<byte>) (Count == 0) を返す。
			/// 操作全体は _bufferLock で排他制御されておりアトミック。
			/// [Phase 5: Central Capture] 将来の中央キャプチャ機能で使用予定
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
			/// [Phase 5: Central Capture] 将来の中央キャプチャ機能で使用予定
			/// </summary>
			public void ReturnFrame(ArraySegment<byte> segment)
			{
				if (segment.Array != null && segment.Count > 0)
				{
					_bufferPool.Return(segment.Array);
				}
			}

			private volatile bool _disposed = false;
	
			public void Dispose()
			{
				// 二重解放保護（複数ループが同時に終了した場合の安全確保）
				if (_disposed) return;
				_disposed = true;
	
				CancellationToken.Cancel();
				// ChannelのWriterを完了状態にして待機中の読み取りタスクを解放
				try { _frameSignal.Writer.Complete(); } catch { }
	
				// 残っているフレームバッファを返却
				lock (_bufferLock)
				{
					if (_latestFrameBuffer != null)
					{
						_bufferPool.Return(_latestFrameBuffer);
						_latestFrameBuffer = null;
					}
				}
	
				CancellationToken.Dispose();
				SendSemaphore.Dispose();
				InitSemaphore.Dispose();
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

		// 2026/07/29 myi 修正（セッション分離対応）
		// 共有のカーソル座標フィールドを廃止し、各セッション固有のカーソル管理へ移行
		// HTTPモード用のカーソル状態（セッションIDベース）
		private readonly ConcurrentDictionary<string, HttpCursorState> _httpCursorStates = new ConcurrentDictionary<string, HttpCursorState>();
		private DateTime _lastHttpCursorCleanupTime = DateTime.UtcNow;

		/// <summary>
		/// HTTPセッションのカーソル状態を保持するクラス
		/// WebSocketセッションとは独立して管理
		/// </summary>
		private class HttpCursorState
			{
				private long _xBits;
				private long _yBits;
				private int _hasValue; // 0: false, 1: true (Interlocked 管理)
	
				// [task19 P4] LastAccessTime を long ticks（UTC）として Interlocked/Volatile 管理
				// DateTime 構造体のプレーン書き込みは 32bit 環境で Torn Read  가능성이あるため、
				// 64bit 値の原子性を保証する Interlocked.Exchange / Volatile.Read に統一
				private long _lastAccessTicks = DateTime.UtcNow.Ticks;
	
				// [task19 v5] HasValue プロパティ（外部レビュー Major 6 修正: アトミック保護維持）
				public bool HasValue => Volatile.Read(ref _hasValue) == 1;
	
				public double X => BitConverter.Int64BitsToDouble(Volatile.Read(ref _xBits));
				public double Y => BitConverter.Int64BitsToDouble(Volatile.Read(ref _yBits));
	
				/// <summary>
				/// 最終アクセス時刻（エバクションポリシーで使用）
				/// [task19 P4] Volatile.Read によるアトミック読み取り
				/// </summary>
				public DateTime LastAccessTime => new DateTime(Volatile.Read(ref _lastAccessTicks), DateTimeKind.Utc);
	
				// [task19 P4] 最終アクセス時刻をアトミックに更新（内部共通ヘルパ）
				private void UpdateLastAccessTicks()
				{
					Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
				}
	
				public void SetX(double value)
				{
					Interlocked.Exchange(ref _xBits, BitConverter.DoubleToInt64Bits(value));
					UpdateLastAccessTicks();
				}
	
				public void SetY(double value)
				{
					Interlocked.Exchange(ref _yBits, BitConverter.DoubleToInt64Bits(value));
					UpdateLastAccessTicks();
				}
	
				/// <summary>
				/// [task19] x, y をセットし HasValue を true にする
				/// </summary>
				public void Touch(double x, double y)
				{
					Interlocked.Exchange(ref _xBits, BitConverter.DoubleToInt64Bits(x));
					Interlocked.Exchange(ref _yBits, BitConverter.DoubleToInt64Bits(y));
					Interlocked.Exchange(ref _hasValue, 1);
					UpdateLastAccessTicks();
				}
	
				/// <summary>
				/// [task19] LastAccessTime のみを更新（タイムアウト状態をリセット）
				/// 命名は Touch(double,double) との紛らわしさを避けるため UpdateLastAccess とした
				/// [task19 P4] Interlocked によるアトミック更新に変更
				/// </summary>
				public void UpdateLastAccess()
				{
					UpdateLastAccessTicks();
				}
		}

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

			// [レビュー修正] .ToArray() による不要なアロケーションを排除
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
		/// アクティブなWebSocketセッションの数を返す
		/// </summary>
		private int GetActiveSessionCount()
		{
			return _webSocketSessions.Count;
		}

		/// <summary>
		/// Heartbeatタイムアウト（30秒）およびWebSocket切断状態のゾンビセッションをクリーンアップする。
		/// CentralCaptureLoopAsync で集約実行（各セッションループからの呼び出しは不要）。
		/// 2026/07/29 myi 修正: WebSocketState をチェックして即時切断を検知し、
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

				// 3. 【task28 二次レビュー修正】 ProtocolVersionTimeoutMs によるフォールバック
				// 設計書 §5.3.3 定義: 接続確立後5秒以内に PROTOCOL_VERSION 応答待ち
				// タイムアウト時は Version 1 (6Bヘッダー) フォールバック
				//
				// 【Critical修正】 TOCTOU 競合解消:
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
		/// 2026/08/12 myi 変更（WebCodecs 設定パラメータの追加）
		/// </summary>
		public RemoteController(FormBrowser parent, int port = 8080, string address = "*", string password = "", int webCodecsBitrate = 800000, int webCodecsFps = 30)
		{
			_parent = parent;
			_port = port;
			_address = address;
			_password = password;
			_sharedFrameBuffer = new SharedFrameBuffer(
				negativeCount => Log(3, $"[RemoteController] 参照カウント不整合: _referenceCount = {negativeCount} (二重解放の可能性があります)"));

			// task28: WebCodecs 設定値をコンストラクタ引数から読み込む
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
					// 【Minor修正】 ハードコードされたif文を静的HashSet(s_authenticatedApiPaths)で管理
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
				else if (path == "/hud")
				{
					// モバイル端末からのHUD情報要求に対し、実際のゲーム内ステータスをJSONで返します
					SendHudData(req, res);
				}
				// task28: WebCodecs モード管理エンドポイント
				// 【Major修正】 認証チェックは一元化されたガード（1525行目）で実施済み（重複排除）
				else if (path == "/capture-mode")
				{
					await HandleCaptureModeAsync(req, res);
				}
				else if (path == "/capture-config")
				{
					SendCaptureConfig(res);
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
					var now = DateTime.Now.ToString("HH:mm:ss.fff");
					var debugLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_static.log");
					if (string.IsNullOrEmpty(basePath))
					{
						string debugMsg = $"[{now}] GetStaticFileBasePath returned null for: {path}";
						System.IO.File.AppendAllText(debugLogPath, debugMsg + Environment.NewLine);
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
					string debugMsg2 = $"[{now}] Static file: path={path}, basePathFull={basePathFull}, fullPath={fullPath}, isSafe={isSafe}";
					System.IO.File.AppendAllText(debugLogPath, debugMsg2 + Environment.NewLine);
					if (isSafe)
					{
						SendStaticFile(req, res, fullPath);
					}
					else
					{
						string debugMsg3 = $"[{now}] Static file BLOCKED: {path}";
						System.IO.File.AppendAllText(debugLogPath, debugMsg3 + Environment.NewLine);
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
			res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' blob: data:;";
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
			// HTMLファイルは常に最新のCSS/JSの ?v= パラメータを参照する必要があるため、ブラウザキャッシュを無効化する
			res.Headers["Cache-Control"] = "no-cache, must-revalidate";
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
		private async Task HandleControlAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			try
			{
				// 2026/07/29 myi 修正（セッション分離対応）
				// HTTPモードでもセッションIDベースのカーソル状態を使用
				CleanupExpiredHttpCursorSessionsIfNeeded();

				// セッションIDを取得（Cookieから）
				string sessionId = GetCookieValue(req, "session_id");
				if (string.IsNullOrEmpty(sessionId))
				{
					sessionId = Guid.NewGuid().ToString("N");
				}

				var cursorState = GetOrCreateHttpCursorState(sessionId);
				cursorState.UpdateLastAccess();  // [task19] タイムアウトリセット（設計書 §2.2.2）
				var action = req.QueryString["action"];
				var xStr = req.QueryString["x"];
				var yStr = req.QueryString["y"];

				// 絶対割合座標 (0.0〜1.0) を受け取り、現在のポインタ座標に適用します。
				if (action == "move" && double.TryParse(xStr, out double x) && double.TryParse(yStr, out double y))
				{
					// [task19 P1] Touch(x,y) で座標更新と HasValue=true をアトミックに設定
					// 旧実装は SetX/SetY のみで HasValue が永遠に false のため、
					// TriggerMouseClickAsync のフォールバックで (0.5, 0.5) が常に優先されていた
					cursorState.Touch(Math.Max(0, Math.Min(1.0, x)), Math.Max(0, Math.Min(1.0, y)));

					await TriggerMouseMoveAsync(cursorState);
				}
				else if (action == "click")
				{
					await TriggerMouseClickAsync(cursorState);
				}
				// 2026/06/11 myi 新規作成（ゲーム画面タッチクリック）
				// ゲーム画面（viewport）をタップした位置の絶対座標を直接クリックする
				else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					// [task19 P1] Touch(x,y) で座標更新と HasValue=true をアトミックに設定（move と同様）
					cursorState.Touch(Math.Max(0, Math.Min(1.0, tx)), Math.Max(0, Math.Min(1.0, ty)));
					await TriggerMouseClickAsync(cursorState);
				}
					#region "追加アクション処理"
					// ブラウザをログインページへ移動（確認はHUD側で行うため、ここでは即座に実行）
					else if (action == "navigate_login")
					{
						_parent.NavigateToLoginPage();
					}
					// 2026/08/18 myi 新規作成（ログアウト: 本体ブラウザを空白ページへ移動）
					// Cookie は削除せず（DMMセッション維持）、空白ページ上のボタンから
					// ログインページへ戻れば自動ログインとなる。
					else if (action == "logout_blank")
					{
						_parent.LogoutToBlankPage();
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
						// 2026/07/29 myi 修正（セッション分離対応：async Task化）
						// マウスダウン（ドラッグ開始）
						else if (action == "mousedown")
						{
							await TriggerMouseDownAsync(cursorState);
						}
						// マウスアップ（ドラッグ終了）
						else if (action == "mouseup")
						{
							await TriggerMouseUpAsync(cursorState);
						}
		
						res.StatusCode = 200;
						res.Close();
					}
			catch (Exception ex)
			{
				Console.WriteLine($"[RemoteController] HandleControlAsync error: {ex.Message}");
				try
				{
					if (res.StatusCode != 500)
					{
						res.StatusCode = 500;
						var buf = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
						await res.OutputStream.WriteAsync(buf, 0, buf.Length);
					}
				}
				finally
				{
					res.Close();
				}
			}
		}

		//====================================================================================
		// 処理ブロック名 : GetBrowserCoords
		// 概要           : モバイル端末から受信した 0.0〜1.0 のゲーム画面上の相対座標を、
		//                  ブラウザコントロールの現在のサイズと艦これアスペクト比(5:3)から生じる
		//                  黒い余白（レターボックス）を動的に考慮し、実際のCefブラウザ論理ピクセル座標へ変換します。
		//                  2026/07/29 myi 修正：セッション固有のカーソル座標を使用するオーバーロードを追加
		//------------------------------------------------------------------------------------
		private Point? GetBrowserCoords(double cursorX, double cursorY)
		{
			// CefSharpブラウザコントロール全体の現在の論理サイズを取得
			double browserWidth = _parent.Browser.Width;
			double browserHeight = _parent.Browser.Height;

			// [task19 不具合2修正] ブラウザサイズが 0/極小の場合のガード
			// ブラウザが未ロード/未表示（Width/Height が 0）の場合、browserAspect が Infinity/NaN になり
			// gameWidth=gameHeight=0 で (0,0) が返る（クリックが常に左上になる原因）。
			// 座標変換の前提が崩れているため、null を返して呼び出し側でイベント送信をスキップする。
			if (browserWidth <= 0 || browserHeight <= 0)
			{
				Log(3, $"[RemoteController] GetBrowserCoords: ブラウザサイズが不正 (Width={browserWidth}, Height={browserHeight}) のため座標変換をスキップします");
				return null;
			}

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
			int x = (int)(offsetX + (cursorX * gameWidth));
			int y = (int)(offsetY + (cursorY * gameHeight));

			return new Point(x, y);
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseMoveAsync
		// 概要           : セッション固有のカーソル座標からアスペクト比補正された
		//                  CefSharp論理ピクセル座標を求め、ブラウザへ移動イベントを送信します。
		//                  2026/07/29 myi 修正：async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private Task TriggerMouseMoveAsync(HttpCursorState cursorState)
		{
			if (_parent.Browser == null) return Task.CompletedTask;
			
			// ChromiumWebBrowser から内部の IBrowser インスタンスを取得
			var browser = _parent.Browser.GetBrowser();
			// ブラウザのホストオブジェクト (IBrowserHost) を取得
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			// セッション固有のカーソル座標を使用
			var coords = GetBrowserCoords(cursorState.X, cursorState.Y);
			if (coords == null) return Task.CompletedTask;

			// マウス移動イベントオブジェクトを生成して送信します
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseMoveEvent(mouseEvent, false);

			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseClickAsync
		// 概要           : セッション固有のカーソル座標で左クリックを送信します。
		//                  Thread.Sleep を Task.Delay に変更し、スレッドブロッキングを排除。
		//                  2026/07/29 myi 修正：async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private async Task TriggerMouseClickAsync(HttpCursorState cursorState)
			{
				if (_parent.Browser == null) return;
	
				var browser = _parent.Browser.GetBrowser();
				var host = browser?.GetHost();
				if (host == null) return;
	
				// [task19 v3] HasValue フラグによるフォールバック（Major 3 修正）
				// HasValue が false の場合のみデフォルト 0.5, 0.5 を使用
				double cursorX, cursorY;
				if (cursorState.HasValue)
				{
					cursorX = cursorState.X;
					cursorY = cursorState.Y;
				}
				else
				{
					cursorX = 0.5;
					cursorY = 0.5;
				}
	
				var coords = GetBrowserCoords(cursorX, cursorY);
				if (coords == null) return;

			// 左クリックのためのマウス座標イベントを生成
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			
			// 1. マウスダウンイベント (左ボタン) の送信
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			
			// 艦これブラウザ側のクリック判定遅延を確実にカバーするため、わずかにウェイトを入れます
			// Thread.Sleep(50) → Task.Delay(50) に変更（スレッドブロッキング排除）
			await Task.Delay(50, CancellationToken.None);
			
			// 2. マウスアップイベント (左ボタン) の送信
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseDownAsync
		// 概要           : セッション固有のカーソル座標で左ボタンダウンを送信
		//                  2026/07/29 myi 修正：async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private Task TriggerMouseDownAsync(HttpCursorState cursorState)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			var coords = GetBrowserCoords(cursorState.X, cursorState.Y);
			if (coords == null) return Task.CompletedTask;
			// CefEventFlags.None を使用（公式推奨）
			// isUp=false でダウンイベントを指定
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			Console.WriteLine($"RemoteController: TriggerMouseDown @ {coords.Value.X},{coords.Value.Y}");
			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseUpAsync
		// 概要           : セッション固有のカーソル座標で左ボタンアップを送信
		//                  2026/07/29 myi 修正：async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private Task TriggerMouseUpAsync(HttpCursorState cursorState)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			var coords = GetBrowserCoords(cursorState.X, cursorState.Y);
			if (coords == null) return Task.CompletedTask;
			// CefEventFlags.None を使用（公式推奨）
			// isUp=true でアップイベントを指定
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
			Console.WriteLine($"RemoteController: TriggerMouseUp @ {coords.Value.X},{coords.Value.Y}");
			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseMoveAsync (WebSocketSession)
		// 概要           : WebSocketセッション固有のカーソル座標からマウス移動イベントを送信
		//------------------------------------------------------------------------------------
		private Task TriggerMouseMoveAsync(WebSocketSession session)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			var coords = GetBrowserCoords(session.CursorX, session.CursorY);
			if (coords == null) return Task.CompletedTask;
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseMoveEvent(mouseEvent, false);

			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseClickAsync (WebSocketSession)
		// 概要           : WebSocketセッション固有のカーソル座標で左クリックを送信
		//------------------------------------------------------------------------------------
		private async Task TriggerMouseClickAsync(WebSocketSession session)
		{
			if (_parent.Browser == null) return;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return;

			var coords = GetBrowserCoords(session.CursorX, session.CursorY);
			if (coords == null) return;
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);

			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			await Task.Delay(50, session.CancellationToken.Token);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseDownAsync (WebSocketSession)
		// 概要           : WebSocketセッション固有のカーソル座標で左ボタンダウンを送信
		//------------------------------------------------------------------------------------
		private Task TriggerMouseDownAsync(WebSocketSession session)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			var coords = GetBrowserCoords(session.CursorX, session.CursorY);
			if (coords == null) return Task.CompletedTask;
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			Console.WriteLine($"RemoteController: TriggerMouseDown @ {coords.Value.X},{coords.Value.Y}");

			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseUpAsync (WebSocketSession)
		// 概要           : WebSocketセッション固有のカーソル座標で左ボタンアップを送信
		//------------------------------------------------------------------------------------
		private Task TriggerMouseUpAsync(WebSocketSession session)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			var coords = GetBrowserCoords(session.CursorX, session.CursorY);
			if (coords == null) return Task.CompletedTask;
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, true, 1);
			Console.WriteLine($"RemoteController: TriggerMouseUp @ {coords.Value.X},{coords.Value.Y}");

			return Task.CompletedTask;
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
						string html = File.ReadAllText(path, Encoding.UTF8);
						// キャッシュバスティング: CSS/JSファイルの最終更新時刻を ?v= パラメータとして付与
						string basePath = GetStaticFileBasePath();
						html = InjectVersionQuery(html, basePath);
						return html;
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

		//====================================================================================
		// 処理ブロック名 : SendStaticFile
		// 概要           : 静的ファイル（CSS/JS）を送信します。
		//                  If-Modified-Since / 304 Not Modified に対応。
		//------------------------------------------------------------------------------------
		private void SendStaticFile(HttpListenerRequest req, HttpListenerResponse res, string filePath)
		{
			if (!File.Exists(filePath))
			{
				res.StatusCode = 404;
				res.Close();
				return;
			}

			// ファイルの最終更新時刻を取得
			DateTime lastModified = File.GetLastWriteTimeUtc(filePath);

			// If-Modified-Since のチェック
			string ifModifiedSinceHeader = req.Headers["If-Modified-Since"];
			if (!string.IsNullOrEmpty(ifModifiedSinceHeader) && DateTime.TryParse(ifModifiedSinceHeader, out var ifModifiedSince))
			{
				// 1秒単位で比較（HTTP日時の精度制限）
				if (lastModified <= ifModifiedSince)
				{
					res.StatusCode = 304; // Not Modified
					res.Close();
					return;
				}
			}

			// MIMEタイプを拡張子から決定
			string mimeType = GetMimeType(filePath);

			// Cache-Control ヘッダーの設定
			res.Headers["Cache-Control"] = "public, max-age=31536000, immutable";

			// Last-Modified ヘッダーを設定（RFC 1123 形式）
			res.Headers["Last-Modified"] = lastModified.ToString("R");

			// セキュリティヘッダー
			res.Headers["X-Content-Type-Options"] = "nosniff";

			// ファイルをストリームとして送信
			res.ContentType = mimeType + "; charset=utf-8";
			try
			{
				using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					res.ContentLength64 = fs.Length;
					fs.CopyTo(res.OutputStream);
				}
			}
			catch (IOException ex)
			{
				System.Diagnostics.Debug.WriteLine($"IOException when serving static file: {filePath} - {ex.Message}");
				res.StatusCode = 500;
				res.Close();
				return;
			}
			res.Close();
		}

		/// <summary>
		/// ファイル拡張子からMIMEタイプを返す。
		/// </summary>
		private string GetMimeType(string filePath)
		{
			string ext = Path.GetExtension(filePath).ToLower();
			switch (ext)
			{
				case ".css": return "text/css";
				case ".js": return "application/javascript";
				case ".html": return "text/html";
				default: return "application/octet-stream";
			}
		}

		//====================================================================================
		// 処理ブロック名 : GetStaticFileBasePath
		// 概要           : 静的ファイル（CSS/JS）のベースディレクトリの絶対パスを返す。
		//                  開発時はソースコード直下の Browser ディレクトリを優先検索します。
		//------------------------------------------------------------------------------------
		private string GetStaticFileBasePath()
		{
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;

			// 1. 【リリース環境優先】実行フォルダ直下に css/ または js/ があるか確認
			//    デプロイ環境（bin/Release等）ではファイルが直接配置されているため
			if (Directory.Exists(Path.Combine(baseDir, "css")) && Directory.Exists(Path.Combine(baseDir, "js")))
			{
				return Path.GetFullPath(baseDir);
			}

			// 2. 【開発用】親階層を遡って Browser ディレクトリを探索
			var dir = new DirectoryInfo(baseDir);
			while (dir != null)
			{
				try
				{
					var testPath = Path.Combine(dir.FullName, "Browser");
					// Browser ディレクトリが存在し、css または js サブディレクトリを持つ場合
					if (Directory.Exists(testPath) && (Directory.Exists(Path.Combine(testPath, "css")) || Directory.Exists(Path.Combine(testPath, "js"))))
					{
						return Path.GetFullPath(testPath);
					}
				}
				catch { }
				dir = dir.Parent;
			}

			// 3. 最終フォールバック: 実行フォルダ直下
			return Path.GetFullPath(baseDir);
		}

		//====================================================================================
		// 処理ブロック名 : InjectVersionQuery
		// 概要           : HTML内の<link>と<script>タグに?v=タイムスタンプを注入する。
		//------------------------------------------------------------------------------------
		private string InjectVersionQuery(string html, string basePath)
		{
			// 正規表現パターン:
			// 1. グループ1: 属性プレフィックス (src=やhref=を含む、イコール前後の空白を許容)
			// 2. グループ2: フルパス (/css/remote_base.css など)
			// 3. グループ3: 閉じクォート（任意）
			// 属性順序に依存しない堅牢なパターン：
			// - イコール前後の空白を許容（例: src = "/css/style.css"）
			// - クォートの有無を許容（例: src="/css/style.css" や src=/css/style.css）
			string pattern = @"((?:src|href)\s*=\s*['""]?(/[^'""]+\.(?:css|js)))(['""]?)";

			return System.Text.RegularExpressions.Regex.Replace(html, pattern, match =>
			{
				string prefix = match.Groups[1].Value;    // e.g., 'href="/'
				string fullPath = match.Groups[2].Value;   // e.g., '/css/remote_base.css'
				string suffix = match.Groups[3].Value;     // e.g., '"'

				// フルパスから相対パスを抽出（先頭のパス区切り文字を除去）
				string relativePath = fullPath.TrimStart('/', '\\');

				// basePath と結合して物理パスを解決
				string filePath = Path.Combine(basePath, relativePath);

				if (File.Exists(filePath))
				{
					DateTime lastWrite = File.GetLastWriteTimeUtc(filePath);
					long timestamp = new DateTimeOffset(lastWrite).ToUnixTimeSeconds();
					return $"{prefix}?v={timestamp}{suffix}";
				}

				// ファイルが見つからない場合は変更しない（開発中やファイル欠落時のフォールバック）
				return match.Value;
			});
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

					// グローバルシーケンス番号を付与（単調増加）
					int seq = Interlocked.Increment(ref _globalSequenceNumber);

					res.ContentType = "image/webp";
					res.Headers["X-Diff-Type"] = "full";
					res.Headers["X-Frame-Sequence"] = seq.ToString();
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
		//                  2026/07/29 注意: GC負荷低減のため、新規実装は ConvertToBytesOptimized を使用してください。
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

		//====================================================================================
		// 処理ブロック名 : ConvertToBytesOptimized
		// 概要           : 受信した Base64 文字列をバイト配列に変換します（ArrayPool 最適化版）。
		//                  Convert.FromBase64String が返す配列を ArrayPool にコピーし、
		//                  送信後に返却することで GC 負荷を低減します。
		//                  2026/07/29 myi 新規作成（GC負荷低減）
		//                  2026/08/01 myi 修正：中間バッファtempの寿命管理厳格化（null代入→GC対象化）
		//------------------------------------------------------------------------------------
		private (byte[] Buffer, int ActualLength) ConvertToBytesOptimized(string dataUrl, string prefix)
		{
			if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith(prefix))
			{
				return (null, 0);
			}
	
			try
			{
				// "data:image/webp;base64," のヘッダ部分（カンマ以前）を除去
				int commaIdx = dataUrl.IndexOf(',');
				if (commaIdx == -1) return (null, 0);
	
				string base64Str = dataUrl.Substring(commaIdx + 1);
	
				// Base64 文字列からバイト配列を取得（一時的なアロケーション）
				byte[] temp = Convert.FromBase64String(base64Str);
	
				// ArrayPool から必要なサイズのバッファをレンタル
				// Rent は要求サイズ以上のバッファを返す（temp.Length とは限らない）
				byte[] pooledBuffer = _bufferPool.Rent(temp.Length);
				Buffer.BlockCopy(temp, 0, pooledBuffer, 0, temp.Length);
	
				// [🚨 厳格な寿命管理] 中間バッファの参照切断 → 即時GC対象化
				// temp.Length を先に取得してからnull代入（長さ取得はnull代入前に実行）
				int actualLength = temp.Length;
				temp = null; // 参照切断 → GC対象化。この行がないとtempはメソッド終了まで生存し、メモリリークの原因となる
	
				// 呼び出し側は ArraySegment<byte>(pooledBuffer, 0, actualLength) として使用する
				return (pooledBuffer, actualLength);
			}
			catch (Exception)
			{
				return (null, 0);
			}
		}

		//====================================================================================
		// 処理ブロック名 : task28 Phase 0/1 — プロトコル拡張・サーバー側制御基盤
		//------------------------------------------------------------------------------------
		// task28 設計書 ver 3.0 に対応するプロトコル拡張およびサーバー側制御ロジックを実装
		//------------------------------------------------------------------------------------

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : BuildWebCodecsHeader
		// 概要           : WebCodecs モード用の7バイトヘッダーを構築する。
		//                  Version(1B)=0x02 + Type(1B)=0x01 + Flags(1B) + Seq(4B)
		//                  【レビュー修正】 設計書 §4.2.1 対応: version 引数を削除し常に 0x02 固定
		//------------------------------------------------------------------------------------
		private byte[] BuildWebCodecsHeader(byte type, byte flags, uint seq)
		{
			return new byte[] { 0x02, type, flags, (byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq };
		}

		//====================================================================================
		// 処理ブロック名 : SendDescriptionAsync
		// 概要           : [task19 P2] SPS/PPS (description) を Type=0x02 のフレームとして送信する。
		//                  フォーマット: [0x02, 0x02, 0x00, Seq(4B BE), SPS/PPSデータ]
		//                  クライアントJS (remote_connection.js) の Type=0x02 受信処理と整合させる
		//------------------------------------------------------------------------------------
		private async Task SendDescriptionAsync(WebSocketSession session, byte[] description, CancellationToken token)
		{
			if (description == null || description.Length == 0) return;
			if (session.WebSocket.State != WebSocketState.Open) return;

			// 新クライアントの追隨送信・reinit_decoder 再送信に共通してシーケンス番号を消費する
			// [task19_v1 Round5 Minor-4 修正] SPS/PPS 送信で seq を消費するため、
			// レートリミット (CanSendDescription) により再送が抑制されるケースでは seq 空間にギャップが生じる。
			// これは設計書通りであり、受信側は単調増加検証（int 差分による過去フレーム検出）で適切に処理する。
			int seq = Interlocked.Increment(ref _globalSequenceNumber);
			byte[] header = BuildWebCodecsHeader(0x02, 0x00, (uint)seq);

			await session.SendSemaphore.WaitAsync(token);
			try
			{
				if (IsWebSocketOpen(session.WebSocket))
				{
					var buffer = ArrayPool<byte>.Shared.Rent(header.Length + description.Length);
					try
					{
						Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
						Buffer.BlockCopy(description, 0, buffer, header.Length, description.Length);

						var segment = new ArraySegment<byte>(buffer, 0, header.Length + description.Length);
						await session.WebSocket.SendAsync(
							segment,
							System.Net.WebSockets.WebSocketMessageType.Binary,
							true,
							token);

						// [task19 レビュー指摘 M1] 送信成功を記録（失敗カウントリセット + 再送バックオフ解除）
						session.DescriptionSent = true;
						session.MarkDescriptionSentSuccess();
						Log(2, $"[RemoteController] SPS/PPS (description) 送信完了 ({description.Length} bytes, seq={seq}, Session: {session.SessionId})");
					}
					finally
					{
						// ArrayPool バッファの不要領域をゼロクリアし返却
						var usedLength = header.Length + description.Length;
						if (usedLength < buffer.Length)
						{
							Array.Clear(buffer, usedLength, buffer.Length - usedLength);
						}
						ArrayPool<byte>.Shared.Return(buffer);
					}
				}
			}
			catch (Exception ex)
			{
				// [task19 レビュー指摘 M1] 送信失敗を記録（DescriptionSent は false のままで次フレーム再試行）。
				// 連続失敗時は CanSendDescription() のバックオフで再送を抑制し、ログスパイクを防ぐ
				session.MarkDescriptionSentFailure();
				Log(3, $"[RemoteController] SPS/PPS (description) 送信失敗 (session={session.SessionId}): {ex.Message}");
			}
			finally
			{
				session.SendSemaphore.Release();
			}
		}

		//====================================================================================
		// 処理ブロック名 : WebCodecsCaptureLoopAsync
		// 概要           : WebCodecs モード用の中央キャプチャループ。
		//                  JS 側の VideoEncoder 経由でエンコード済みフレームを取得し、
		//                  各 WebSocket クライアントに配信する。
		//                  設計書 ver 3.0 Phase 2 対応
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/08/12
		// version        : 1.00 (task28 Phase 2)
		//------------------------------------------------------------------------------------
		private async Task WebCodecsCaptureLoopAsync(CancellationToken token)
		{
			Log(3, "[RemoteController] WebCodecs キャプチャループを開始しました (Debug)");

			var sw = System.Diagnostics.Stopwatch.StartNew();
			// [task19_v1 Critical#3修正] 2秒毎キーフレーム間隔用の独立ストップウォッチ
			// sw はアダプティブ FPS 計測（loopStart / loopElapsed）に使用するため、
			// キーフレーム間隔の sw.Restart() が FPS 計測を破壊する既存問題を回避するため分離する
			var keyFrameSw = System.Diagnostics.Stopwatch.StartNew();
			// [task19_v1 Critical#3修正] 初回フレームフラグ（初回はキーフレームを強制）
			bool isWebCodecsFirstFrame = true;
			// 【Minor修正】 seq の型整合性の明文字
			// 設計書 §4.2 では uint (32bit 単調増加) が規定されているが、
			// C# の Interlocked.Increment は uint 型をサポートしないため int 型を使用。
			// ヘッダー書き込み時（BuildWebCodecsHeader）で (uint) キャストを行う。
			// int 型として -2147483648 〜 +2147483647 の範囲で単調増加し、
			// ラップアラウンド後も差分計算（int 演算）で過去フレームの検出が機能。
			// 符号付き整数の差分でラップアラウンド後も正しく機能するため、int 型が正当。
			int seq = 0;

			try
			{
				while (!token.IsCancellationRequested)
				{
					var loopStart = sw.ElapsedMilliseconds;

					// Kancolle frame チェック
					var kancolleFrame = _parent.GetKanColleFrame();
					if (kancolleFrame == null)
					{
						await Task.Delay(100, token);
						continue;
					}

					// 【レビュー修正】 IPC最適化: isReinitializing チェックを個別EvaluateScriptAsyncから削除
					// 設計書 §5.3.2 対応: pull_encoded() 内で isReinitializing チェックを行う設計に統一
					// JS 側 pull_encoded() は isReinitializing=true のとき ['none', null] を返す

					// [task19_v1 Critical#3修正] キーフレーム強制タイミングの判定（設計書 §5.3.2 対応）
					// ① 初回フレーム ② DescriptionSent 未送信状態（reinit_decoder / switch_mode でリセット後）
					// ③ 2秒毎のキーフレーム強制（GOP 周期: WebCodecsKeyFrameIntervalMs）
					// 【レビュー修正】 2秒毎のキーフレーム強制要求（設計書 §5.3.2 対応）
					// [task19_v1 Critical#3修正] 独立 keyFrameSw を使用（sw.Restart() が FPS 計測を破壊する既存問題を回避）
					// [task19_v1 Critical#3修正] SPS/PPS (Type=0x02) を受信できる Version 2+ クライアントのみを判定対象にする。
					// Version 1 クライアントは SPS/PPS を受信できない（送信ループでスキップされる）ため、
					// その DescriptionSent は永久に false のままだ。V1 みを判定対象にすると毎フレーム
					// キーフレームを強制し続ける（I フレーム連続生成 = 帯域・CPU 浪費）ため、V1 は除外する。
					bool anyDescriptionPending = false;
					foreach (var session in _webSocketSessions.Values)
					{
						if (session.DescriptionSent)
							continue;
						// SPS/PPS (Type=0x02) は Version 2+ のみ受信可能（Version 1 は 6B ヘッダーで非対応）
						var clientVer = GetClientVersion(session.SessionId);
						if (clientVer == null || clientVer < 2)
							continue;
						anyDescriptionPending = true;
						break;
					}
					bool isKeyFrame = isWebCodecsFirstFrame
						|| anyDescriptionPending
						|| keyFrameSw.ElapsedMilliseconds >= WebCodecsKeyFrameIntervalMs;
					if (isKeyFrame)
					{
						keyFrameSw.Restart();
					}

					// [task19_v1 Critical#3修正] JS 側 encodeFrame() を呼び出し、_encodedChunks にフレームを生成する
					// encodeFrame() は同期関数（Promise ではなくオブジェクトを直接返却）のため、
					// 非 Promise 版 EvaluateScriptAsync を使用（ExecuteJavaScriptAsync 相当）。
					// 結果は pull_encoded()（_encodedChunks キュー）で回収される。
					// AGENTS.md: 空 catch 禁止 — engine 未初期化・例外時は警告ログを出力し、そのフレームをスキップしてループ継続
					try
					{
						var encodeScript = WebCodecsEncodeFrameTemplate.Replace("__IS_KEY_FRAME__", isKeyFrame ? "true" : "false");
						var encodeResult = await kancolleFrame.EvaluateScriptAsync(encodeScript);
						if (!encodeResult.Success || encodeResult.Result == null)
						{
							// engine 未初期化（window._kancolleCaptureEngine が null）または JS 例外
							// 警告ログを出力し、そのフレームをスキップしてループ継続
							var encodeFailReason = encodeResult.Success ? "Result null" : encodeResult.Message;
							Log(3, $"[RemoteController] WebCodecs encodeFrame() 呼び出し失敗 (engine 未初期化または JS 例外): {encodeFailReason}");
							await Task.Delay(100, token);
							continue;
						}

						// JS 側 encodeFrame() 返却値: { success: bool, error?: string, action?: 'reinit', type?: 'encoded' }
						// CefSharp は JS プレーンオブジェクトを IDictionary<string, object> として受信する
						var encodeDict = encodeResult.Result as System.Collections.Generic.IDictionary<string, object>;
						if (encodeDict != null && encodeDict.TryGetValue("action", out var actionVal) && actionVal is string actionStr && actionStr == "reinit")
						{
							// エンコーダ状態が closed / unconfigured → 再初期化してループ継続（次イテレーションで初回フレーム=キーフレーム）
							Log(3, "[RemoteController] WebCodecs encodeFrame() が reinit を返却 (encoder closed/unconfigured)、再初期化します");
							var reinitOk = await InitializeWebCodecsEncoderAsync(token);
							if (!reinitOk)
							{
								Log(3, "[RemoteController] WebCodecs 再初期化失敗、このフレームをスキップします");
								await Task.Delay(100, token);
								continue;
							}
							// 再初期化後は次フレームをキーフレームとして強制（SPS/PPS 再生成が必要）
							isWebCodecsFirstFrame = true;
							continue;
						}

						// encodeFrame() 呼び出し失敗（encoder not running 等）→ 警告ログを出力し、そのフレームをスキップしてループ継続
						if (encodeDict != null && encodeDict.TryGetValue("success", out var encodeSuccessVal) && encodeSuccessVal is bool encodeSuccess && !encodeSuccess)
						{
							var encodeError = encodeDict.TryGetValue("error", out var encodeErrVal) ? encodeErrVal as string : null;
							Log(3, $"[RemoteController] WebCodecs encodeFrame() 失敗（このフレームをスキップ）: {encodeError ?? "unknown"}");
							await Task.Delay(100, token);
							continue;
						}

						// encodeFrame() 成功 → 初回フレームフラグをクリア（初回フレームが実際にエンコードされたことを示す）
						isWebCodecsFirstFrame = false;
					}
					catch (OperationCanceledException)
					{
						throw; // キャンセル要求は外側 catch に伝播（正常終了）
					}
					catch (Exception ex)
					{
						// AGENTS.md: 空 catch 禁止 — 警告ログを出力し、そのフレームをスキップしてループ継続
						Log(3, $"[RemoteController] WebCodecs encodeFrame() 例外（このフレームをスキップ）: {ex.Message}");
						await Task.Delay(100, token);
						continue;
					}

					// PullEncodedBinaryAsync を呼び出し、エンコード済みデータを取得
					var pullResult = await PullEncodedBinaryAsync();
					if (pullResult.data == null || pullResult.data.Length == 0)
					{
						// 【レビュー修正】 アダプティブ FPS 制御: 未データ時は短待機
						await Task.Delay(100, token);
						continue;
					}

					// 【レビュー修正】 キーフレームフラグの統合（JS 側と C# 側を統合）
					bool dataIsKeyFrame = pullResult.isKeyFrame;

					// [task19 P2] SPS/PPS (description) をキャッシュ（reinit_decoder 再送信・新クライアント追隨送信用）
					// キーフレームに添付された SPS/PPS は AVC セッション内で不変のため、最新値を保持して再利用する
					// [task19 レビュー指摘 C1] 不変性保証: pullResult.description は CefSharp 経由の新規 byte[] だが、
					// 将来のバッファ再利用・共有化に備え、キャッシュ登録時に防御コピー（不変配列化）を行う。
					// 代入先 _lastWebCodecsDescription は volatile 宣言済みのため、プレーン代入でも
					// フルフェンス付きアトミックな参照交換として他スレッドに可視化される（CS0420 回避）
					if (pullResult.description != null && pullResult.description.Length > 0)
					{
						var cachedDesc = new byte[pullResult.description.Length];
						Buffer.BlockCopy(pullResult.description, 0, cachedDesc, 0, cachedDesc.Length);
						_lastWebCodecsDescription = cachedDesc;
					}

					// Flags: キーフレームかどうか（C# 側のキーフレーム間隔 + JS 側のキーフレームフラグを統合）
					byte flags = (isKeyFrame || dataIsKeyFrame) ? (byte)0x01 : (byte)0x00;

					// 全 WebSocket セッションに配信
					foreach (var session in _webSocketSessions.Values)
					{
						if (session.WebSocket.State != WebSocketState.Open)
							continue;

						// クライアントバージョンを確認（null 時は送信抑止）
						// [task19 P2] SPS/PPS (Type=0x02) は 7B ヘッダーの Version 2 形式のため、
						// Version 1 クライアント（6B ヘッダー）には送信不可。
						// バージョンチェックを SPS/PPS 送信ブロックより前に置く
						var clientVer = GetClientVersion(session.SessionId);
						if (clientVer == null)
							continue;

						if (clientVer < 2)
						{
							// Version 1: 6B ヘッダー（WebPのみ、WebCodecsは非対応）
							// 【レビュー修正】 Version 1 クライアントへの警告ログ追加
							// [task19 レビュー指摘 M3] 毎フレーム出力するとログスパイクになるため、
							// 10秒間隔でダウンスープリングする（TryLogVersion1Warning）
							if (session.TryLogVersion1Warning())
							{
								Log(3, $"[RemoteController] WebCodecs フレームを Version 1 クライアント (session={session.SessionId}) に送信しない（10秒間隔ログ）");
							}
							continue;
						}

						// [task19 P2] SPS/PPS 送信タイミング保証（設計書 §4.2.2 ステップ3: Critical 3 修正）
						// description が未送信のセッションには、キーフレーム (Type=0x01) より前に
						// SPS/PPS (Type=0x02) を必ず送信する。これによりクライアント側は
						// VideoDecoder.configure() 実行時に description が確実に存在する
						if (!session.DescriptionSent)
						{
							// [task19 レビュー指摘 M1] 連続失敗時はバックオフ期間中のみ再送を抑制する
							if (session.CanSendDescription())
							{
								// [task19 レビュー指摘 C1] volatile 参照はローカル変数へ即スナップショットし、
								// 送信中に別スレッドで _lastWebCodecsDescription が差し替わっても影響を受けないようにする
								// （volatile 宣言フィールドのプレーン読み取りはフルフェンス付きで可視性が保証される）
								// （description 自体は防御コピー済みの不変配列。SendDescriptionAsync 内で BlockCopy して送信）
								byte[] descToSend = pullResult.description ?? _lastWebCodecsDescription;
								if (descToSend != null && descToSend.Length > 0)
								{
									await SendDescriptionAsync(session, descToSend, token);
								}
							}
						}

						// [task19_v1 Minor-1/Minor-2 修正] フレーム seq 採番を SPS/PPS 送信の後に移動
						// SPS/PPS (Type=0x02) 送信後にフレーム seq を採番することで、
						// SPS/PPS seq < フレーム seq が seq 空間・受信順の両方で保証される。
						// これにより seq 順序逆転（Minor-1）と受信順での単調増加違反（Minor-2）を解消する。
						// 同一グローバルカウンタ（_globalSequenceNumber）を維持し、heartbeat は seq を消費しない。
						// [task19_v1 Round5 Minor-3/Minor-5 修正] 同一フレームは全セッションで同一 seq を共有する。
						// seq 採番はセッションループ外で 1 回のみ実行され、同一フレームが全セッションに
						// 同一 seq で送信される。受信側は seq で順序保証を行う設計であり、
						// セッション横断の単調増加（シーケンス空間）が維持される。
						seq = Interlocked.Increment(ref _globalSequenceNumber);

						// BuildWebCodecsHeader の呼び出し（version 引数削除）
						// BuildWebCodecsHeader は常に 7B の固定ヘッダーを返し、null を返さないため null チェックは不要
						byte[] header = BuildWebCodecsHeader(0x01, flags, (uint)seq);

						// バイナリ送信（ヘッダー + データ）
						// 【Critical修正】 SendSemaphore 取得によりソケットの同時書き込みを防止
						await session.SendSemaphore.WaitAsync(token);
						try
						{
							if (IsWebSocketOpen(session.WebSocket))
							{
								var buffer = ArrayPool<byte>.Shared.Rent(header.Length + pullResult.data.Length);
								try
								{
									Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
									Buffer.BlockCopy(pullResult.data, 0, buffer, header.Length, pullResult.data.Length);

									var segment = new ArraySegment<byte>(buffer, 0, header.Length + pullResult.data.Length);
									await session.WebSocket.SendAsync(
										segment,
										System.Net.WebSockets.WebSocketMessageType.Binary,
										true,
										token);
								}
								finally
								{
									// 【レビュー修正】 ArrayPool バッファの不要領域をゼロクリア（二次レビュー Major 対応）
									// Rent() で取得したバッファはゼロクリアされないため、前回の残データを消去
									var usedLength = header.Length + pullResult.data.Length;
									if (usedLength < buffer.Length)
									{
										Array.Clear(buffer, usedLength, buffer.Length - usedLength);
									}
									ArrayPool<byte>.Shared.Return(buffer);
								}
							}
						}
						catch (Exception ex)
						{
							Log(3, $"[RemoteController] WebCodecs フレーム送信失敗 (session={session.SessionId}): {ex.Message}");
						}
						finally
						{
							session.SendSemaphore.Release();
						}
					}

					// 【レビュー修正】 アダプティブ FPS 制御: 実際のループ時間を計測し、目標フレーム間隔に達するまで待機
					var loopElapsed = sw.ElapsedMilliseconds - loopStart;
					int intervalMs = 1000 / _webCodecsFps;
					int waitMs = intervalMs - (int)loopElapsed;
					if (waitMs > 0)
					{
						await Task.Delay(waitMs, token);
					}
					// waitMs <= 0 の場合は目標 FPS を達成できず、次フレームへ即時移行
				}
			}
			catch (OperationCanceledException)
			{
				// キャンセル要求による正常終了
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebCodecs キャプチャループ例外: {ex.Message}");
			}
			finally
			{
				// finally ブロックで StopWebCodecsEncoderAsync を await 実行（設計書 §5.3.2 対応）
				try
				{
					await StopWebCodecsEncoderAsync();
				}
				catch (Exception ex)
				{
					Log(3, $"[RemoteController] WebCodecs キャプチャループ停止時例外: {ex.Message}");
				}

				lock (_modeLock)
				{
					_webCodecsCaptureTask = null;
				}
				Log(2, "[RemoteController] WebCodecs キャプチャループが完全に終了しました");
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : InitializeWebCodecsEncoderAsync
		// 概要           : CefSharp 経由で JS の initEncoder() を呼び出し、WebCodecs エンコーダを初期化する。
		//                  タイムアウト（5秒）制御付き。
		//------------------------------------------------------------------------------------
		private async Task<bool> InitializeWebCodecsEncoderAsync(CancellationToken cancellationToken = default)
		{
			// 既に初期化済みなら即座に完了
			if (_isWebCodecsInitialized) return true;

			try
			{
				// CefSharp 経由で JS の initEncoder() を呼び出す
				var kancolleFrame = _parent.GetKanColleFrame();
				if (kancolleFrame == null)
				{
					Log(3, "[RemoteController] WebCodecs エンコーダ初期化: Kancolle frame not found");
					return false;
				}

				// 【レビュー修正】 コーデック値のエスケープ処理を追加（設計書 ver 3.9 Mi1 対応 + 二次レビュー Major S1 対応）
				// 特殊文字（' , " , \ , 制御文字）が含まれる場合のJavaScript構文エラーを防止
				// 【レビュー修正】 JS エスケープの完全性向上（一次レビュー Major + 二次レビュー Major 対応）
				// 制御文字（\u0000-\u001F）を \\uXXXX 形式でエスケープし、JS 構文エラーを防止
				var escapedCodec = _webCodecsCodec
					.Replace("\\", "\\\\")
					.Replace("'", "\\'")
					.Replace("\"", "\\\"")
					.Replace("\n", "\\n")
					.Replace("\r", "\\r")
					.Replace("\t", "\\t");
				// 【追加】 残りの制御文字（\u0000-\u001F 除去済み以外）を \\uXXXX 形式でエスケープ
				var sb = new StringBuilder();
				foreach (var c in escapedCodec)
				{
					if (c >= 0x00 && c <= 0x1F)
					{
						sb.AppendFormat("\\u{0:X4}", (int)c);
					}
					else
					{
						sb.Append(c);
					}
				}
				escapedCodec = sb.ToString();
				// [task19_v1 Critical修正] string.Format の代わりに固有トークン置換 + string.Replace で構築
				// 理由: string.Format は JS リテラルブレース（{if...} {return...} {codec...}）をフォーマット項目と解釈し、
				// FormatException（「入力文字列の形式が正しくありません」）を投げて JS に到達しなかった。
				// verbatim テンプレートには {0} プレースホルダを含ませないため、ブレースのエスケープが不要で検証しやすい。
				// 置換順序: __BITRATE__ / __FPS__（整数値）を先に置き、__CODEC__ は最後に置換して、
				// コーデック値にトークン文字列が含まれる場合の二重置換を防止する。
				var script = WebCodecsInitEncoderTemplate
					.Replace("__BITRATE__", _webCodecsBitrate.ToString())
					.Replace("__FPS__", _webCodecsFps.ToString())
					.Replace("__CODEC__", escapedCodec);
	
				// タイムアウト制御（5秒）
				// 【ver 3.0 対応】 design doc §5.3.2a: timeoutCts.Cancel() により delayTask を確実にキャンセル
				// [レビュー修正] 設計書 §5.3.2a 通り 5秒 に統一
				// 【レビュー修正】 using 句で CancellationTokenSource を確実に破棄（タスクリーク防止）
				using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
				using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
				{
					// [task19_v1 Critical#2修正] EvaluateScriptAsPromiseAsync に切替（CefSharp 143 WebBrowserExtensions 拡張メソッド）
					// initEncoder() は async function で Promise を返却するが、非 Promise 版 EvaluateScriptAsync は
					// Promise オブジェクト自体（配列ではない）を返却するため、C# 側の IList<object> 解析が失敗していた。
					// Promise 対応 API はスクリプトを async function でラップし、Promise.resolve(...).then() で解決値を回収する。
					var evalTask = kancolleFrame.EvaluateScriptAsPromiseAsync(script);
					var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
	
					var completedTask = await Task.WhenAny(evalTask, delayTask);
					linkedCts.Cancel();
	
					if (completedTask == delayTask)
					{
						// タイムアウト
						Log(3, "[RemoteController] WebCodecs エンコーダ初期化タイムアウト（5秒）");
						return false;
					}
	
					// 【task19_v1 Critical#2修正】 EvaluateScriptAsPromiseAsync を使用し、initEncoder() の Promise を解決して回収
					// evalTask が既に完了している場合は await せずに結果を取得
					// Task.WhenAny 後に evalTask を await すると、evalTask が未完了の状態でブロックするリスクがある
					// ただし、この場合 completedTask == evalTask なので安全に await 可能
					var result = await evalTask;
					// [task19_v1 Critical#2修正] Promise が reject された場合（initEncoder が例外を投げる等）
					// EvaluateScriptAsPromiseAsync は catch 経路でエラー文字列を Result に格納して Success=false を返す
					if (!result.Success)
					{
						Log(3, $"[RemoteController] WebCodecs エンコーダ初期化失敗 (Promise reject): {result.Result}");
						return false;
					}

					// engine 未初期化（window._kancolleCaptureEngine が null）の場合、IIFE が undefined を解決し Result は null
					if (result.Result == null)
					{
						Log(3, "[RemoteController] WebCodecs エンコーダ初期化: 結果が null（engine 未初期化または未解決）");
						return false;
					}

					// JS 側 initEncoder() 返却値: { success: bool, codec?: string, error?: string }
					// CefSharp は JS プレーンオブジェクトを IDictionary<string, object> として受信する
					var dict = result.Result as System.Collections.Generic.IDictionary<string, object>;
					if (dict == null)
					{
						// 予期しない型（オブジェクト以外）を受信した場合は WebP フォールバック
						Log(3, $"[RemoteController] WebCodecs エンコーダ初期化: 結果の形式が不正 (type: {result.Result.GetType().Name})");
						return false;
					}

					var success = dict.TryGetValue("success", out var successVal) && successVal is bool s && s;
					if (success)
					{
						_isWebCodecsInitialized = true;
						var codec = dict.TryGetValue("codec", out var codecVal) ? codecVal as string : null;
						Log(2, $"[RemoteController] WebCodecs エンコーダ初期化成功 (codec: {codec})");
						return true;
					}
					else
					{
						var error = dict.TryGetValue("error", out var errorVal) ? errorVal as string : null;
						var errorMsg = error ?? successVal?.ToString() ?? "unknown";
						Log(3, $"[RemoteController] WebCodecs エンコーダ初期化失敗: {errorMsg}");
						return false;
					}
				}
			}
			catch (OperationCanceledException)
			{
				Log(3, "[RemoteController] WebCodecs エンコーダ初期化キャンセル");
				return false;
			}
			catch (Exception ex)
			{
				// エラー時のロールバック
				_isWebCodecsInitialized = false;
				Log(3, $"[RemoteController] WebCodecs エンコーダ初期化例外: {ex.Message}");
				return false;
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : StopWebCodecsEncoderAsync
		// 概要           : CefSharp 経由で JS の stopEncoder() を呼び出し、WebCodecs エンコーダを停止する。
		//                  WaitForOldLoopCompletionAsync により旧ループの完全停止を待機する。
		//------------------------------------------------------------------------------------
		private async Task StopWebCodecsEncoderAsync()
		{
			if (!_isWebCodecsInitialized) return;

			try
			{
				// JS 側の stopEncoder() を呼び出す
				var kancolleFrame = _parent.GetKanColleFrame();
				if (kancolleFrame != null)
				{
					var result = await kancolleFrame.EvaluateScriptAsync(WebCodecsStopScript);
					if (!result.Success)
					{
						Log(3, "[RemoteController] stopEncoder() 呼び出し失敗");
					}
				}
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] stopEncoder() 例外: {ex.Message}");
			}
			finally
			{
				_isWebCodecsInitialized = false;
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : SetWebCodecsCodec
		// 概要           : WebCodecs エンコーダのコーデック値を変更し、再初期化を行う。
		//                  【Major修正】 設計書 ver 3.9 §5.3.1 対応: コーデック変更経路の実装
		//------------------------------------------------------------------------------------
		public async Task<bool> SetWebCodecsCodec(string codec)
		{
			if (string.IsNullOrEmpty(codec))
			{
				Log(3, "[RemoteController] SetWebCodecsCodec: 空のコーデック値");
				return false;
			}

			// 現在使用中のコーデックと変更がなければ何もしない
			if (_webCodecsCodec == codec)
			{
				return true;
			}

			Log(2, $"[RemoteController] WebCodecs コーデック変更: {_webCodecsCodec} -> {codec}");

			// 旧エンコーダを停止
			await StopWebCodecsEncoderAsync();

			// コーデック値を更新（volatile 宣言によりスレッド間で即座に可視）
			_webCodecsCodec = codec;

			// 新コーデックで再初期化
			return await InitializeWebCodecsEncoderAsync();
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : SetWebCodecsBitrate
		// 概要           : WebCodecs エンコーダのビットレート値を変更し、再初期化を行う。
		//------------------------------------------------------------------------------------
		public async Task<bool> SetWebCodecsBitrate(int bitrate)
		{
			if (bitrate <= 0)
			{
				Log(3, "[RemoteController] SetWebCodecsBitrate: 無効なビットレート値");
				return false;
			}

			if (_webCodecsBitrate == bitrate)
			{
				return true;
			}

			Log(2, $"[RemoteController] WebCodecs ビットレート変更: {_webCodecsBitrate} -> {bitrate}");

			await StopWebCodecsEncoderAsync();
			_webCodecsBitrate = bitrate;
			return await InitializeWebCodecsEncoderAsync();
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : SetWebCodecsFps
		// 概要           : WebCodecs エンコーダのFPS値を変更する（再初期化不要）。
		//                  volatile 宣言によりキャプチャループに即座に反映される。
		//------------------------------------------------------------------------------------
		public void SetWebCodecsFps(int fps)
		{
			if (fps <= 0 || fps > 60)
			{
				Log(3, $"[RemoteController] SetWebCodecsFps: 無効なFPS値 ({fps})");
				return;
			}

			if (_webCodecsFps == fps)
			{
				return;
			}

			Log(2, $"[RemoteController] WebCodecs FPS変更: {_webCodecsFps} -> {fps}");
			_webCodecsFps = fps; // volatile によりキャプチャループに即座に可視
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : WaitForOldLoopCompletionAsync
		// 概要           : 旧ループタスクの完了を待機する（タイムアウト付き）。
		//                  デフォルトタイムアウト: 7000ms
		//------------------------------------------------------------------------------------
		private async Task WaitForOldLoopCompletionAsync(CancellationTokenSource oldCts, int timeoutMs = 7000)
		{
			// 旧ループのタスクを収集
			Task[] oldTasks = null;
			lock (_modeLock)
			{
				var list = new List<Task>();
				if (_webCodecsCaptureTask != null) list.Add(_webCodecsCaptureTask);
				if (_centralCaptureTask != null) list.Add(_centralCaptureTask);
				if (list.Count > 0)
				{
					oldTasks = list.ToArray();
				}
			}

			if (oldTasks == null || oldTasks.Length == 0)
			{
				return;
			}

			var sw = Stopwatch.StartNew();
			bool timeoutOccurred = false;

			try
			{
				// 旧ループタスクの完了を並列に待機（タイムアウト付き）
				using (var timeoutCts = new CancellationTokenSource())
				{
					var allTasks = Task.WhenAll(oldTasks);
					var delayTask = Task.Delay(timeoutMs);

					var completedTask = await Task.WhenAny(allTasks, delayTask);

					if (completedTask == allTasks)
					{
						// Task.WhenAll が完了した場合は正常終了
						if (allTasks.IsFaulted)
						{
							var agg = allTasks.Exception?.Flatten();
							foreach (var ex in agg?.InnerExceptions ?? Enumerable.Empty<Exception>())
							{
								Log(3, $"[RemoteController] 旧ループタスク例外: {ex.GetType().Name}: {ex.Message}");
							}
						}
						Log(2, $"[RemoteController] 旧ループ完了待機完了（{sw.ElapsedMilliseconds}ms / タイムアウト{timeoutMs}ms）");
						return;
					}

					// タイムアウト発生
					timeoutOccurred = true;
					if (oldCts != null)
					{
						try { if (!oldCts.IsCancellationRequested) oldCts.Cancel(); }
						catch (ObjectDisposedException) { /* 既に破棄済み */ }
						catch (Exception ex) {
							Log(3, $"[RemoteController] oldCts.Cancel() 予期せぬ例外: {ex.GetType().Name}: {ex.Message}");
							throw;
						}
					}
				}
			}
			catch (AggregateException agg)
			{
				foreach (var ex in agg.InnerExceptions)
				{
					if (ex is OperationCanceledException)
					{
						// 正常なキャンセル
					}
					else
					{
						Log(3, $"[RemoteController] 旧ループタスク例外: {ex.GetType().Name}: {ex.Message}");
					}
				}
			}
			catch (OperationCanceledException)
			{
				timeoutOccurred = true;
			}

			if (timeoutOccurred)
			{
				Log(2, $"[RemoteController] 旧ループ待機タイムアウト（{sw.ElapsedMilliseconds}ms / タイムアウト{timeoutMs}ms）。旧ループを強制キャンセルし、新モードを開始します");
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : PullEncodedBinaryAsync
		// 概要           : CefSharp 経由で pull_encoded() を呼び出し、エンコード済みチャンクのバイナリを取得する。
		//                  ver 2.9 対応: Base64 文字列受信 → Convert.FromBase64String() 変換
		//                  【レビュー修正】 pull_encoded() が async に変更されたことに合わせ、
		//                  キーフレームフラグも返却するように拡張
		//------------------------------------------------------------------------------------
		private struct PullEncodedResult
		{
			public byte[] data;
			public bool isKeyFrame;
			// [task19 P2] SPS/PPS (description) バイト列（AVCDecoderConfigurationRecord / AVCC 形式）
			// WebCodecs 仕様（W3C AVC 登録仕様に準拠）: avc.format='avc' 時、description は
			// ISO/IEC 14496-15 §5.3.3.1 の AVCDecoderConfigurationRecord 形式。
			// JS 側 pull_encoded() の第4要素（Base64）を復号した値。null = 未設定
			public byte[] description;
		}

		// 【二次レビュー Major 指摘S1 修正】 連続タイムアウトカウント（PullEncodedBinaryAsync）
		// JS 側 _acquireLock() タイムアウト（50ms）と C# 側タイムアウト（500ms）の10倍の開きにより、
		// 連続タイムアウト時に過剰な再試行/ CPU リソース消費を防ぐための保護。
		// 閾値超過時はループを中断して再初期化トリガーを返す。
		private int _pullTimeoutCount = 0;
		private const int MaxPullTimeoutCount = 10;

		private async Task<PullEncodedResult> PullEncodedBinaryAsync()
		{
			var result = new PullEncodedResult { data = null, isKeyFrame = false };

			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null) return result;

			// 【レビュー修正】 タイムアウト制御（500ms）を追加（設計書 §5.3.2a 対応）
			// EvaluateScriptAsync のデフォルトタイムアウトは 30秒であり、
			// JS 側の _acquireLock() でブロックされている場合、ループが永久に待機するリスクがある
			using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
			{
				try
				{
					var evalTask = kancolleFrame.EvaluateScriptAsync(WebCodecsPullScript);
					var delayTask = Task.Delay(500, timeoutCts.Token);

					var completed = await Task.WhenAny(evalTask, delayTask);
					if (completed == delayTask)
					{
						// 【二次レビュー Major 指摘S1 修正】 連続タイムアウトカウントインクリメント
						// 【二次レビュー Minor 修正】 Interlocked に原子化（将来的な並列化に備える）
						int currentCount = System.Threading.Interlocked.Increment(ref _pullTimeoutCount);
						if (currentCount >= MaxPullTimeoutCount)
						{
							Log(3, $"[RemoteController] PullEncodedBinaryAsync 連続タイムアウト {_pullTimeoutCount} 回 (最大 {MaxPullTimeoutCount} 回に達)、再初期化をトリガーします");
							// 【二次レビュー Minor 修正】 Interlocked.Exchange でリセット
							System.Threading.Interlocked.Exchange(ref _pullTimeoutCount, 0);
						}
						else
						{
							Log(3, $"[RemoteController] PullEncodedBinaryAsync タイムアウト ( {_pullTimeoutCount}/{MaxPullTimeoutCount} 回) — 再試行を続行");
						}
						// タイムアウト時はフレームをドロップ
						return result;
					}

					// 【二次レビュー Major 指摘S1 修正】 成功時は連続タイムアウトカウントをリセット
					// 【二次レビュー Minor 修正】 Interlocked.Exchange でリセット
					System.Threading.Interlocked.Exchange(ref _pullTimeoutCount, 0);

					var scriptResult = await evalTask;
					if (!scriptResult.Success || scriptResult.Result == null) return result;

					// CefSharp は JS 配列を IList<object> として返す
					var list = scriptResult.Result as System.Collections.Generic.IList<object>;
					if (list == null || list.Count < 2) return result;

					string type = list[0] as string;
					if (type != "encoded") return result;

					// Base64 文字列を受信し、直ちに byte[] 化（ver 2.9 対応: GC ガベージゼロ）
					string base64 = list[1] as string;
					if (string.IsNullOrEmpty(base64)) return result;
					result.data = Convert.FromBase64String(base64);

					// 【レビュー修正】 キーフレームフラグの取得（第3要素）
					if (list.Count >= 3)
					{
						string keyFlag = list[2] as string;
						result.isKeyFrame = keyFlag == "1";
					}

					// [task19 P2] SPS/PPS (description) の取得（第4要素: Base64 文字列または null）
					// JS 側 pull_encoded() はキーフレーム時に metadata.decoderConfig.description を
					// Base64 文字列として添付する（設計書 §4.2.2 ステップ2）。
					// null / 空文字列 / 不正な Base64 の場合は description を null のままにする
					if (list.Count >= 4 && list[3] is string descriptionB64 && !string.IsNullOrEmpty(descriptionB64))
					{
						try
						{
							result.description = Convert.FromBase64String(descriptionB64);
						}
						catch (FormatException ex)
						{
							// 不正な Base64 は破棄（例外の握りつぶし禁止のためログ出力）
							Log(3, $"[RemoteController] PullEncodedBinaryAsync: SPS/PPS Base64 復号失敗: {ex.Message}");
						}
					}

					// 【Minor修正 指摘6】 成功時に明示的にキャンセル（タイマー稼働防止）
					timeoutCts.Cancel();
					return result;
				}
				catch (OperationCanceledException)
				{
					// タイムアウトによるキャンセル
					return result;
				}
				catch (Exception ex)
				{
					Log(3, $"[RemoteController] PullEncodedBinaryAsync 例外: {ex.Message}");
					return result;
				}
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
		//                  【Critical修正】 CleanupZombieSessions と SetClientVersion 間の TOCTOU
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
			// 【バグ修正】 旧実装: return result != null;
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
			// [外部レビュー 指摘4] UIスレッドでの .Wait() による同期ブロックは厳禁
			// CancellationTokenSource.Cancel() による非同期キャンセルのみに変更
			if (_centralCaptureCts != null && !_centralCaptureCts.IsCancellationRequested)
			{
				_centralCaptureCts.Cancel();
				_centralCaptureCts.Dispose();
			}

			// WebCodecs キャプチャループのキャンセル（task28 Phase 1）
			if (_webCodecsCaptureCts != null && !_webCodecsCaptureCts.IsCancellationRequested)
			{
				_webCodecsCaptureCts.Cancel();
				_webCodecsCaptureCts.Dispose();
			}

			// [二次レビュー Minor 指摘3 / CriticalReview 指摘3] タスク参照をローカル変数にキャプチャしてから監視
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

			// 【Critical修正 指摘5】 _webCodecsCaptureTask の終了監視を追加
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
		// 処理ブロック名 : HandleCaptureModeAsync
		// 概要           : WebCodecs モード切替リクエストを処理する（POST）。
		//                  body に JSON { mode: "webp" | "webcodecs" | "http" } を期待。
		//------------------------------------------------------------------------------------
		private async Task HandleCaptureModeAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			if (req.HttpMethod != "POST")
			{
				res.StatusCode = 405;
				res.Close();
				return;
			}

			try
			{
				using (var reader = new StreamReader(req.InputStream))
				{
					var body = await reader.ReadToEndAsync();
					// 【Minor修正】 正規表現による堅牢なJSONパース
					// 単純なReplaceチェーンでは {"mode":"webcodecs","quality":100} → "webcodecsquality100" 等と誤認識
					// Trim() + LowerInvariant() による大文字小文字・空白の正規化を適用
					var match = System.Text.RegularExpressions.Regex.Match(body, @"""mode""\s*:\s*""([^""]+)""");
					var modeStr = match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : "";

					CaptureMode newMode;
					switch (modeStr)
					{
						case "webcodecs": newMode = CaptureMode.WebCodecs; break;
						case "http": newMode = CaptureMode.HttpPolling; break;
						case "webp": newMode = CaptureMode.WebP; break;
						default:
							// [Minor修正] 想定外の文字列時はデフォルトモードにフォールバックし警告ログを出力
							Log(2, $"[RemoteController] 無効なキャプチャモード文字列 \"{modeStr}\"、デフォルト(WebP)にフォールバック");
							newMode = CaptureMode.WebP;
							break;
					}

					await SetCaptureModeAsync(newMode);

					Log(2, $"[RemoteController] キャプチャモード切替完了: {newMode}");

					res.StatusCode = 200;
					res.ContentType = "application/json";
					var bytes = Encoding.UTF8.GetBytes($"{{\"mode\":\"{newMode.ToString().ToLower()}\"}}");
					res.ContentLength64 = bytes.Length;
					res.OutputStream.Write(bytes, 0, bytes.Length);
				}
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] HandleCaptureModeAsync エラー: {ex.Message}");
				res.StatusCode = 500;
			}
			finally
			{
				res.Close();
			}
		}

		//====================================================================================
		// 処理ブロック名 : SetCaptureModeAsync
		// 概要           : キャプチャモードを切替える（設計書 §5.3.3 対応）。
		//                  旧ループの停止・待機 → 新ループの起動を行う。
		//------------------------------------------------------------------------------------
		private async Task SetCaptureModeAsync(CaptureMode newMode)
		{
			CancellationTokenSource oldCts = null;
			CaptureMode oldMode;

			lock (_modeLock)
			{
				oldMode = _currentCaptureMode;
				if (oldMode == newMode) return;

				Log(2, $"[RemoteController] キャプチャモード切替開始: {oldMode} → {newMode}");

				// 旧ループのキャンセル
				switch (oldMode)
				{
					case CaptureMode.WebP:
						oldCts = _centralCaptureCts;
						if (oldCts != null && !oldCts.IsCancellationRequested)
							oldCts.Cancel();
						break;
					case CaptureMode.WebCodecs:
						oldCts = _webCodecsCaptureCts;
						if (oldCts != null && !oldCts.IsCancellationRequested)
							oldCts.Cancel();
						break;
				}

				_currentCaptureMode = newMode;
			}

			// 旧ループの完了待機（デフォルト 7000ms）
			if (oldCts != null)
			{
				await WaitForOldLoopCompletionAsync(oldCts);
			}

			// 新ループの起動
			switch (newMode)
			{
				case CaptureMode.WebCodecs:
					{
						var initResult = await InitializeWebCodecsEncoderAsync();
						if (initResult)
						{
							var cts = new CancellationTokenSource();
							lock (_modeLock)
							{
								_webCodecsCaptureCts = cts;
								_webCodecsCaptureTask = Task.Run(
									() => WebCodecsCaptureLoopAsync(cts.Token));
							}
							Log(2, "[RemoteController] WebCodecs キャプチャループを起動しました");
						}
						else
						{
							Log(3, "[RemoteController] WebCodecs 初期化失敗、WebP にフォールバック");
							// 【Major修正】 初期化失敗時はWebPループを再起動（モード切替のみの旧実装は配信停止を発生）
							await SetCaptureModeAsync(CaptureMode.WebP);
						}
					}
					break;
				case CaptureMode.WebP:
					{
						// WebP ループの起動（中央キャプチャループは接続時に自動起動）
						// 既に接続クライアントがいる場合は明示的に起動
						if (_centralCaptureCts == null || _centralCaptureCts.IsCancellationRequested)
						{
							var cts = new CancellationTokenSource();
							lock (_modeLock)
							{
								_centralCaptureCts = cts;
							}
							_forceNextKeyFrame = true;
							_centralCaptureTask = Task.Run(() => CentralCaptureLoopAsync(cts.Token));
							Log(2, "[RemoteController] 中央キャプチャループ(WebP)を起動しました");
						}
					}
					break;
			}
		}

		//====================================================================================
		// 処理ブロック名 : SendCaptureConfig
		// 概要           : WebCodecs 設定情報を JSON で返却する。
		//------------------------------------------------------------------------------------
		private void SendCaptureConfig(HttpListenerResponse res)
		{
			try
			{
				lock (_modeLock)
				{
					var json = $"{{\"mode\":\"{_currentCaptureMode.ToString().ToLower()}\",\"bitrate\":{_webCodecsBitrate},\"fps\":{_webCodecsFps}}}";
					res.ContentType = "application/json";
					var bytes = Encoding.UTF8.GetBytes(json);
					res.ContentLength64 = bytes.Length;
					res.OutputStream.Write(bytes, 0, bytes.Length);
				}
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] SendCaptureConfig エラー: {ex.Message}");
				res.StatusCode = 500;
			}
			finally
			{
				res.Close();
			}
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
		//====================================================================================
		// 処理ブロック名 : RemoveWebSocketSession
		// 概要           : WebSocketセッションを即時削除し、保持しているリソースをすべて解放する。
		//                  セッションのタスク（ファンアウト/送信/受信）をキャンセルし、
		//                  SharedFrameBufferの参照カウントが正しくデクリメントされることを保証する。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/29
		// version        : 1.00 (フェーズ6：接続管理とクリーンアップの実装)
		//------------------------------------------------------------------------------------
		private void RemoveWebSocketSession(string sessionId)
		{
			if (_webSocketSessions.TryRemove(sessionId, out WebSocketSession session))
			{
				Log(2, $"[RemoteController] WebSocket セッション削除開始: SessionID={sessionId}");

				// 1. CancellationToken をキャンセルして全ループタスクを停止
				session.CancellationToken.Cancel();

				// 2. 接続カウントをデクリメント
				Interlocked.Decrement(ref _activeConnectionCount);

				// 3. 【二次レビュー Major 修正】 クライアントバージョンエントリを削除
				// 設計書 §5.3.3 定義: WebSocket 切断時に _clientVersions からエントリを削除
				RemoveClientVersion(sessionId);

				// 4. セッションの Dispose（SemaphoreSlim, CancellationTokenSource, バッファ返却等）
				session.Dispose();

				Log(2, $"[RemoteController] WebSocket セッション削除完了: SessionID={sessionId}, 接続数={Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0)}/{MaxConcurrentClients}");
			}
		}

		//====================================================================================
		// 処理ブロック名 : IsWebSocketOpen
		// 概要           : [二次レビュー修正] WebSocketの接続状態を安全に確認するヘルパーメソッド。
		//                  WebSocketState プロパティのアクセスを try-catch で保護し、
		//                  Abort() や CloseAsync() 呼び出し後の例外発生を防止する。
		//------------------------------------------------------------------------------------
		private bool IsWebSocketOpen(System.Net.WebSockets.WebSocket ws)
		{
			try
			{
				return ws.State == WebSocketState.Open;
			}
			catch
			{
				return false;
			}
		}

		private async Task HandleWebSocketAsync(HttpListenerContext context)
		{
			var req = context.Request;
			var res = context.Response;
			System.Net.WebSockets.WebSocket ws = null;
			string sessionId = null;

			// 認証チェック
			if (!IsAuthenticated(req, res))
			{
				Log(2, $"[RemoteController] WebSocket 接続拒否: 未認証 (IP: {req.RemoteEndPoint})");
				res.StatusCode = 401;
				res.Close();
				return;
			}

			// 最大接続数制限チェック（アトミックな比較-交換）
			// Interlocked.CompareExchange を使用して、Check-then-Act のレースコンディションを排除
			int currentCount = Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0);
			if (currentCount >= MaxConcurrentClients)
			{
				Log(2, $"[RemoteController] WebSocket 接続拒否: 最大接続数 ({MaxConcurrentClients}) に到達 (IP: {req.RemoteEndPoint})");
				res.StatusCode = 503; // Service Unavailable
				res.Close();
				return;
			}

			try
			{
				// WebSocket アップグレード
				var acceptResult = await context.AcceptWebSocketAsync(null);
				ws = acceptResult.WebSocket;

				// セッションIDを生成（クッキーから取得、または新規発行）
				sessionId = GetCookieValue(req, "session_id") ?? Guid.NewGuid().ToString("N");
				if (string.IsNullOrEmpty(GetCookieValue(req, "session_id")))
				{
					_sessions.TryAdd(sessionId, new SessionInfo(sessionId));
				}

				var session = new WebSocketSession(ws, sessionId);

				// アトミックに接続カウントをインクリメント（上限チェック）
				int count = Interlocked.Increment(ref _activeConnectionCount);
				if (count > MaxConcurrentClients)
				{
					// レースコンディションで上限超過: 接続を拒否
					Interlocked.Decrement(ref _activeConnectionCount);
					Log(2, $"[RemoteController] WebSocket 接続拒否: 最大接続数 ({MaxConcurrentClients}) に到達 - レースコンディション (IP: {req.RemoteEndPoint})");
					// 標準的な WebSocket 切断フローを使用（Abort ではなく CloseOutputAsync + CloseAsync）
					try { await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "接続上限に到達", CancellationToken.None); } catch { }
					try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "接続上限に到達", CancellationToken.None); } catch { }
					res.StatusCode = 503;
					res.Close();
					return;
				}

				_webSocketSessions.TryAdd(sessionId, session);

				// 【二次レビュー Major 修正】 クライアントバージョンの初期化（null = 未決状態）
				// 設計書 §5.3.3 定義: 接続確立時に _clientVersions に null エントリを追加
				// クライアントから PROTOCOL_VERSION メッセージを受信するまでバージョン未決状態を保持
				_clientVersions[sessionId] = null;

				Log(2, $"[RemoteController] WebSocket 接続確立: SessionID={sessionId}, IP={req.RemoteEndPoint}, 接続数={count}/{MaxConcurrentClients}");

				// 中央キャプチャループが起動していない場合は起動（フェーズ5）
				// [Task05-対策1] タスク完了状態も判定条件に追加（自動停止後の再起動を確実に検知）
				// [外部レビュー 指摘2] _centralCaptureTask == null を含める（TOCTOU 競合対策）
				// 【Critical修正】 WebP モード時のみ中央キャプチャループ(WebP)を起動
				// WebCodecs モード中は WebP ループが二重起動するのを防止
				if (_currentCaptureMode == CaptureMode.WebP &&
					(_centralCaptureCts == null || _centralCaptureCts.IsCancellationRequested ||
					 _centralCaptureTask == null || _centralCaptureTask.IsCompleted))
				{
					lock (_captureStartLock)
					{
						// 再チェック（ダブルチェックロックパターン）
						if (_currentCaptureMode == CaptureMode.WebP &&
							(_centralCaptureCts == null || _centralCaptureCts.IsCancellationRequested ||
							 _centralCaptureTask == null || _centralCaptureTask.IsCompleted))
						{
							_centralCaptureCts?.Dispose();
							_centralCaptureCts = new CancellationTokenSource();
							// [外部レビュー 指摘1] 再起動時は必ずフルキーフレームを要求
							_forceNextKeyFrame = true;
							_centralCaptureTask = Task.Run(() => CentralCaptureLoopAsync(_centralCaptureCts.Token));
							Log(2, "[RemoteController] 中央キャプチャループ(WebP)を起動しました (フェーズ5)");
						}
					}
				}

				// セッション固有のタスクを開始（ファンアウト / 送信 / 受信）
				session.CaptureTask = Task.Run(() => WebSocketFanoutLoopAsync(session));
				session.SendTask = Task.Run(() => WebSocketSendLoopAsync(session));
				session.ReceiveTask = Task.Run(() => WebSocketReceiveLoopAsync(session));
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WebSocket アップグレードエラー (IP: {req.RemoteEndPoint}): {ex.GetType().Name} - {ex.Message}");

				// セッションが既に追加された場合はクリーンアップ
				if (sessionId != null)
				{
					RemoveWebSocketSession(sessionId);
				}

				// リソースリーク防止のため強制的にソケットを破棄
				try { ws?.Abort(); } catch { }
				try { res.StatusCode = 500; res.Close(); } catch { }
			}
		}

		//====================================================================================
		// 処理ブロック名 : CentralCaptureLoopAsync
		// 概要           : 単一の中央キャプチャタスクでCefSharpから画面をキャプチャし、
		//                  共有フレームバッファに格納してから全セッションに通知する。
		//                  CefSharpへのキャプチャ要求は常に1回/フレーム（O(1)負荷）。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/29
		// version        : 1.00 (フェーズ5：中央キャプチャ・ファンアウト方式)
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
					// [Task05-対策1 / CriticalReview 指摘1 対策] 省電力化: 接続数が0台になったら即座にループを抜ける
					// ※ 接続数チェックと _centralCaptureTask のクリアを lock 内でアトミックに実行し TOCTOU 競合を完全防止
					if (Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0) <= 0)
					{
						lock (_captureStartLock)
						{
							if (Interlocked.CompareExchange(ref _activeConnectionCount, 0, 0) <= 0)
							{
								Log(2, "[RemoteController] 接続クライアントが0台になったため、中央キャプチャループを停止します");
								_centralCaptureTask = null;
								break;
							}
						}
					}

					// [外部レビュー 指摘1] 再起動後初回、または強制フラグが立っている場合は isInit = true でフルフレームを取得
					// ※ CefSharp JS側が差分キャッシュを保持しており、接続直後に type == "none" を返すケースを防ぐ
					bool isInit = isFirstFrameAfterStart || _forceNextKeyFrame;
					if (isInit)
					{
						_forceNextKeyFrame = false;
						isFirstFrameAfterStart = false;
					}

					sw.Restart();
	
					// 画面キャプチャ（CefSharpへの要求: 1回/フレーム）
					// [案G-1] CaptureCanvasAsync の実行時間を Stopwatch で計測
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
							// [二次レビュー修正] ToArray による毎フレームのアロケーションを排除し、直接 .Values を列挙
							foreach (var session in _webSocketSessions.Values)
							{
								if (session.WebSocket.State == WebSocketState.Open)
								{
									// フレーム到着シグナル（Channelを使用し、例外発生を防止）
									// TryWrite はチャンネルが一杯でも例外を投げず、false を返す
									// [Critical修正] 設計書 §5.1.2 規定: TryWrite 失敗時はフレームドロップとして処理
									if (!session._frameSignal.Writer.TryWrite(seq))
									{
										// 【Minor修正】 session._droppedFrameCount を使用（設計書 §1740-1731 の規定：メソッドスコープではなくセッションスコープ）
										// 設計書 §1740-1731 ではメソッドスコープの droppedCount を規定しているが、
										// セッション全体のドロップ回数の累積統計は運用上のデバッグ・監視に有用なため、
										// セッションスコープのフィールドとして実装している（設計書整合性の明記）
										session._droppedFrameCount++;
										// 【Critical修正】 設計書 §5.3.2 規定: キュー溢れ警告ログ出力（M1対応）
										Log(3, $"[RemoteController] _frameSignal queue full, dropping frame {seq} (session={session.SessionId}, dropped={session._droppedFrameCount})");
									}
								}
							}
						}
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
				// [外部レビュー 指摘2] タスク終了時に参照をクリアし、TOCTOU レース条件を排除
				lock (_captureStartLock)
				{
					_centralCaptureTask = null;
				}
				Log(2, "[RemoteController] 中央キャプチャループが完全に終了しました");
			}
		}
		
		//====================================================================================
		// 処理ブロック名 : RecordCaptureLatency
		// 概要           : [案G-2] CaptureCanvasAsync の実行遅延統計を記録し、
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
		
				// [案G-3] FPS推定値算出
				var theoreticalMaxFps = avg > 0 ? 1000.0 / avg : 0;
		
				if (avg > 500)
				{
					Log(3, $"[Profile] High IPC latency: avg={avg:F0}ms, min={min}ms, max={max}ms, theoreticalMaxFPS={theoreticalMaxFps:F1}");
				}
				else
				{
					// [二次レビュー修正] 正常系ログを100フレームに1回に抑制（ログI/O負荷軽減）
					if (Interlocked.Increment(ref _sendFrameCounter) % 100 == 0)
					{
						Log(3, $"[Profile] CaptureCanvasAsync: {elapsedMs}ms, avg={avg:F0}ms, min={min}ms, max={max}ms, theoreticalMaxFPS={theoreticalMaxFps:F1}, samples={_captureLatencyHistory.Count}");
					}
				}
			}
		}
	
		//====================================================================================
		// 処理ブロック名 : WebSocketFanoutLoopAsync (最終修正版)
		// 概要           : 中央キャプチャからのフレームをセッションごとに配信するループ。
		//                  ・セッションの要求周期が中央キャプチャ周期と同等以下の場合は
		//                    中央キャプチャ側のペース制御を信頼し、ファンアウト側での制限をスキップ（バイパス）。
		//                  ・他セッションの高FPS要求により中央キャプチャが高周期（16ms等）で動いている場合のみ
		//                    本セッションの目標FPSに合わせてフレームを間引き（ドロップ）する。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/29
		// version        : 2.00 (v3.0設計書によるバイパス条件・マージン・統計変数修正)
		//------------------------------------------------------------------------------------
		private async Task WebSocketFanoutLoopAsync(WebSocketSession session)
		{
			var ws = session.WebSocket;
			var token = session.CancellationToken.Token;
		
			int lastSentSequenceNumber = 0;
			long lastSentTicks = 0;
			double ticksPerMs = (double)System.Diagnostics.Stopwatch.Frequency / 1000.0;
			long droppedFramesCount = 0;
			int sessionFrameCounter = 0;
		
			try
			{
				while (!token.IsCancellationRequested && IsWebSocketOpen(ws))
				{
					int targetInterval = session.TargetIntervalMs <= 0 ? 16 : session.TargetIntervalMs;
					bool frameReady = await session.WaitForFrameAsync(targetInterval, token);
			
					if (frameReady)
					{
						int centralInterval = GetMaxFpsIntervalMs();
		
						// [バイパス判定]
						// 1. targetInterval <= 16 (60FPS) は無条件バイパス
						// 2. targetInterval <= centralInterval の場合、中央キャプチャループが既に
						//    このセッションと同等以上の周期で制御しているため、ファンアウト側でのスロットリングは不要（バイパス）
						if (targetInterval > 16 && targetInterval > centralInterval)
						{
							long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
							if (lastSentTicks != 0)
							{
								double elapsedMs = (currentTicks - lastSentTicks) / ticksPerMs;
								// クロックのゆらぎ（1~4ms）を考慮したマージン（-4ms）で間引き判定
								if (elapsedMs < (targetInterval - 4))
								{
									droppedFramesCount++;
									continue; // このフレームはドロップして送信スキップ
								}
							}
							lastSentTicks = currentTicks;
						}
						else
						{
							// バイパス時: 送信基準時刻のみ更新し、即座に送信へ進む
							lastSentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
						}
		
						// 共有バッファから最新のフレームを取得（参照カウント +1）
						var (data, dataLength, seq) = _sharedFrameBuffer.GetFrame();
		
						// 前回送信フレームとの重複チェック（符号付き整数差分で過去フレーム検出）
						int diff = seq - lastSentSequenceNumber;
						if (diff <= 0 && lastSentSequenceNumber != 0)
						{
							_sharedFrameBuffer.ReleaseFrame(data);
							continue;
						}
		
						try
						{
							if (data != null && dataLength > 0)
							{
								await session.SendSemaphore.WaitAsync(token);
								try
								{
									if (IsWebSocketOpen(ws))
									{
										// [ver 3.0修正] ヘッダーを7バイトに拡張 (Version+Type+Flags+Seq)
										byte[] combined = _bufferPool.Rent(7 + dataLength);
										try
										{
											var header = session._headerBuffer;
											header[0] = (byte)WebSocketProtocolVersion;
											header[1] = 0;  // Type
											header[2] = 0;  // Flags
											header[3] = (byte)(seq >> 24);
											header[4] = (byte)(seq >> 16);
											header[5] = (byte)(seq >> 8);
											header[6] = (byte)seq;
		
											header.CopyTo(combined, 0);
											Buffer.BlockCopy(data, 0, combined, 7, dataLength);
		
											await ws.SendAsync(
												new ArraySegment<byte>(combined, 0, 7 + dataLength),
												WebSocketMessageType.Binary, true, token);
		
											lastSentSequenceNumber = seq;
		
											// 可観測性: 100フレームごとの動作統計ログ
											if (++sessionFrameCounter % 100 == 0)
											{
												Log(3, $"[Profile] Fanout sent seq={seq}, targetInterval={targetInterval}ms, centralInterval={centralInterval}ms, dropped={droppedFramesCount}, sessions={GetActiveSessionCount()}");
												droppedFramesCount = 0;
											}
										}
										finally
										{
											_bufferPool.Return(combined);
										}
									}
								}
								finally
								{
									session.SendSemaphore.Release();
								}
							}
						}
						finally
						{
							_sharedFrameBuffer.ReleaseFrame(data);
						}
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] Fanout loop exception: {ex.Message}");
			}
			finally
			{
				RemoveWebSocketSession(session.SessionId);
			}
		}

		//====================================================================================
		// 処理ブロック名 : WebSocketCaptureLoopAsync
		// 概要           : [Phase 5] 廃止予定。各セッション独立キャプチャを中央キャプチャへ移行。
		//                  後方互換性のため残すが一時的に無効化されている。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 1.00
		//------------------------------------------------------------------------------------
		// [Phase 5: 廃止] 中央キャプチャ方式へ移行したため、このメソッドは使用されません。
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
				while (!token.IsCancellationRequested && IsWebSocketOpen(ws))
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
						// 2026/07/29 修正: ArrayPool を使用して GC 負荷を低減
						var (webpData, actualDataLength) = ConvertToBytesOptimized(dataUrl, "data:image/webp");
						if (webpData != null && actualDataLength > 0)
						{
							// [Fix] SendSemaphore 取得前にシーケンス番号を確保
							// HTTP側との競合防止: シーケンス番号はセマフォ外で確保し、
							// セマフォ保持時間を送信処理のみに極小化
							int seq = Interlocked.Increment(ref _globalSequenceNumber);

							// 6バイトヘッダーを事前に構築（セマフォ外で準備）
							byte[] header = new byte[6];
							header[0] = WebSocketProtocolVersion;
							header[1] = 0; // Flags = 0
							// Big-Endian シーケンス番号を直接書き込み（BitConverter+BlockCopyの中間配列を排除）
							header[2] = (byte)(seq >> 24);
							header[3] = (byte)(seq >> 16);
							header[4] = (byte)(seq >> 8);
							header[5] = (byte)seq;

							// [FPS Fix] セマフォ待機時間を測定
							var swWait = System.Diagnostics.Stopwatch.StartNew();
							await session.SendSemaphore.WaitAsync(token);
							long semaphoreWaitMs = swWait.ElapsedMilliseconds;
							swWait.Stop();
							try
							{
								if (IsWebSocketOpen(ws))
								{
									// [FPS Fix] WebSocketフラグメント化を排除: ヘッダーとデータを単一メッセージとして送信
									byte[] combined = _bufferPool.Rent(6 + actualDataLength);
									try
									{
										// ヘッダーを結合バッファに書き込み
										combined[0] = WebSocketProtocolVersion;
										combined[1] = 0; // Flags = 0
										combined[2] = (byte)(seq >> 24);
										combined[3] = (byte)(seq >> 16);
										combined[4] = (byte)(seq >> 8);
										combined[5] = (byte)seq;
										// データをコピー
										Buffer.BlockCopy(webpData, 0, combined, 6, actualDataLength);
										
										// [FPS Fix] 単一メッセージとして送信 (endOfMessage = true)
										var swSendLocal = System.Diagnostics.Stopwatch.StartNew();
										await ws.SendAsync(
											new ArraySegment<byte>(combined, 0, 6 + actualDataLength),
											WebSocketMessageType.Binary, true, token);
										long sendMs = swSendLocal.ElapsedMilliseconds;
										
										// [FPS Fix] 可観測性: 送信所要時間・セマフォ待機時間をログ出力（100フレームに1回）
										if (Interlocked.Add(ref _sendFrameCounter, 1) >= 100)
										{
											Interlocked.Exchange(ref _sendFrameCounter, 0);
											Log(3, $"[RemoteController] SendProfile CaptureLoop: send={sendMs}ms, semaphore_wait={semaphoreWaitMs}ms, data={actualDataLength}B (Session: {session.SessionId})");
										}
									}
									finally
									{
										_bufferPool.Return(combined);
									}
								}
							}
							finally
							{
								session.SendSemaphore.Release();
							}

							// ArrayPool への返却（送信完了後）
							_bufferPool.Return(webpData);

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

			// [案E-2] Heartbeatスキップカウンター（サーバーC#側管理）
			int consecutiveHeartbeatSkips = 0;
			const int MAX_HEARTBEAT_SKIP = 3; // サーバーC#のPing/Pongタイムアウトと整合

			try
			{
				while (!token.IsCancellationRequested && IsWebSocketOpen(ws))
				{
					await Task.Delay(WebSocketHeartbeatIntervalMs, token);

					if (ws.State != WebSocketState.Open) break;

					// Heartbeat 処理
					if ((DateTime.UtcNow - session.LastHeartbeat).TotalMilliseconds > WebSocketHeartbeatIntervalMs)
					{
						// [案E-2] ノンブロッキング送信: セマフォ取得できない場合はスキップ
						// .NET Framework 4.8ではTryWait(0)が存在しないためWait(0)を使用
						if (session.SendSemaphore.Wait(0))
						{
							try
							{
								if (IsWebSocketOpen(ws))
								{
									var segment = new ArraySegment<byte>(HeartbeatBytes);
									await ws.SendAsync(segment, WebSocketMessageType.Text, true, token);
									session.LastHeartbeat = DateTime.UtcNow;
									consecutiveHeartbeatSkips = 0; // 送信成功でリセット
								}
							}
							finally
							{
								session.SendSemaphore.Release();
							}
						}
						else
						{
							consecutiveHeartbeatSkips++;
							if (consecutiveHeartbeatSkips >= MAX_HEARTBEAT_SKIP)
							{
								// 3回連続スキップ → 接続切断をトリガー
								Log(3, $"[RemoteController] Heartbeat {consecutiveHeartbeatSkips}回連続スキップ (Session: {session.SessionId}): 接続切断");
								session.CancellationToken.Cancel();
							}
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
			// [二次レビュー修正] 受信バッファを64KBに拡大（1920x1080高画質WebPは50KB〜150KB）
			byte[] receiveBuffer = _bufferPool.Rent(65536);

			try
			{
				while (!token.IsCancellationRequested && IsWebSocketOpen(ws))
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
						Buffer.BlockCopy(receiveBuffer, 0, messageBytes, 0, result.Count);
						string message = Encoding.UTF8.GetString(messageBytes, 0, result.Count);
						await ProcessWebSocketControlMessageAsync(session, message);
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
				_bufferPool.Return(receiveBuffer);
			}
		}

		//====================================================================================
		// 処理ブロック名 : ProcessWebSocketControlMessage
		// 概要           : WebSocket 経由で受信した制御メッセージを処理します。
		//                  形式: JSON { "action": "init" }, { "action": "move", "x": 0.5, "y": 0.5 }
		//                  2026/07/29 myi 修正：async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/27
		// version        : 3.00 (async Task化、セッション固有カーソル対応)
		//------------------------------------------------------------------------------------
		private async Task ProcessWebSocketControlMessageAsync(WebSocketSession session, string message)
		{
			try
			{
				string action = ExtractJsonString(message, "action");
				string xStr = ExtractJsonString(message, "x");
				string yStr = ExtractJsonString(message, "y");
	
				// 【二次レビュー Major 修正】 PROTOCOL_VERSION: クライアントのプロトコルバージョンを受信
				// 設計書 §5.3.3 定義: クライアント接続時に PROTOCOL_VERSION を送信し、サーバーがセッションごとにバージョンを記録
				// バージョン番号は将来の仕様が互換性なく変更される場合にインクリメント
				// Version 2: 7Bヘッダー (Version + Type + Flags + Seq)
				if (action == "PROTOCOL_VERSION")
				{
					string versionStr = ExtractJsonString(message, "version");
					if (int.TryParse(versionStr, out int version))
					{
						SetClientVersion(session.SessionId, version);
						Log(2, $"[RemoteController] PROTOCOL_VERSION 受信: version={version} (Session: {session.SessionId})");
					}
					else
					{
						Log(3, $"[RemoteController] PROTOCOL_VERSION 無効なバージョン値: '{versionStr}' (Session: {session.SessionId})");
					}
					return;
				}
	
				if (action == "init")
				{
					Log(2, $"[RemoteController] WebSocket init 要求を受領: 初期化フルフレームを生成します (Session: {session.SessionId})");
					// 専用セマフォ: init処理専用のセマフォを使用し、SendSemaphoreとの競合を防止
					await session.InitSemaphore.WaitAsync(session.CancellationToken.Token);
					try
					{
						// 1. 画面キーフレームのキャプチャと変換（SendSemaphore 取得前）
						// 2026/07/29 修正: キャプチャと変換を SendSemaphore 取得前に行い、
						//    SendSemaphore の保持時間を送信処理のみ(最小)に制限
						var capture = await CaptureCanvasAsync(true); // isInit = true
						byte[] webpData = null;
						int actualDataLength = 0;
						if (capture.Item1 == "full" && !string.IsNullOrEmpty(capture.Item2))
						{
							var result = ConvertToBytesOptimized(capture.Item2, "data:image/webp");
							webpData = result.Buffer;
							actualDataLength = result.ActualLength;
						}

						// 2. フレーム送信（SendSemaphore 保持時間を最小化）
						if (webpData != null && actualDataLength > 0)
						{
							// [Fix] SendSemaphore 取得前にシーケンス番号を確保
							// HTTP側との競合防止: シーケンス番号はセマフォ外で確保し、
							// セマフォ保持時間を送信処理のみに極小化
							int seq = Interlocked.Increment(ref _globalSequenceNumber);

							// [二次レビュー修正] 既存の _headerBuffer を流用（毎回の new byte[6] によるアロケーションを排除）
							// [ver 3.0修正] 7Bヘッダー対応
							var header = session._headerBuffer;
							header[0] = (byte)WebSocketProtocolVersion;
							header[1] = 0; // Type
							header[2] = 0x01; // Flags: initフレームはキーフレーム (Bit 0 = isKeyFrame)
							// Big-Endian シーケンス番号を直接書き込み（BitConverter+BlockCopyの中間配列を排除）
							header[3] = (byte)(seq >> 24);
							header[4] = (byte)(seq >> 16);
							header[5] = (byte)(seq >> 8);
							header[6] = (byte)seq;

							// [FPS Fix] セマフォ待機時間を測定
							var swWait = System.Diagnostics.Stopwatch.StartNew();
							await session.SendSemaphore.WaitAsync(session.CancellationToken.Token);
							long semaphoreWaitMs = swWait.ElapsedMilliseconds;
							swWait.Stop();
							try
							{
								if (session.WebSocket.State == WebSocketState.Open)
								{
									// [FPS Fix] WebSocketフラグメント化を排除: ヘッダーとデータを単一メッセージとして送信
									// [ver 3.0修正] 7Bヘッダー対応
									byte[] combined = _bufferPool.Rent(7 + actualDataLength);
									try
									{
										// ヘッダーを結合バッファに書き込み
										combined[0] = (byte)WebSocketProtocolVersion;
										combined[1] = 0; // Type
										combined[2] = 0x01; // Flags: initフレームはキーフレーム (Bit 0 = isKeyFrame)
										combined[3] = (byte)(seq >> 24);
										combined[4] = (byte)(seq >> 16);
										combined[5] = (byte)(seq >> 8);
										combined[6] = (byte)seq;
										// データをコピー
										Buffer.BlockCopy(webpData, 0, combined, 7, actualDataLength);
										
										// [FPS Fix] 単一メッセージとして送信 (endOfMessage = true)
										var swSend = System.Diagnostics.Stopwatch.StartNew();
										await session.WebSocket.SendAsync(
											new ArraySegment<byte>(combined, 0, 7 + actualDataLength),
											WebSocketMessageType.Binary, true, session.CancellationToken.Token);
										long sendMs = swSend.ElapsedMilliseconds;
										
										// [FPS Fix] 可観測性ログ
										Log(2, $"[RemoteController] WebSocket init フルフレーム送信成功 ({actualDataLength} bytes, seq={seq}, send={sendMs}ms, semaphore_wait={semaphoreWaitMs}ms, Session: {session.SessionId})");
									}
									finally
									{
										_bufferPool.Return(combined);
									}
								}
							}
							finally
							{
								session.SendSemaphore.Release();
							}
						}
						// ArrayPool への返却（送信完了後）
						if (webpData != null)
						{
							_bufferPool.Return(webpData);
						}

						// 3. HUD全データのパケット送信（init_hud）
						await SendInitHudDataAsync(session);
					}
					catch (Exception ex)
					{
						Log(3, $"[RemoteController] WebSocket init エラー: {ex.Message}");
					}
					finally
					{
						session.InitSemaphore.Release();
					}
				}
				else if (action == "set_fps")
				{
					string intervalStr = ExtractJsonString(message, "intervalMs");
					if (int.TryParse(intervalStr, out int intervalMs))
					{
						session.TargetIntervalMs = Math.Max(0, Math.Min(300000, intervalMs));

						// [外部レビュー 指摘5] FPS変更を受信したら中央キャプチャのFPSキャッシュをクリア
						// ※ GetMaxFpsIntervalMs() は _cachedSessionCount が一致している場合、
						//   キャッシュされた _cachedMaxFpsInterval を返すため、
						//   接続数が変わらない場合でも再計算されるよう強制する
						_cachedSessionCount = -1;

						int jsInterval = (session.TargetIntervalMs <= 0) ? 16 : Math.Max(16, session.TargetIntervalMs);
						Log(2, $"[RemoteController] WebSocket 目標間隔変更受領: {session.TargetIntervalMs}ms → JS側: {jsInterval}ms (Session: {session.SessionId})");

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
					session.SetCursorX(Math.Max(0, Math.Min(1.0, x)));
					session.SetCursorY(Math.Max(0, Math.Min(1.0, y)));
					await TriggerMouseMoveAsync(session);
				}
				else if (action == "click")
				{
					await TriggerMouseClickAsync(session);
				}
				else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					session.SetCursorX(Math.Max(0, Math.Min(1.0, tx)));
					session.SetCursorY(Math.Max(0, Math.Min(1.0, ty)));
					await TriggerMouseClickAsync(session);
				}
				else if (action == "mousedown")
				{
					await TriggerMouseDownAsync(session);
				}
				else if (action == "mouseup")
				{
					await TriggerMouseUpAsync(session);
				}
				// [task19 P3] WCS モード切替（クライアント switchToWcsMode() → { action: 'switch_mode', mode: 'webcodecs' }）
				// 設計書 §5.2 整合: クライアントは既存 WebSocket 接続を維持したまま
				// sendWsControl を用いてモード切替を要求し、サーバーが SetCaptureModeAsync を実行する
				else if (action == "switch_mode")
				{
					// [task19 レビュー指摘 M2b] レートリミット: SetCaptureModeAsync はキャプチャループの停止・起動を伴うため、
					// 5秒以内の連続要求は拒否して再起動ストームを防止する（クライアント側 sendWsControl にはスロットリングがない）
					if (!session.TryAcceptSwitchMode())
					{
						Log(3, $"[RemoteController] switch_mode レートリミットで拒否 (Session: {session.SessionId})");
						return;
					}

					// [task19 レビュー指摘 m1] 厳格な入力バリデーション:
					// JSON の "mode" 欠損（null）と無効値（想定外文字列）を区別してログし、
					// いずれも安全側（WebP）へフォールバックする（例外の握りつぶし禁止のためログ出力）
					string modeStr = ExtractJsonString(message, "mode");
					CaptureMode targetMode;
					if (modeStr == null)
					{
						Log(3, $"[RemoteController] switch_mode: \"mode\" フィールド欠損のため WebP にフォールバック (Session: {session.SessionId})");
						targetMode = CaptureMode.WebP;
					}
					else
					{
						switch (modeStr.Trim().ToLowerInvariant())
						{
							case "webp": targetMode = CaptureMode.WebP; break;
							case "webcodecs": targetMode = CaptureMode.WebCodecs; break;
							default:
								Log(3, $"[RemoteController] switch_mode: 無効なモード値 \"{modeStr}\"、WebP にフォールバック (Session: {session.SessionId})");
								targetMode = CaptureMode.WebP;
								break;
						}
					}
					Log(2, $"[RemoteController] WebSocket switch_mode 受信: {targetMode} (Session: {session.SessionId})");
					await SetCaptureModeAsync(targetMode);

					// [task19 P3] edge case 対策: サーバーが既に WebCodecs モードの場合、
					// SetCaptureModeAsync は早期リターン（旧=新モード）するため SPS/PPS 再送信されない。
					// 一方、クライアント switchToWcsMode() はデコーダ状態をリセット済み（_videoDecoderDescription=null）
					// であるため、DescriptionSent をリセットし、次フレームで SPS/PPS を再送信させる
					if (targetMode == CaptureMode.WebCodecs)
					{
						session.DescriptionSent = false;
						// [task19 レビュー指摘 M1] バックオフ状態もリセットし、即座に再送を許可する
						session.ResetDescriptionSendState();
						Log(2, $"[RemoteController] switch_mode(WebCodecs) で SPS/PPS 再送トリガー (Session: {session.SessionId})");
					}
				}
				// [task19 P3] SPS/PPS 再送信要求（クライアントがデコードエラー上限到達時に送信、設計書 §4.2.3 Major 4）
				// DescriptionSent をリセットすると WebCodecsCaptureLoopAsync が
				// 次フレームより前に Type=0x02 の SPS/PPS フレームを再送信する
				else if (action == "reinit_decoder")
				{
					// [task19 レビュー指摘 M2] レートリミット: クライアントの 5秒スロットリング（_clampReinitIntervalMs）と整合させ、
					// 5秒以内の連続受信を拒否して SPS/PPS の無限再送（帯域浪費・サーバー負荷増大）を防ぐ
					if (!session.TryAcceptReinitDecoder())
					{
						Log(3, $"[RemoteController] reinit_decoder レートリミットで拒否 (Session: {session.SessionId})");
						return;
					}

					// [task19 レビュー指摘 M2] キャッシュ未保持時は SPS/PPS を再送できないため、
					// フラグリセットのみ行っても再送信が起きない状態になる。警告ログで可視化する
					if (_lastWebCodecsDescription == null)
					{
						Log(3, $"[RemoteController] reinit_decoder 受信: SPS/PPS キャッシュが存在しないため再送信できません (Session: {session.SessionId})");
					}
					session.DescriptionSent = false;
					// [task19 レビュー指摘 M1] バックオフ状態もリセットし、即座に再送を許可する
					session.ResetDescriptionSendState();
					Log(2, $"[RemoteController] reinit_decoder 受信: SPS/PPS を再送信します (Session: {session.SessionId}, cached={(_lastWebCodecsDescription?.Length.ToString() ?? "null")} bytes)");
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
		// 処理ブロック名 : SendInitHudDataAsync
		// 概要           : 再接続時にクライアントがHUDデータを最新の状態に同期するための
		//                  init_hudパケットを送信します。
		//------------------------------------------------------------------------------------
		// author         : myi
		// creation date  : 2026/07/29
		// version        : 1.00
		//------------------------------------------------------------------------------------
		private async Task SendInitHudDataAsync(WebSocketSession session)
		{
			string hudJson;
			lock (_hudLock)
			{
				hudJson = _cachedHudJson;
			}
	
			if (string.IsNullOrEmpty(hudJson)) return;
	
			// [Fix] _cachedHudJson が有効な JSON オブジェクトであることを検証
			// Trim() で前後の空白を許容し、"{..." }" 形式であることを確認
			var trimmed = hudJson.Trim();
			if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
			{
				Log(3, $"[RemoteController] init_hud 送信スキップ: _cachedHudJson はJSONオブジェクトではありません (Session: {session.SessionId})");
				return;
			}
		
			try
			{
				// [Fix] init_hud パケットのバイト列変換をセマフォ外で行い、
				//   SendSemaphore の保持時間を送信処理のみに極小化
				// [Fix] "data" キーを追加して有効なJSON構造とする
				//   変更前: {"type":"init_hud",{"material":...}} → 無効なJSON
				//   変更後: {"type":"init_hud","data":{"material":...}} → 有効なJSON
				//   注: hudJson が null/空の場合は string.IsNullOrEmpty(hudJson) で既に return している
				var packet = "{\"type\":\"init_hud\",\"data\":" + hudJson + "}";
				byte[] bytes = Encoding.UTF8.GetBytes(packet);
		
				await session.SendSemaphore.WaitAsync(session.CancellationToken.Token);
				try
				{
					if (session.WebSocket.State == WebSocketState.Open)
					{
						await session.WebSocket.SendAsync(
							new ArraySegment<byte>(bytes),
							WebSocketMessageType.Text, true, session.CancellationToken.Token);
					}
				}
				finally
				{
					session.SendSemaphore.Release();
				}
	
				Log(2, $"[RemoteController] init_hud パケット送信成功 (Session: {session.SessionId})");
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] init_hud 送信エラー: {ex.Message}");
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

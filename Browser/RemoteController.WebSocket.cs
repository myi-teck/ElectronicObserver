//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.WebSocket.cs
// summary        : WebSocket session, fanout, heartbeat, control messages
//----------------------------------------------------------------------------
// author         : myi
// creation date  : 2026-08-25
// version        : 1.00
//----------------------------------------------------------------------------
#region "change history"
// change history : 2026-08-25 myi task30 split out of RemoteController.cs
#endregion
// Copyright 2026 myi
//============================================================================================

using System;
using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
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
				_cursorXBits = BitConverter.DoubleToInt64Bits(0.5);
				_cursorYBits = BitConverter.DoubleToInt64Bits(0.5);
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

				// [task13 / B-2] 中央キャプチャループ起動判定を EnsureCentralCaptureLoopStarted() に統合
				// （TOCTOU 解消: _captureStartLock 内での再チェック / 旧 CTS は Dispose せず GC に委譲 —
				// dotnet/runtime#105811 参照: Dispose 後 .Token アクセスが ObjectDisposedException を投げ得るため
				// 旧タスク finally 側の try-catch と連携）
				EnsureCentralCaptureLoopStarted("ws_connect");

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
			// [task13 / B-4] フレーム到着待機連続タイムアウト回数（try 外で初期化、§7.4-5）
			int frameWaitTimeoutStreak = 0;
		
			try
			{
				while (!token.IsCancellationRequested && IsWebSocketOpen(ws))
				{
					int targetInterval = session.TargetIntervalMs <= 0 ? 16 : session.TargetIntervalMs;
					bool frameReady = await session.WaitForFrameAsync(targetInterval, token);
			
					if (frameReady)
					{
						// [task13 / R1-MINOR-1] 成功時のみリセット（失敗は else 分支でカウント）
						frameWaitTimeoutStreak = 0;
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
					else
					{
						// [task13 / B-4・L6] 自己救済: フレーム到着待ちが連続3回タイムアウトした場合、
						// 中央キャプチャループ不在を検知して再起動を試行する
						// [task13 / R1-MINOR-1] 失敗時のみカウント（成功時のみリセットする方式）
						frameWaitTimeoutStreak++;
						// [task13 / Minor 3] 連続タイムアウト閾値の定数化（フレーム到着待機が3回連続でタイムアウトで自己救済）
						if (frameWaitTimeoutStreak >= FrameWaitTimeoutStreakThreshold)
						{
							// [task13 / R1-MINOR-1] 戻り値に関わらずリセットして毎フレームのロック取得を防止
							frameWaitTimeoutStreak = 0;
							if (EnsureCentralCaptureLoopStarted("fanout_self_heal"))
							{
								Log(3, $"[RemoteController] Fanout 自己救済: 中央キャプチャループを再起動しました (session={session.SessionId})");
							}
						}
						// 中央ループは WebP モードのみ起動されるため、WCS モードでは何もしない（WCS は A-3 のループ自己救済で回復）
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

						// WebCodecs キャプチャループの目標 FPS も即座に更新
						// [task23 F-7] intervalMs <= 0 は「無制限 (MAX)」= 60fps 上限と解釈し、
						// JS 側 jsInterval = 16ms（60fps 相当）と一致させる（修正前は 30 で不一致）
						int targetFps = (session.TargetIntervalMs <= 0) ? 60 : Math.Max(1, Math.Min(60, (int)Math.Round(1000.0 / session.TargetIntervalMs)));
						SetWebCodecsFps(targetFps);
	
						// [task23 F-4/F-5] 旧実装「window._kancolleCaptureEngine.intervalMs」JS 注入を削除:
						// CaptureEngineScript（FormBrowser.cs）に intervalMs プロパティが存在せず no-op だったため
						// （空 catch { } も同時に解消・AGENTS.md NEVER「サイレントな例外の握りつぶし禁止」）
						// FPS 制御は C# WCS キャプチャループ（Task.Delay によるアダプティブ FPS）が唯一の正（source of truth）
					}
				}
				// [task14] クライアント中継イベントログの受信（§3.5a: move 分岐より前に配置）
				else if (action == "event_log")
				{
					string evJson = ExtractJsonString(message, "ev"); // 既存ヘルパを流用（JSON ライブラリ不使用・既存パターン）
					// [task14 Major-S2] 受信検証: 空・サイズ上限超過・JSON オブジェクト形状以外を拒否
					if (string.IsNullOrEmpty(evJson) || evJson.Length > 4096 ||
						!evJson.TrimStart().StartsWith("{") || !evJson.TrimEnd().EndsWith("}"))
					{
						Log(3, $"[RemoteController] event_log 無効な payload を拒否: len={evJson?.Length ?? 0} (Session: {session.SessionId})");
						return;
					}
					RemoteDedicatedLog.Event.Append(evJson);
				}
				else if (action == "move" && double.TryParse(xStr, out double x) && double.TryParse(yStr, out double y))
				{
					session.SetCursorX(Math.Max(0, Math.Min(1.0, x)));
					session.SetCursorY(Math.Max(0, Math.Min(1.0, y)));
					await TriggerMouseMoveAsync(session);
					// [task14 §3.5b] サーバ側アクション実行ログ（クライアント送信ログと時系列で突き合わせる）
					AppendServerActionLog("move", x, y, session.SessionId, "ws");
				}
				else if (action == "click")
				{
					double? cx = null;
					double? cy = null;
					if (double.TryParse(xStr, out double cxx) && double.TryParse(yStr, out double cyy))
					{
						cx = cxx;
						cy = cyy;
						session.SetCursorX(Math.Max(0, Math.Min(1.0, cxx)));
						session.SetCursorY(Math.Max(0, Math.Min(1.0, cyy)));
					}
					await TriggerMouseClickAsync(session);
					// [task14 §3.5b] サーバ側アクション実行ログ
					AppendServerActionLog("click", cx, cy, session.SessionId, "ws");
				}
				else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					session.SetCursorX(Math.Max(0, Math.Min(1.0, tx)));
					session.SetCursorY(Math.Max(0, Math.Min(1.0, ty)));
					await TriggerMouseClickAsync(session);
					// [task14 §3.5b] サーバ側アクション実行ログ
					AppendServerActionLog("touch_click", tx, ty, session.SessionId, "ws");
				}
				else if (action == "mousedown")
				{
					double? mx = null;
					double? my = null;
					if (double.TryParse(xStr, out double mxx) && double.TryParse(yStr, out double myy))
					{
						mx = mxx;
						my = myy;
						session.SetCursorX(Math.Max(0, Math.Min(1.0, mxx)));
						session.SetCursorY(Math.Max(0, Math.Min(1.0, myy)));
					}
					await TriggerMouseDownAsync(session);
					// [task14 §3.5b] サーバ側アクション実行ログ
					AppendServerActionLog("mousedown", mx, my, session.SessionId, "ws");
				}
				else if (action == "mouseup")
				{
					double? ux = null;
					double? uy = null;
					if (double.TryParse(xStr, out double uxx) && double.TryParse(yStr, out double uyy))
					{
						ux = uxx;
						uy = uyy;
						session.SetCursorX(Math.Max(0, Math.Min(1.0, uxx)));
						session.SetCursorY(Math.Max(0, Math.Min(1.0, uyy)));
					}
					await TriggerMouseUpAsync(session);
					// [task14 §3.5b] サーバ側アクション実行ログ
					AppendServerActionLog("mouseup", ux, uy, session.SessionId, "ws");
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
					// [task13] 生存性チェック: サーバが既に WebCodecs モードの場合、SetCaptureModeAsync は
					// 早期リターン（旧=新モード）するため、WCS ループが不在（前回 WebP 切替、または
					// モード切替で停止済み）の場合に再起動する。
					// ※ WCS ループは接続数0で自終了しない（停止は SetCaptureModeAsync の Cancel 経由のみ）。
					// [task13 / R1-MAJOR-1] 判定基準はクライアント送信の targetMode ではなく、
					// SetCaptureModeAsync 実行後の実効モード _currentCaptureMode を用いる。
					// SetCaptureModeAsync 内で初期化失敗 → WebP フォールバックした場合は _currentCaptureMode == WebP となり、
					// 生存性チェック（StartWebCodecsCaptureLoopAsync 再呼び出し）がスキップされるため、
					// 二重初期化・多重フォールバック（フォールバック後に再度 force 初期化失敗 → 再度 WebP フォールバック）を回避する。
					if (_currentCaptureMode == CaptureMode.WebCodecs)
					{
						session.DescriptionSent = false;
						// [task19 レビュー指摘 M1] バックオフ状態もリセットし、即座に再送を許可する
						session.ResetDescriptionSendState();
						// [task13] ループ不在（_webCodecsCaptureTask が null または完了）なら再起動
						// （_currentCaptureMode 基準のため、WebP フォールバック後の多重起動・多重フォールバックは起きない）
						var wcsTask = _webCodecsCaptureTask;
						if (wcsTask == null || wcsTask.IsCompleted)
						{
							// [task13 / L2] WCS ループ不在を検知、再起動
							Log(2, $"[RemoteController] switch_mode(WebCodecs) で WCS ループ不在を検知、再起動します (Session: {session.SessionId})");
							await StartWebCodecsCaptureLoopAsync();
						}
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

		// 2026/07/29 新規作成（WebSocketプロトコルバージョン定数）
		// バージョン番号は将来の仕様が互換性なく変更される場合にインクリメント
		// Version 2: 7Bヘッダー (Version + Type + Flags + Seq)
		private const int WebSocketProtocolVersion = 2;

		// 2026/07/29 新規作成（最大同時接続数制限）
		private const int MaxConcurrentClients = 5;

		// [task13 / Minor 3] フレーム到着待機の連続タイムアウト閾値（ファンアウト自己救済: N 回連続で中央キャプチャループを再起動）
		private const int FrameWaitTimeoutStreakThreshold = 3;

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

		// 2026/07/27 新規作成（WebSocket Heartbeat 用バイト列の事前構築）
		// 毎回の Encoding.UTF8.GetBytes("heartbeat") による一時バッファ割り当てを排除する。
		private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes("heartbeat");
	}
}

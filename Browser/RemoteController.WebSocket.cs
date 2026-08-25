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
	}
}

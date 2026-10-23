using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Browser
{
	/// <summary>
	/// ホスト側の表示状態（配信元画面が描画可能な状態か）。
	/// Degraded は「配信元で描画が停止・凍結する可能性が高い」ことを意味し、
	/// クライアントへ display_state 制御メッセージで通知する。
	/// </summary>
	public enum DisplayState
	{
		Normal,
		Degraded
	}

	//====================================================================================
	// 処理ブロック名 : RemoteController (DisplayState 部分クラス)
	// 概要           : ヘッドレス対策の一次検知。セッション接続形態（コンソール/RDP）、
	//                  モニタ接続数、WTS セッション状態から表示状態を判定し、
	//                  悪化時に display_state 制御メッセージを全セッションへ配信する。
	//                  検知は 5 秒ポーリング + WM_WTSSESSION_CHANGE イベントの二本立て
	//                  （イベント購読失敗時はポーリングのみで継続）。
	// 判定根拠       : ワークステーションロック状態は艦これの放置（rAF 低速化）と
	//                  区別不能なため判定入力にしない。RDP セッションは切断されても
	//                  セッション自体は生存するため WTSDisconnected を悪化の決定打とする。
	//====================================================================================
	public partial class RemoteController
	{
		//================================================================================
		// Win32 P/Invoke 定義
		//================================================================================

		/// <summary>セッション接続状態（WTS_CONNECTSTATE_CLASS）</summary>
		private const int WTSActive = 0;
		private const int WTSConnected = 1;
		private const int WTSDisconnected = 4;

		/// <summary>WTSQuerySessionInformationW の情報クラス（混同しやすい WTSSessionAddressV4=28 とは別物）</summary>
		private enum WTS_INFO_CLASS
		{
			WTSConnectState = 8,        // DWORD (WTS_CONNECTSTATE_CLASS)
			WTSClientProtocolType = 16  // USHORT (0=コンソール, 1=レガシーRDP, 2=RDP)
		}

		/// <summary>WM_WTSSESSION_CHANGE（セッション接続/切断イベント）</summary>
		private const int WM_WTSSESSION_CHANGE = 0x02B1;

		// WM_WTSSESSION_CHANGE の wParam イベント種別
		private const int WTS_CONSOLE_CONNECT = 0x1;
		private const int WTS_CONSOLE_DISCONNECT = 0x2;
		private const int WTS_REMOTE_CONNECT = 0x3;
		private const int WTS_REMOTE_DISCONNECT = 0x4;
		private const int WTS_SESSION_LOGOFF = 0x6;

		/// <summary>WTSRegisterSessionNotification: 呼び出し元スレッドのセッションのみ通知</summary>
		private const int NOTIFY_FOR_THIS_SESSION = 0;

		private const int SM_CMONITORS = 80;

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int left;
			public int top;
			public int right;
			public int bottom;
		}

		// デリゲートはフィールド/ローカル保持で GC を防ぐ（EnumDisplayMonitors 実行中に収集されるとクラッシュ）
		[return: MarshalAs(UnmanagedType.Bool)]
		private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int nIndex);

		[DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool WTSQuerySessionInformationW(
			IntPtr hServer, int SessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

		[DllImport("wtsapi32.dll")]
		private static extern void WTSFreeMemory(IntPtr pMemory);

		[DllImport("wtsapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

		[DllImport("wtsapi32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

		//================================================================================
		// 状態フィールド
		//================================================================================

		/// <summary>表示状態評価の直列化用（ポーリング/WTS イベント/SendCaptureConfig の同時アクセス対策）</summary>
		private readonly object _displayStateLock = new object();

		private DisplayState _displayState = DisplayState.Normal;
		private string _displayStateReason = "ok";

		private Timer _displayStateTimer;
		private int _displayStatePollBusy; // ポーリング再入防止（Evaluate 中の次ティック実行スキップ）

		private bool _wtsNotifyRegistered;
		private IntPtr _wtsNotifyHwnd = IntPtr.Zero;

		/// <summary>エンジン（キャプチャ JS）の rAF ストール観測値。キャプチャ経路とポーリング経路から更新</summary>
		private volatile bool _engineRafStale;

		/// <summary>render_stall の display_state 配信中フラグ（0=通常, 1=悪化配信中）。遷移時のみ配信</summary>
		private int _renderStallActive;

		/// <summary>復旧手順の多重実行防止（0=アイドル, 1=実行中）</summary>
		private int _recoveryInProgress;

		/// <summary>前回復旧手順の開始時刻（連続発動防止用クールダウン判定）</summary>
		private long _lastRecoveryStartTicks;

		/// <summary>復旧手順のクールダウン。恒久ヘッドレス環境で検知ポーリング毎の復旧発動を防止する</summary>
		private const long RecoveryCooldownTicks = TimeSpan.TicksPerSecond * 30;

		/// <summary>表示状態ポーリング間隔。WTS イベントを補うフォールバックとして十分短い 5 秒</summary>
		private const int DisplayStatePollIntervalMs = 5000;

		//================================================================================
		// 公開 API
		//================================================================================

		/// <summary>現在の表示状態と理由コード（ok / rdp_disconnected / no_monitor / session0 / render_stall）を取得する。</summary>
		public DisplayState GetCurrentDisplayState(out string reason)
		{
			lock (_displayStateLock)
			{
				reason = _displayStateReason;
				return _displayState;
			}
		}

		/// <summary>
		/// WM_WTSSESSION_CHANGE の購読先ウィンドウハンドルを登録する（FormBrowser のハンドルから転送を受ける）。
		/// 購読失敗時はログを残し、ポーリングのみで監視を継続する（機能停止にはしない）。
		/// </summary>
		public void AttachDisplayStateMonitor(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) return;
			lock (_displayStateLock)
			{
				if (_wtsNotifyRegistered) return;
				try
				{
					if (WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION))
					{
						_wtsNotifyRegistered = true;
						_wtsNotifyHwnd = hwnd;
						Log(2, "[RemoteController] WTS セッション通知の購読を開始しました");
					}
					else
					{
						Log(2, $"[RemoteController] WTSRegisterSessionNotification 失敗 (Win32Error={Marshal.GetLastWin32Error()})。ポーリングのみで監視を継続します");
					}
				}
				catch (Exception ex)
				{
					Log(2, $"[RemoteController] WTS セッション通知の購読で例外: {ex.Message}。ポーリングのみで監視を継続します");
				}
			}
		}

		/// <summary>
		/// FormBrowser.WndProc から WM_WTSSESSION_CHANGE を転送する口。
		/// 接続/切断系のイベントのみ即時再評価する（評価は別スレッドで実行し WndProc をブロックしない）。
		/// </summary>
		public void HandleSessionNotification(int eventType)
		{
			switch (eventType)
			{
				case WTS_CONSOLE_CONNECT:
				case WTS_CONSOLE_DISCONNECT:
				case WTS_REMOTE_CONNECT:
				case WTS_REMOTE_DISCONNECT:
				case WTS_SESSION_LOGOFF:
					break;
				default:
					return; // ロック/アンロック等は判定入力にしない（艦これ放置と区別不能なため）
			}

			Log(2, $"[RemoteController] WTS セッションイベント受信 (eventType={eventType})。表示状態を再評価します");
			Task.Run(() =>
			{
				try { EvaluateAndApplyDisplayState("wts_event"); }
				catch (Exception ex) { Log(3, $"[RemoteController] WTS イベント時の表示状態評価で例外: {ex.Message}"); }
			});
		}

		/// <summary>
		/// display_state 制御メッセージを全 open セッションへ配信する。
		/// reason: ok / rdp_disconnected / no_monitor / session0 / render_stall
		/// </summary>
		public Task BroadcastDisplayStateAsync(DisplayState state, string reason)
		{
			string stateName = state == DisplayState.Degraded ? "degraded" : "normal";
			string json = "{\"type\":\"display_state\",\"state\":\"" + stateName + "\",\"reason\":\"" + reason + "\"}";
			return BroadcastControlToAllSessionsAsync(json);
		}

		//================================================================================
		// ライフサイクル
		//================================================================================

		/// <summary>表示状態監視を開始する（Start() から呼ぶ）。起動時に一度評価し、悪化時は即ログへ残す。</summary>
		private void StartDisplayStateMonitoring()
		{
			if (_displayStateTimer != null) return;

			_displayStateTimer = new Timer(_ => DisplayStatePollTick(), null, DisplayStatePollIntervalMs, DisplayStatePollIntervalMs);

			try
			{
				EvaluateAndApplyDisplayState("startup");
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] 起動時の表示状態評価で例外: {ex.Message}");
			}
		}

		/// <summary>表示状態監視を停止する（Dispose() から呼ぶ）。</summary>
		private void StopDisplayStateMonitoring()
		{
			var timer = Interlocked.Exchange(ref _displayStateTimer, null);
			timer?.Dispose();

			lock (_displayStateLock)
			{
				if (_wtsNotifyRegistered)
				{
					try
					{
						WTSUnRegisterSessionNotification(_wtsNotifyHwnd);
					}
					catch (Exception ex)
					{
						Log(2, $"[RemoteController] WTS セッション通知の解除で例外: {ex.Message}");
					}
					_wtsNotifyRegistered = false;
					_wtsNotifyHwnd = IntPtr.Zero;
				}
			}
		}

		//================================================================================
		// 評価ロジック
		//================================================================================

		private void DisplayStatePollTick()
		{
			if (!_isRunning) return;
			// 前回の評価が長い場合（WTS クエリ詰まり）は次ティックをスキップして山積み防止
			if (Interlocked.CompareExchange(ref _displayStatePollBusy, 1, 0) != 0) return;
			Task.Run(async () =>
			{
				try
				{
					// エンジン rAF ストール確認: 中央キャプチャループが不在の
					// WCS オンリー / HTTP オンリー運用でも検知できるようポーリングでも読み取る
					if (HasAnyDisplayConsumer())
					{
						bool? stale = await QueryEngineRafStaleAsync().ConfigureAwait(false);
						if (stale.HasValue) ObserveEngineRafStale(stale.Value);
					}
					EvaluateAndApplyDisplayState("poll");
				}
				catch (Exception ex)
				{
					Log(3, $"[RemoteController] 表示状態のポーリング評価で例外: {ex.Message}");
				}
				finally
				{
					Interlocked.Exchange(ref _displayStatePollBusy, 0);
				}
			});
		}

		//================================================================================
		// エンジン rAF ストール観測（キャプチャ経路の第4要素 + ポーリング読み取りの二本立て）
		//================================================================================

		private bool HasAnyDisplayConsumer()
		{
			return HasWebpConsumers() || HasWcsClientSessions() || IsHttpPollingActive();
		}

		/// <summary>
		/// キャプチャエンジンの rAF ストール観測値を処理する。
		/// ストール検知は「コンシューマ 1 以上」を条件に display_state degraded(render_stall) を即配信する
		/// （ポーリング待ちではなく観測即座に配信）。復帰時は悪化配信の遷移時配信として normal を返す。
		/// </summary>
		private void ObserveEngineRafStale(bool stale)
		{
			_engineRafStale = stale; // volatile フィールドのため直接代入で可視性確保

			if (stale)
			{
				if (!HasAnyDisplayConsumer()) return;
				if (Interlocked.CompareExchange(ref _renderStallActive, 1, 0) == 0)
				{
					Log(2, "[RemoteController] エンジン rAF ストール検知 → display_state degraded(render_stall) を配信します");
					_ = Task.Run(async () =>
					{
						try { await BroadcastDisplayStateAsync(DisplayState.Degraded, "render_stall").ConfigureAwait(false); }
						catch (Exception ex) { Log(3, $"[RemoteController] render_stall 配信エラー: {ex.Message}"); }
					});
					// ストール検知も復旧手順のトリガー（display_state 配信と同時、ポーリング待ちなし）
					_ = TryRecoverRenderingAsync();
				}
			}
			else
			{
				// 復帰は遷移時のみ true→false 遷移が 1 を返す。表示状態自体が悪化中の場合は
				// banner を消さない（表示状態側の復帰遷移で normal が配信される）
				if (Interlocked.Exchange(ref _renderStallActive, 0) == 1)
				{
					Log(2, "[RemoteController] エンジン rAF ストール解除を検知しました");
					string reason;
					if (GetCurrentDisplayState(out reason) == DisplayState.Normal)
					{
						_ = Task.Run(async () =>
						{
							try { await BroadcastDisplayStateAsync(DisplayState.Normal, "ok").ConfigureAwait(false); }
							catch (Exception ex) { Log(3, $"[RemoteController] ストール解除の display_state 配信エラー: {ex.Message}"); }
						});
					}
				}
			}
		}

		/// <summary>
		/// キャプチャエンジンの rafStale フラグを直接読み取る（エンジン未読込・ページ離脱時は null）。
		/// 高頻度呼び出し禁止（呼び出し毎に CefSharp IPC が発生する）。
		/// </summary>
		private async Task<bool?> QueryEngineRafStaleAsync()
		{
			try
			{
				var kancolleFrame = _parent?.GetKanColleFrame();
				if (kancolleFrame == null || !kancolleFrame.IsValid) return null;

				const string script = "(function(){var e=window._kancolleCaptureEngine;if(!e)return null;return e.rafStale===true;})()";
				var response = await kancolleFrame.EvaluateScriptAsync(script).ConfigureAwait(false);
				if (!response.Success || response.Result == null) return null;
				return Convert.ToBoolean(response.Result);
			}
			catch (Exception ex)
			{
				// 読み取り失敗は検知機会 1 回のスキップのみ。握りつぶさず記録する
				Log(3, $"[RemoteController] rAF ストールフラグの読み取り失敗: {ex.Message}");
				return null;
			}
		}

		//================================================================================
		// 描画復旧手順（4 段階）
		//   ① NotifyScreenInfoChanged → ② 本番検証（rAF 再開 + 新フレーム）
		//   → ③ 強制キーフレーム再同期 → ④ display_state normal 配信（検証成立時のみ）
		// 採用不採用: nudge（可視サイズ 1px 往復）・エンジン再注入・ブラウザ再起動は
		// 副作用リスクに対し効果が未検証のため不採用（設計書 §3.4.4）
		//================================================================================

		/// <summary>
		/// 表示状態悪化 / rAF ストール検知時の復旧手順。多重実行防止とクールダウンで
		/// 恒久ヘッドレス環境での連続発動を防ぐ。例外は内部で記録し、呼び出し側を投げない。
		/// </summary>
		public async Task TryRecoverRenderingAsync()
		{
			if (Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) != 0)
			{
				Log(1, "[RemoteController] 復旧手順は実行中のため今回の発動をスキップします");
				return;
			}
			try
			{
				long nowTicks = DateTime.UtcNow.Ticks;
				long last = Interlocked.Read(ref _lastRecoveryStartTicks);
				if (last != 0 && nowTicks - last < RecoveryCooldownTicks)
				{
					Log(1, "[RemoteController] 復旧手順はクールダウン中のためスキップします（連続発動防止）");
					return;
				}
				Interlocked.Exchange(ref _lastRecoveryStartTicks, nowTicks);

				// ① 画面情報再通知: windowed レンダリングでは効果が保証されない（公式ドキュメントは
				// windowless 専用と明記）。成功/例外をログへ残し、効かなければ次段階へ進む二段フォールバックの前提とする
				Log(2, "[RemoteController] 復旧手順①: NotifyScreenInfoChanged による画面情報再通知");
				bool stage1Ok = await NotifyScreenInfoChangedAsync().ConfigureAwait(false);
				Log(2, $"[RemoteController] 復旧手順①: {(stage1Ok ? "API 呼び出し完了" : "API 呼び出し失敗（次段階へ継続）")}");

				// ② 本番検証: rAF 再開 + 新フレーム（encodeCount 増加）を 5 秒猶予で確認
				bool verified = await WaitForEngineResumeAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
				Log(2, $"[RemoteController] 復旧手順②: 本番検証 {(verified ? "成立" : "未成立（③へ継続）")}");

				// ③ 強制キーフレーム再同期: WebP は次回 pull を force に、
				// WCS は SPS/PPS（description）再送フラグを全セッションで折り返す
				if (!verified)
				{
					_forceNextKeyFrame = true;
					int wcsResetCount = 0;
					foreach (var session in _webSocketSessions.Values)
					{
						if (session.ClientMode != 1) continue;
						session.DescriptionSent = false;
						session.ResetDescriptionSendState();
						wcsResetCount++;
					}
					Log(2, $"[RemoteController] 復旧手順③: 強制キーフレーム再同期（WebP force + WCS description 再送 {wcsResetCount} セッション）");
					verified = await WaitForEngineResumeAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
				}

				// ④ 復帰配信: 本番検証が成立した場合のみ normal を返し banner を解除する。
				// 未成立時は degraded を維持する（banner が実態と食い違うのを防ぐ）
				if (verified)
				{
					Log(2, "[RemoteController] 復旧手順④: 本番復帰を確認しました");
					if (Interlocked.Exchange(ref _renderStallActive, 0) == 1)
					{
						string reason;
						if (GetCurrentDisplayState(out reason) == DisplayState.Normal)
						{
							await BroadcastDisplayStateAsync(DisplayState.Normal, "ok").ConfigureAwait(false);
						}
					}
				}
				else
				{
					Log(2, "[RemoteController] 復旧手順④: 本番復帰は確認できず — degraded 表示を維持します（次回トリガー待ち）");
				}
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] 描画復旧手順中の例外: {ex.Message}");
			}
			finally
			{
				Interlocked.Exchange(ref _recoveryInProgress, 0);
			}
		}

		/// <summary>
		/// UI スレッドで NotifyScreenInfoChanged を実行する。UI スレッド閉塞時に復旧手順全体が
		/// ハングしないよう 3 秒でタイムアウト扱いとする（BeginInvoke でブロッキングしない）。
		/// </summary>
		private async Task<bool> NotifyScreenInfoChangedAsync()
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			try
			{
				var browser = _parent?.Browser;
				if (browser == null || browser.IsDisposed)
				{
					Log(2, "[RemoteController] 復旧手順①: ブラウザ参照が取得できずスキップします");
					return false;
				}

				browser.BeginInvoke(new Action(() =>
				{
					try
					{
						var host = browser.GetBrowser()?.GetHost();
						if (host == null)
						{
							tcs.TrySetResult(false);
							return;
						}
						host.NotifyScreenInfoChanged();
						tcs.TrySetResult(true);
					}
					catch (Exception ex)
					{
						Log(3, $"[RemoteController] 復旧手順① NotifyScreenInfoChanged の実行で例外: {ex.Message}");
						tcs.TrySetResult(false);
					}
				}));
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] 復旧手順① の UI スレッドディスパッチに失敗: {ex.Message}");
				return false;
			}

			var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000)).ConfigureAwait(false);
			if (completed != tcs.Task)
			{
				Log(2, "[RemoteController] 復旧手順①: UI スレッド応答が 3 秒以内に戻らず（無効扱い）");
				return false;
			}
			return tcs.Task.Result;
		}

		/// <summary>
		/// 本番検証: rAF ストール解除（rafStale=false）かつ新フレーム（encodeCount 由来の
		/// _contentVersion 増加）を 1 秒間隔で確認する。猶予超過は false（呼び出し側は次段階へ）。
		/// 画面静止時は encodeCount が増加しないため false になりうるが、その場合は
		/// ストール解除観測側（ObserveEngineRafStale）で normal 復帰が配信される。
		/// </summary>
		private async Task<bool> WaitForEngineResumeAsync(TimeSpan grace)
		{
			long versionBefore = Volatile.Read(ref _contentVersion);
			DateTime deadline = DateTime.UtcNow.Add(grace);

			while (DateTime.UtcNow < deadline)
			{
				await Task.Delay(1000).ConfigureAwait(false);

				bool? stale = await QueryEngineRafStaleAsync().ConfigureAwait(false);
				if (stale != false) continue; // ストール継続 / エンジン読込不可は再開未確認

				if (Volatile.Read(ref _contentVersion) > versionBefore) return true;
			}
			return false;
		}

		/// <summary>
		/// 表示状態を評価し、前回から変化があれば全セッションへ display_state を配信する。
		/// WTS クエリ失敗時は状態を維持する（一時的なクエリ失敗で誤った悪化通知を流さない）。
		/// </summary>
		private void EvaluateAndApplyDisplayState(string trigger)
		{
			DisplayState newState;
			string newReason;
			if (!TryEvaluateDisplayState(out newState, out newReason)) return;

			bool changed;
			lock (_displayStateLock)
			{
				changed = (newState != _displayState) || (newReason != _displayStateReason);
				if (changed)
				{
					Log(2, $"[RemoteController] 表示状態変化 ({trigger}): {_displayState}/{_displayStateReason} → {newState}/{newReason}");
					_displayState = newState;
					_displayStateReason = newReason;
				}
			}

			if (!changed) return;

			// 配信は fire-and-forget（評価スレッドをブロックしない）。BroadcastControlToAllSessionsAsync は
			// セッション単位で catch するため、ここで観測するのは JSON 構築レベルの例外のみ
			_ = Task.Run(async () =>
			{
				try { await BroadcastDisplayStateAsync(newState, newReason).ConfigureAwait(false); }
				catch (Exception ex) { Log(3, $"[RemoteController] display_state 配信エラー: {ex.Message}"); }
			});

			// 悪化への遷移時は配信待ちせず復旧手順をただちに発動する。
			// session0 は運用ミス（サポート外構成）であり復旧措置が成立しないため発動しない
			if (newState == DisplayState.Degraded && newReason != "session0")
			{
				_ = TryRecoverRenderingAsync();
			}
		}

		/// <summary>
		/// 現在のセッション構成から表示状態を判定する。
		/// 戻り値 false は判定不能（WTS クエリ失敗）で、呼び出し側は状態を維持する。
		/// </summary>
		private bool TryEvaluateDisplayState(out DisplayState state, out string reason)
		{
			state = DisplayState.Normal;
			reason = "ok";

			int sessionId = 0;
			try { sessionId = Process.GetCurrentProcess().SessionId; }
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] プロセスセッション ID の取得に失敗: {ex.Message}");
				return false;
			}

			// サービス（セッション 0）実行: サポート外。情報提供のみ行い復旧措置は発動しない
			if (sessionId == 0)
			{
				state = DisplayState.Degraded;
				reason = "session0";
				return true;
			}

			int connectState;
			if (!TryQuerySessionInt32(sessionId, WTS_INFO_CLASS.WTSConnectState, out connectState))
				return false;

			if (connectState == WTSDisconnected)
			{
				// RDP セッション切断 = デスクトップ描画が停止しうる決定状態
				state = DisplayState.Degraded;
				reason = "rdp_disconnected";
				return true;
			}

			if (connectState == WTSActive || connectState == WTSConnected)
			{
				// RDP 由来のアクティブセッションは仮想ディスプレイを持つため正常扱い
				short protocolType;
				if (TryQuerySessionInt16(sessionId, WTS_INFO_CLASS.WTSClientProtocolType, out protocolType) && protocolType != 0)
				{
					state = DisplayState.Normal;
					reason = "ok";
					return true;
				}

				// コンソールセッション: モニタ 0 本（ダミープラグ未挿し等）は描画が停止しうる
				if (CountConnectedMonitors() == 0)
				{
					state = DisplayState.Degraded;
					reason = "no_monitor";
					return true;
				}

				state = DisplayState.Normal;
				reason = "ok";
				return true;
			}

			// Idle/Down/Init 等の過渡状態: 判定を保留し現状維持（誤検知による誤 banner 防止）
			return false;
		}

		//================================================================================
		// Win32 クエリヘルパー
		//================================================================================

		/// <summary>
		/// EnumDisplayMonitors と SM_CMONITORS の両方でモニタ数を数え、小さい方を返す。
		/// どちらかが 0 を返した時点で「モニタ無し」とみなす（API 間で結果が分かれる環境対策）。
		/// </summary>
		private static int CountConnectedMonitors()
		{
			int count = 0;
			MonitorEnumDelegate callback = delegate (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
			{
				count++;
				return true;
			};
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
			GC.KeepAlive(callback); // コールバック実行完了までの GC 防止

			int sysCount = GetSystemMetrics(SM_CMONITORS);
			return Math.Min(count, sysCount);
		}

		private bool TryQuerySessionInt32(int sessionId, WTS_INFO_CLASS infoClass, out int value)
		{
			value = 0;
			IntPtr buffer = IntPtr.Zero;
			uint bytesReturned;
			try
			{
				if (!WTSQuerySessionInformationW(IntPtr.Zero /* WTS_CURRENT_SERVER_HANDLE */, sessionId, infoClass, out buffer, out bytesReturned))
					return false;
				if (buffer == IntPtr.Zero || bytesReturned < 4) return false;
				value = Marshal.ReadInt32(buffer);
				return true;
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WTS セッション情報 (Int32) の取得で例外: {ex.Message}");
				return false;
			}
			finally
			{
				// 例外経路でも必ず解放（リーク防止）
				if (buffer != IntPtr.Zero)
				{
					try { WTSFreeMemory(buffer); }
					catch (Exception freeEx) { Log(3, $"[RemoteController] WTS バッファ解放で例外: {freeEx.Message}"); }
				}
			}
		}

		private bool TryQuerySessionInt16(int sessionId, WTS_INFO_CLASS infoClass, out short value)
		{
			value = 0;
			IntPtr buffer = IntPtr.Zero;
			uint bytesReturned;
			try
			{
				if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out buffer, out bytesReturned))
					return false;
				if (buffer == IntPtr.Zero || bytesReturned < 2) return false;
				value = Marshal.ReadInt16(buffer);
				return true;
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] WTS セッション情報 (Int16) の取得で例外: {ex.Message}");
				return false;
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					try { WTSFreeMemory(buffer); }
					catch (Exception freeEx) { Log(3, $"[RemoteController] WTS バッファ解放で例外: {freeEx.Message}"); }
				}
			}
		}
	}
}

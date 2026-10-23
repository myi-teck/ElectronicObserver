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
			try
			{
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

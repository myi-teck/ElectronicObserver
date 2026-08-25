//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.HttpApi.cs
// summary        : control / eventlog / hud endpoints, static file serving, mouse control
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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
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

		/// <summary>
		/// HTTPセッションのカーソル状態を保持するクラス
		/// WebSocketセッションとは独立して管理
		/// </summary>
		private class HttpCursorState
			{
				private long _xBits;
				private long _yBits;
				private int _hasValue; // 0: false, 1: true (Interlocked 管理)
	
				// LastAccessTime を long ticks（UTC）として Interlocked/Volatile 管理
				// DateTime 構造体のプレーン書き込みは 32bit 環境で Torn Read  가능성이あるため、
				// 64bit 値の原子性を保証する Interlocked.Exchange / Volatile.Read に統一
				private long _lastAccessTicks = DateTime.UtcNow.Ticks;
	
				// HasValue プロパティ（アトミック保護維持）
				public bool HasValue => Volatile.Read(ref _hasValue) == 1;
	
				public double X => BitConverter.Int64BitsToDouble(Volatile.Read(ref _xBits));
				public double Y => BitConverter.Int64BitsToDouble(Volatile.Read(ref _yBits));
	
				/// <summary>
				/// 最終アクセス時刻（エバクションポリシーで使用）
				/// Volatile.Read によるアトミック読み取り
				/// </summary>
				public DateTime LastAccessTime => new DateTime(Volatile.Read(ref _lastAccessTicks), DateTimeKind.Utc);
	
				// 最終アクセス時刻をアトミックに更新（内部共通ヘルパ）
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
				/// x, y をセットし HasValue を true にする
				/// </summary>
				public void Touch(double x, double y)
				{
					Interlocked.Exchange(ref _xBits, BitConverter.DoubleToInt64Bits(x));
					Interlocked.Exchange(ref _yBits, BitConverter.DoubleToInt64Bits(y));
					Interlocked.Exchange(ref _hasValue, 1);
					UpdateLastAccessTicks();
				}
	
				/// <summary>
				/// LastAccessTime のみを更新（タイムアウト状態をリセット）
				/// 命名は Touch(double,double) との紛らわしさを避けるため UpdateLastAccess とした
				/// Interlocked によるアトミック更新に変更
				/// </summary>
				public void UpdateLastAccess()
				{
					UpdateLastAccessTicks();
				}
		}

		/// <summary>
		/// マウス操作を受け取ってエミュレートします。
		/// </summary>
		private async Task HandleControlAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			try
			{
				// （セッション分離対応）
				// HTTPモードでもセッションIDベースのカーソル状態を使用
				CleanupExpiredHttpCursorSessionsIfNeeded();

				// セッションIDを取得（Cookieから）
				string sessionId = GetCookieValue(req, "session_id");
				if (string.IsNullOrEmpty(sessionId))
				{
					sessionId = Guid.NewGuid().ToString("N");
				}

				var cursorState = GetOrCreateHttpCursorState(sessionId);
				cursorState.UpdateLastAccess();
				var action = req.QueryString["action"];
				var xStr = req.QueryString["x"];
				var yStr = req.QueryString["y"];

				// 絶対割合座標 (0.0〜1.0) を受け取り、現在のポインタ座標に適用します。
				if (action == "move" && double.TryParse(xStr, out double x) && double.TryParse(yStr, out double y))
				{
					// Touch(x,y) で座標更新と HasValue=true をアトミックに設定
					// 旧実装は SetX/SetY のみで HasValue が永遠に false のため、
					// TriggerMouseClickAsync のフォールバックで (0.5, 0.5) が常に優先されていた
					cursorState.Touch(Math.Max(0, Math.Min(1.0, x)), Math.Max(0, Math.Min(1.0, y)));

					await TriggerMouseMoveAsync(cursorState);
					// サーバ側アクション実行ログ（HTTP 経路）
					AppendServerActionLog("move", x, y, sessionId, "http");
				}
				else if (action == "click")
				{
					// 座標パース失敗時は null を記録
					double? cx = null;
					double? cy = null;
					if (double.TryParse(xStr, out double cxx) && double.TryParse(yStr, out double cyy))
					{
						cx = cxx;
						cy = cyy;
						cursorState.Touch(Math.Max(0, Math.Min(1.0, cxx)), Math.Max(0, Math.Min(1.0, cyy)));
					}
					await TriggerMouseClickAsync(cursorState);
					// サーバ側アクション実行ログ（HTTP 経路）
					AppendServerActionLog("click", cx, cy, sessionId, "http");
				}
				// （ゲーム画面タッチクリック）
				// ゲーム画面（viewport）をタップした位置の絶対座標を直接クリックする
				else if (action == "touch_click" && double.TryParse(xStr, out double tx) && double.TryParse(yStr, out double ty))
				{
					// Touch(x,y) で座標更新と HasValue=true をアトミックに設定（move と同様）
					cursorState.Touch(Math.Max(0, Math.Min(1.0, tx)), Math.Max(0, Math.Min(1.0, ty)));
					await TriggerMouseClickAsync(cursorState);
					// サーバ側アクション実行ログ（HTTP 経路）
					AppendServerActionLog("touch_click", tx, ty, sessionId, "http");
				}
					#region "追加アクション処理"
					// ブラウザをログインページへ移動（確認はHUD側で行うため、ここでは即座に実行）
					else if (action == "navigate_login")
					{
						_parent.NavigateToLoginPage();
					}
					// （ログアウト: 本体ブラウザを空白ページへ移動）
					// Cookie は削除せず（DMMセッション維持）、空白ページ上のボタンから
					// ログインページへ戻れば自動ログインとなる。
					else if (action == "logout_blank")
					{
						_parent.LogoutToBlankPage();
					}
					// プライバシーフィルターのカラーモードを設定（0=なし, 1=グレースケール, 4=低コントラスト）
					// 差分WebP方式に伴い、フィルターはRemoteControllerクライアント側でCSS適用するため
					//                  サーバー側ではNOP（何もしない）に変更。本家ブラウザ側には影響を与えない。
					else if (action == "set_filter")
					{
						// クライアントサイドフィルターのため、サーバーサイドでは何もしない（NOP）
						// 将来的に状態同期が必要な場合はここを拡張可能
					}
					// WebP圧縮品質の動的設定アクションを追加
					// （品質スライダーとのWebPリアルタイム連動対応）
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
						// （ドラッグ操作対応）
						// （セッション分離対応：async Task化）
						// マウスダウン（ドラッグ開始）
						else if (action == "mousedown")
						{
							// 座標パース失敗時は null を記録
							double? mx = null;
							double? my = null;
							if (double.TryParse(xStr, out double mxx) && double.TryParse(yStr, out double myy))
							{
								mx = mxx;
								my = myy;
								cursorState.Touch(Math.Max(0, Math.Min(1.0, mxx)), Math.Max(0, Math.Min(1.0, myy)));
							}
							await TriggerMouseDownAsync(cursorState);
							// サーバ側アクション実行ログ（HTTP 経路）
							AppendServerActionLog("mousedown", mx, my, sessionId, "http");
						}
						// マウスアップ（ドラッグ終了）
						else if (action == "mouseup")
						{
							// 座標パース失敗時は null を記録
							double? ux = null;
							double? uy = null;
							if (double.TryParse(xStr, out double uxx) && double.TryParse(yStr, out double uyy))
							{
								ux = uxx;
								uy = uyy;
								cursorState.Touch(Math.Max(0, Math.Min(1.0, uxx)), Math.Max(0, Math.Min(1.0, uyy)));
							}
							await TriggerMouseUpAsync(cursorState);
							// サーバ側アクション実行ログ（HTTP 経路）
							AppendServerActionLog("mouseup", ux, uy, sessionId, "http");
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
		// クライアント中継イベントログ API（POST /event-log）・Q9 ON/OFF API（POST /log-toggle）
		// 設計書: plans/task14_remote-doubleclick-fix-and-event-logging.md §3.5 / §3.5c / §3.8
		//------------------------------------------------------------------------------------
		/// <summary>
		/// POST /event-log: クライアント中継イベントログを受信し専用ファイル（remote_event_log_YYYYMMDD.log）へ追記。
		/// 認証済み領域（IsAuthenticated 通過後）でルーティングされる（/control と同一）。
		/// </summary>
		private async Task HandleEventLogAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			try
			{
				if (req.HttpMethod != "POST")
				{
					res.StatusCode = 405;
					res.Close();
					return;
				}
				// body サイズ上限（64KB）: 読み捨てず、先頭 64KB を読取して保存（トリミング）。
				// ContentLength64 が <= 0（chunked transfer・サイズ未知）の場合、
				// ReadToEndAsync による全量読取は巨大 payload のメモリ逼迫を招くため、上限（65537 文字）までの有界読取を行う。
				long contentLength = req.ContentLength64;
				string body;
				using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
				{
					if (contentLength > 0 && contentLength <= 65536)
					{
						// サイズ既知・上限内: 全量読取
						body = await reader.ReadToEndAsync();
					}
					else
					{
						// サイズ未知（chunked）または上限超過: 有界読取（上限 65537 文字＝上限 65536 超過判定用の +1）。
						// StreamReader.ReadAsync(char[], int, int) は
						// Task<int> を返す（読取文字数）ため、単一呼び出しで全量取得できる保証はない。
						// 上限までループ読取し、read <= 0（EOF）で終える。
						var buffer = new char[65537];
						int total = 0;
						while (total < buffer.Length)
						{
							int read = await reader.ReadAsync(buffer, total, buffer.Length - total);
							if (read <= 0) break;
							total += read;
						}
						body = new string(buffer, 0, total);
					}
				}
				bool truncated = false;
				// ContentLength64 が <= 0（chunked）の場合、
				// 実読取サイズ（body.Length・文字数）で補正する（従来は originalBytes=0 になる矛盾を解消）。
				// 注: chunked 時は originalBytes がバイト数ではなく文字数の近似値になる（設計書 §3.5c-4）。
				long originalBytes = (contentLength > 0) ? contentLength : (long)body.Length;
				if (body.Length > 65536)
				{
					// body が 64KB 超過 → 先頭 64KB を切り取って保存（読み捨てしない）
					// 切り取り位置はサロゲートペア境界に合わせ直す（§3.5c-3・SafeTruncate）
					body = SafeTruncate(body, 65536);
					truncated = true;
				}
				var line = body.Trim();
				// JSON オブジェクト形状でない（{ で始まらない）: 無効 payload → 400（拒否）
				if (!line.StartsWith("{"))
				{
					Log(3, $"[RemoteController] POST /event-log 無効な payload を拒否（{{ で始まらない）: len={line.Length}");
					res.StatusCode = 400;
					res.Close();
					return;
				}
				// 1 行 4096 文字超過 → 先頭 4096 文字に切り取って保存（truncated=true）
				// 切り取り位置はサロゲートペア境界に合わせ直す（§3.5c-3・SafeTruncate）。
				// 「4096」は文字数（UTF-16 コードユニット）でありバイト数ではない。多バイト UTF-8 文字を含む場合、
				// ディスク上のバイト数は大きくなるが、JSONL の「1 行の長さ」は 4096 文字上限で維持する（設計意図: 行長の安定）。
				if (line.Length > 4096)
				{
					line = SafeTruncate(line, 4096);
					truncated = true;
				}
				// トリミングにより JSON が壊れた場合: parse_error=true を付加して生テキストを保存
				bool parseError = !line.EndsWith("}");
				string savedLine = line;
				if (parseError)
				{
					// JSON が壊れているため、切り取った生テキストをエンベロープ JSON として保存。
					// 行は JSONL 1 行・先頭 {・末尾 } を維持し、調査者が parse_error を判別可能にする。
					savedLine = "{\"parse_error\":true,\"truncated\":" + truncated.ToString().ToLower() +
								",\"originalBytes\":" + originalBytes +
								",\"raw\":\"" + EscapeJsonString(line, 3900) + "\"}";
					Log(3, $"[RemoteController] POST /event-log トリミングで JSON 破損: len={line.Length}, originalBytes={originalBytes}（parse_error=true として保存）");
				}
				RemoteDedicatedLog.Event.Append(savedLine);
				res.StatusCode = 200;
				// 内容が保存できたなら 200（truncated 情報を body に返す・クライアントを失敗させない）
				var respBody = truncated
					? "{\"ok\":true,\"truncated\":true,\"originalBytes\":" + originalBytes + "}"
					: "{\"ok\":true}";
				var buf = System.Text.Encoding.UTF8.GetBytes(respBody);
				await res.OutputStream.WriteAsync(buf, 0, buf.Length);
				res.Close();
			}
			catch (Exception ex)
			{
				// AGENTS.md NEVER 対応: 例外を握りつぶさず記録・500 返却。
				// 設計書コード例の finally { try { res.Close(); } catch { } } の空 catch を解消するため、
				// HandleControlAsync と同一の「catch でログ記録＋finally で res.Close()」パターンに構造化。
				Log(3, $"[RemoteController] POST /event-log error: {ex.GetType().Name}: {ex.Message}");
				try { res.StatusCode = 500; } catch (Exception ex2) { Log(3, $"[RemoteController] POST /event-log status 設定失敗: {ex2.Message}"); }
			}
			finally
			{
				res.Close();
			}
		}

		/// <summary>
		/// POST /log-toggle: サーバ側ログ記録（イベントログ）の ON/OFF を即時切り替え。
		/// 認証済み領域（IsAuthenticated 通過後）でルーティングされる（/control・/event-log と同一）。
		/// body: {"enabled": true|false}（それ以外 400）。応答: {"ok":true,"enabled":<現行値>}。
		/// </summary>
		private async Task HandleLogToggleAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			try
			{
				if (req.HttpMethod != "POST")
				{
					res.StatusCode = 405;
					res.Close();
					return;
				}
				string body;
				using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
				{
					body = await reader.ReadToEndAsync();
				}
				// enabled の抽出: 既存ヘルパ ExtractJsonString を流用（未クォートの true/false も抽出可能）。
				// 設計書コード例の「enabled 未定義」欠陥を解消:
				// キー欠落時は ExtractJsonString が null を返し、bool.TryParse が false を返すため 400 で拒否する。
				string enabledStr = ExtractJsonString(body ?? "", "enabled");
				bool enabled;
				if (!bool.TryParse(enabledStr, out enabled))
				{
					Log(3, $"[RemoteController] POST /log-toggle 無効な body（enabled が true/false 以外）: body={body ?? "(null)"}");
					res.StatusCode = 400;
					res.Close();
					return;
				}
				// 即時反映: メモリ内フラグ変更 ＋ 切り替え操作自体をログ記録（SetEnabled 内）
				RemoteDedicatedLog.SetEnabled(enabled, "log-toggle endpoint");
				res.StatusCode = 200;
				var buf = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true,\"enabled\":" + enabled.ToString().ToLower() + "}");
				await res.OutputStream.WriteAsync(buf, 0, buf.Length);
				res.Close();
			}
			catch (Exception ex)
			{
				// AGENTS.md NEVER 対応: 例外を握りつぶさず記録・500 返却（HandleControlAsync と同一パターン）。
				Log(3, $"[RemoteController] POST /log-toggle error: {ex.GetType().Name}: {ex.Message}");
				try { res.StatusCode = 500; } catch (Exception ex2) { Log(3, $"[RemoteController] POST /log-toggle status 設定失敗: {ex2.Message}"); }
			}
			finally
			{
				res.Close();
			}
		}

		/// <summary>
		/// JSON 文字列内の特殊文字をエスケープ（標準機能のみ・NuGet 不要）。
		/// 生テキストを JSONL 1 行の "raw" 値として安全に格納するためのヘルパ（§3.5c-3）。
		/// （一次 Major-1・二次 Major-2 反映）:
		/// - 切り取り位置は SafeTruncate でサロゲートペア境界に合わせ、ペアを途中で分断しない。
		/// - ソースに存在した孤立サロゲート（unpaired surrogate）は U+FFFD（置換文字）に置換し、
		///   不正な UTF-16／UTF-8 を生成して日本語文字化けするのを防止する。
		/// - System.Text.Encodings.Web（JavaScriptEncoder）は .NET Framework 4.8 でも package-provided
		///   （NuGet 参照必須）であり、新規 NuGet 禁止制約（AGENTS.md）に抵触するため不採用（§9 参照）。
		/// </summary>
		private static string EscapeJsonString(string s, int maxLen)
		{
			if (s == null) return "";
			s = SafeTruncate(s, maxLen); // raw も最大長で切り詰め（行長維持・サロゲートペア境界に一致）
			var sb = new StringBuilder(s.Length + 16);
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
						else if (char.IsSurrogate(c))
						{
							// 孤立サロゲート（ペアを構成しない）は U+FFFD に置換。
							// 正当なペア（上位＋直後の下位、または直前の上位＋下位）はそのまま保持し、
							// エンコード結果が有効な UTF-16／UTF-8 シーケンスになることを保証する。
							bool paired = (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
									  || (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]));
							if (paired) { sb.Append(c); }
							else { sb.Append("\\ufffd"); }
						}
						else sb.Append(c);
						break;
				}
			}
			return sb.ToString();
		}

		/// <summary>
		/// 文字列を指定文字数で切り詰め（サロゲートペア境界に一致）。
		/// 切り取り位置がサロゲートペア（上位サロゲート＋下位サロゲート）の中間に落ちる場合は
		/// ペア頭（上位サロゲート）の前へ巻き戻すことで、ペアを途中で分断して孤立サロゲート
		/// （不正な UTF-16／UTF-8・日本語文字化け）を生成しない（§3.5c-3）。
		/// </summary>
		private static string SafeTruncate(string s, int maxLen)
		{
			if (s == null) return "";
			if (s.Length <= maxLen) return s;
			int end = maxLen;
			// s.Length > maxLen（上記で検証済み）なので s[end] は必ず存在する（境界チェック不要）。
			if (char.IsHighSurrogate(s[end - 1]) && char.IsLowSurrogate(s[end]))
			{
				end -= 1; // ペア中間切り取り → ペア頭の前へ巻き戻し（上位サロゲートを切り捨て）
			}
			return s.Substring(0, end);
		}

		/// <summary>
		/// サーバ側アクション実行ログ（src=server）。
		/// TriggerMouse*Async を実際に実行したタイミングで呼ばれ、クライアント送信ログと
		/// 同じ専用ファイル（remote_event_log_YYYYMMDD.log）に時系列で並ぶ。
		/// 二重発火の再発調査（クライアント2回送信？サーバ2回実行？）に用いる（§12 参照）。
		/// </summary>
		private void AppendServerActionLog(string action, double? x, double? y, string sessionId, string route)
		{
			string xVal = x.HasValue ? x.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null";
			string yVal = y.HasValue ? y.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null";
			RemoteDedicatedLog.Event.Append(
				$@"{{""ts_ms"":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},""ts_local"":""{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}"",""src"":""server"",""kind"":""action"",""action"":""{action}"",""x"":{xVal},""y"":{yVal},""session"":""{sessionId}"",""route"":""{route}""}}");
		}

		//====================================================================================
		// 処理ブロック名 : GetBrowserCoords
		// 概要           : モバイル端末から受信した 0.0〜1.0 のゲーム画面上の相対座標を、
		//                  ブラウザコントロールの現在のサイズと艦これアスペクト比(5:3)から生じる
		//                  黒い余白（レターボックス）を動的に考慮し、実際のCefブラウザ論理ピクセル座標へ変換します。
		// セッション固有のカーソル座標を使用するオーバーロードを追加
		//------------------------------------------------------------------------------------
		private Point? GetBrowserCoords(double cursorX, double cursorY)
		{
			// CefSharpブラウザコントロール全体の現在の論理サイズを取得
			double browserWidth = _parent.Browser.Width;
			double browserHeight = _parent.Browser.Height;

			// ブラウザサイズが 0/極小の場合のガード
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
		// async Task化、セッション固有カーソル対応
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
		// async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private async Task TriggerMouseClickAsync(HttpCursorState cursorState)
			{
				if (_parent.Browser == null) return;
	
				var browser = _parent.Browser.GetBrowser();
				var host = browser?.GetHost();
				if (host == null) return;
	
				// HasValue フラグによるフォールバック
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
			
			// 0. マウス移動（ホバー・フォーカス確立を先行）
			host.SendMouseMoveEvent(mouseEvent, false);

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
		// async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private Task TriggerMouseDownAsync(HttpCursorState cursorState)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			double cursorX = cursorState.HasValue ? cursorState.X : 0.5;
			double cursorY = cursorState.HasValue ? cursorState.Y : 0.5;

			var coords = GetBrowserCoords(cursorX, cursorY);
			if (coords == null) return Task.CompletedTask;
			// CefEventFlags.None を使用（公式推奨）
			// isUp=false でダウンイベントを指定
			var mouseEvent = new MouseEvent(coords.Value.X, coords.Value.Y, CefEventFlags.None);
			// 0. マウス移動（ホバー・フォーカス確立を先行）
			host.SendMouseMoveEvent(mouseEvent, false);
			// 1. マウスダウン
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			Console.WriteLine($"RemoteController: TriggerMouseDown @ {coords.Value.X},{coords.Value.Y}");
			return Task.CompletedTask;
		}

		//====================================================================================
		// 処理ブロック名 : TriggerMouseUpAsync
		// 概要           : セッション固有のカーソル座標で左ボタンアップを送信
		// async Task化、セッション固有カーソル対応
		//------------------------------------------------------------------------------------
		private Task TriggerMouseUpAsync(HttpCursorState cursorState)
		{
			if (_parent.Browser == null) return Task.CompletedTask;

			var browser = _parent.Browser.GetBrowser();
			var host = browser?.GetHost();
			if (host == null) return Task.CompletedTask;

			double cursorX = cursorState.HasValue ? cursorState.X : 0.5;
			double cursorY = cursorState.HasValue ? cursorState.Y : 0.5;

			var coords = GetBrowserCoords(cursorX, cursorY);
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

			// 0. マウス移動（ホバー・フォーカス確立を先行）
			host.SendMouseMoveEvent(mouseEvent, false);
			// 1. マウスダウン
			host.SendMouseClickEvent(mouseEvent, MouseButtonType.Left, false, 1);
			await Task.Delay(50, session.CancellationToken.Token);
			// 2. マウスアップ
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
			// 0. マウス移動（ホバー・フォーカス確立を先行）
			host.SendMouseMoveEvent(mouseEvent, false);
			// 1. マウスダウン
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

			// 1. 開発時の利便性向上のため、プロジェクトフォルダ直下のソースファイルを探索します。
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

		// （set_quality アクション用スクリプトの事前構築）
		// 動的な品質値のみを連結し、固定部分は定数として事前定義する。
		private const string QualityScriptPrefix = "if(window._kancolleCaptureEngine)window._kancolleCaptureEngine.webpQuality=";
		private const char QualityScriptSuffix = ';';
	}
}

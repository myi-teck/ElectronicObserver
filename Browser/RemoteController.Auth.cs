//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.Auth.cs
// summary        : Authentication, login, cookie, IP block
//----------------------------------------------------------------------------
// version        : 1.00
// Copyright 2026 myi
//============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
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

			// （Grace Period: 並行リクエストによるRace Condition対策）
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

			// （Grace Period内なら有効とみなす）
			public bool IsValid()
			{
				return !IsExpired() && (RevokedAt == null || DateTime.UtcNow - RevokedAt.Value < TimeSpan.FromSeconds(10));
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

					// （無効トークン検知時のCookie消去）
					// ブラウザの無効Cookieを消去して、次回以降の同じ401エラーの繰り返しを防ぐ
					ClearCookie(res, "session_id");
					ClearCookie(res, "refresh_token");
				}
				else
				{
					// （ストアにトークンが存在しない場合のCookie消去）
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
				// （Grace Periodトークンのクリーンアップ実装）
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

		// JSON boolean リテラルの定数化・ヘルパー化:
		// C# の bool.ToString() は仕様上大文字 "True"/"False" を返すため、そのまま文字列連結すると
		// 不正 JSON となる（JS の JSON.parse が SyntaxError）。
		// 定数 + ヘルパーに集約し、大文字回帰を構造的に防止する（JSON boolean 出力はすべてこの経路で統一）。
		private const string JsonTrue = "true";
		private const string JsonFalse = "false";

		/// <summary>
		/// 値に対応する JSON boolean リテラル（小文字 true/false、JSON 仕様に準拠）を返す。
		/// </summary>
		private static string WriteJsonBool(bool value)
		{
			return value ? JsonTrue : JsonFalse;
		}

		/// <summary>
		/// ログイン状態（= ログアウト空白ページ状態）を JSON で返却する。
		/// リモートクライアントがポーリングし、loggedOut == true で /logout-page へリダイレクトする。
		/// </summary>
		private void SendLoginState(HttpListenerResponse res)
		{
			bool loggedOut = _parent.IsLogoutBlank;   // volatile 読み込み（HTTP スレッドから安全）
			bool dummyVideo = _parent.IsDummyVideo;   // volatile 読み込み（同上・ダミー映像状態）
			// JSON を文字列連結で組み立てる意図:
			// 固定キーの JSON 3 つ（loggedOut: bool / dummyVideo: bool / ts: long）のみのシンプルなレスポンスであり、
			// System.Text.Json の導入は新規依存を伴うため最小差分原則で文字列連結を維持する。
			// bool は true/false として、DateTime.Now.Ticks（long）は暗黙の ToString で JSON 数値として
			// 出力される（JS の JSON.parse で boolean / number として正しく解析可能）。
			// C# の bool.ToString() は仕様上大文字 "True"/"False" を返すため、
			// そのまま連結すると不正 JSON となる（JS 側 res.json()=JSON.parse が SyntaxError を投げ、
			// remote 画面の /logout-page 遷移・/ 復帰が永発しなくなる）。
			// JSON 仕様に従い小文字 true/false に明示する。
			// boolean 出力は WriteJsonBool() に統一（大文字回帰の構造防止）。
			// TODO: 本メソッドの文字列連結による JSON 組立は
			// 最小差分原則（固定キー 3 つ・エスケープ不要値のみ）での暫定対応。bool/long 以外の値
			// （double・文字列等）が追加される場合、不正 JSON になるリスクがあるため、その際は
			// 標準シリアライザ（System.Web.Script.Serialization.JavaScriptSerializer 等のラッパ）へ移行する。
			string json = "{\"loggedOut\":" + WriteJsonBool(loggedOut)
				+ ",\"dummyVideo\":" + WriteJsonBool(dummyVideo)
				+ ",\"ts\":" + DateTime.Now.Ticks + "}";
			// キャッシュ制御: ブラウザ/プロキシがレスポンスをキャッシュしないよう付与
			// （5 秒間隔のポーリング対象のため、loggedOut 状態変化の検出遅延がポーリング間隔を超えないことを保証）
			res.Headers["Cache-Control"] = "no-cache, must-revalidate";
			SendJsonResponse(res, json);
		}

		/// <summary>
		/// ログアウト中画面（logout.html の remote モード）を配信する。
		/// ファイル探索は SelfHostedHtml（login.html と同一ロジック）。
		/// ファイル不在時はインラインフォールバック HTML を配信する（サイレントスルーしない）。
		/// </summary>
		private void SendLogoutPage(HttpListenerResponse res)
		{
			string html = SelfHostedHtml.ReadSelfHostedHtml("logout.html");
			if (html == null)
			{
				Console.WriteLine("RemoteController: logout.html が見つからないためフォールバック HTML を配信します");
				html = LogoutPageFallbackHtml;
			}
			else
			{
				// remote モード: 「ログインページへ」ボタンは /control?action=navigate_login を使用するため
				// __LOGIN_URL__ は空文字で置換（data-login-url="" となり JS 側で if(u) ガード）
				// 2 つのプレースホルダは相互に独立な文字列であり置換順序は
				// 結果に影響しない。連鎖 Replace を個別変数へ分解する案は最小差分原則で現状維持し、
				// 置換対象・結果を明記したコメントで可読性を担保する。
				html = html.Replace("__MODE__", "remote").Replace("__LOGIN_URL__", "");
			}
			byte[] buffer = Encoding.UTF8.GetBytes(html);

			res.ContentType = "text/html; charset=utf-8";
			ApplySecurityHeaders(res);
			res.Headers["Cache-Control"] = "no-cache, must-revalidate";
			res.ContentLength64 = buffer.Length;
			res.OutputStream.Write(buffer, 0, buffer.Length);
			res.Close();
		}

		// logout.html 不在時のフォールバック（remote モード挙動を自己完結で再現）
		private static readonly string LogoutPageFallbackHtml = @"<!DOCTYPE html>
<html lang=""ja"">
<head>
<meta charset=""utf-8"" />
<title>LOGGED OUT</title>
<style>
html,body { margin:0; padding:0; width:100%; height:100%; background:#000; color:#fff; font-family:'Segoe UI',sans-serif; box-sizing:border-box; }
.wrap { width:100%; height:100%; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:24px; }
.title { font-size:28px; letter-spacing:4px; opacity:.85; }
.btn { display:inline-block; padding:14px 40px; font-size:18px; background:#1e88e5; color:#fff; border:none; border-radius:4px; cursor:pointer; }
.btn:hover { background:#42a5f5; }
.hint { font-size:12px; color:#888; text-align:center; }
</style>
</head>
<body>
<div class=""wrap"">
	<div class=""title"">LOGGED OUT</div>
	<button type=""button"" class=""btn"" id=""relogin-btn"">ログインページへ</button>
	<div class=""hint"" id=""hint"">Cookieは保持されています。ログインページに戻れば自動ログインされます。</div>
</div>
<script>
(function () {
	var btn = document.getElementById('relogin-btn');
	var hint = document.getElementById('hint');
	btn.addEventListener('click', function () {
		// 連打防止: クリック直後に disabled 化、失敗時のみ再活性化（logout.html と同方針）
		btn.disabled = true;
		hint.textContent = '要求送信中...';
		fetch('/control?action=navigate_login', { cache: 'no-store' })
			.then(function (res) { if (!res.ok) throw new Error('HTTP ' + res.status); return res.text(); })
			.then(function () { hint.textContent = '要求済み: 本体ブラウザがログインページへ遷移中です（Cookie保持で自動ログイン）。'; })
			.catch(function (err) { btn.disabled = false; hint.textContent = '要求に失敗しました: ' + err + '（再試行できます）'; });
	});
	setInterval(function () {
		fetch('/login-state', { cache: 'no-store' })
			.then(function (res) {
				// 401（リモート認証セッション切れ）は /login-page へリダイレクト（logout.html と同方針）
				if (res.status === 401) { window.location.href = '/login-page'; return null; }
				return res.ok ? res.json() : null;
			})
			.then(function (data) { if (data && data.loggedOut === false) window.location.replace('/'); })
			.catch(function () { /* 一時的な通信エラーは次回のポーリングで再試行（logout.html と同方針） */ });
	}, 2000);
})();
</script>
</body>
</html>";

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

		// （ブルートフォース対策：IPアドレスごとのログイン失敗追跡）
		private readonly ConcurrentDictionary<string, (int count, DateTime lastAttempt)> _loginAttempts = new ConcurrentDictionary<string, (int count, DateTime lastAttempt)>();
		private const int MaxLoginAttempts = 5;
		private const int LoginBlockMinutes = 15;

		// セッションのアイドルタイムアウト（30分）
		private const int SessionTimeoutMinutes = 30;

		// リフレッシュトークンの有効期間（30日）
		private const int RefreshTokenDays = 30;

		// （クリーンアップ最適化：前回実行時刻を保持）
		private DateTime _lastCleanupTime = DateTime.MinValue;
	}
}

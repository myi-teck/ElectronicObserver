//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.Auth.cs
// summary        : Authentication, login, cookie, IP block
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
	}
}

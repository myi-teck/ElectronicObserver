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
using System.Drawing;
using System.Threading;

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
	}
}

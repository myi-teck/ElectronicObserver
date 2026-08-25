//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.Capture.cs
// summary        : WebP + WebCodecs capture, mode switching, central capture loop
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
		// task28 Phase 1: モード管理
		public enum CaptureMode { WebP, WebCodecs, HttpPolling }

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
	}
}

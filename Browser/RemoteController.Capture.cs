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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
		// モード管理
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
		// 処理ブロック名 : EncodeAndPullAsync
		// 概要           : CefSharp 経由でエンコード済みチャンクのバイナリを取得する（チャンク一覧）。
		// Base64 文字列受信 → Convert.FromBase64String() 変換
		// pull_encoded() が async に変更されたことに合わせ、
		//                  キーフレームフラグも返却するように拡張
		//------------------------------------------------------------------------------------
		private struct PullEncodedResult
		{
			public byte[] data;
			public bool isKeyFrame;
			// SPS/PPS (description) バイト列（AVCDecoderConfigurationRecord / AVCC 形式）
			// WebCodecs 仕様（W3C AVC 登録仕様に準拠）: avc.format='avc' 時、description は
			// ISO/IEC 14496-15 §5.3.3.1 の AVCDecoderConfigurationRecord 形式。
			// JS 側 pull_encoded() の第4要素（Base64）を復号した値。null = 未設定
			public byte[] description;
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
		// （WebGLバッファクリア対策としてJS側同期キャッシュから pull で引き抜く設計に変更）
		// peek=true: ダーティフラグを消費しない読み取り専用引き抜き（HTTP の cver 判定後 full 配信用）。
		//            pull/peek の第3要素（encodeCount=コンテンツ版数）から _contentVersion を更新する。
		//------------------------------------------------------------------------------------
		private async Task<Tuple<string, string>> CaptureCanvasAsync(bool isInit, bool peek = false)
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
			string script = peek ? CaptureScriptPeek : (isInit ? CaptureScriptInit : CaptureScriptNormal);

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
						// 第3要素: lastWebp 更新回数（コンテンツ版数）。HTTP の cver 判定に使用（単調増加）
						if (list.Count >= 3 && list[2] != null)
						{
							long version;
							if (long.TryParse(list[2].ToString(), out version) && version > Volatile.Read(ref _contentVersion))
							{
								Volatile.Write(ref _contentVersion, version);
							}
						}
						// 第4要素: エンジンの rAF ストール検知フラグ（旧エンジン互換のため存在時のみ観測）
						if (list.Count >= 4 && list[3] != null)
						{
							ObserveEngineRafStale(Convert.ToBoolean(list[3]));
						}
						return Tuple.Create(type, data);
					}
				}

				// まだ常駐エンジンが存在しない場合は、動的に注入して起動する（セーフティネット）
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
		// 処理ブロック名 : GetCaptureEngineStatsAsync
		// 概要           : キャプチャエンジンの統計（encodeCount / rafCount）を読み取る。
		//                  二重レート計測（配信本数 vs 固有本数）の集計窓ごとに 1 回のみ呼ぶ前提
		//                  （呼び出し毎に CefSharp IPC が発生するため高頻度呼び出し禁止）。
		// 戻り値         : 成功時 (encodeCount, rafCount)、エンジン未読込・解析失敗時は null
		//------------------------------------------------------------------------------------
		private async Task<Tuple<long, long>> GetCaptureEngineStatsAsync()
		{
			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null || !kancolleFrame.IsValid) return null;

			try
			{
				const string script = "(function(){var e=window._kancolleCaptureEngine;if(!e)return null;return [e.encodeCount||0,e.rafCount||0];})()";
				var response = await kancolleFrame.EvaluateScriptAsync(script);
				if (!response.Success || response.Result == null) return null;

				var list = response.Result as System.Collections.Generic.IList<object>;
				if (list == null || list.Count < 2) return null;
				return Tuple.Create(Convert.ToInt64(list[0]), Convert.ToInt64(list[1]));
			}
			catch (Exception ex)
			{
				// 統計読み取り失敗は計測窓のスキップのみで配信機能には無影響。握りつぶさず記録する
				Log(3, $"[RemoteController] キャプチャエンジン統計の読み取り失敗: {ex.Message}");
				return null;
			}
		}

		//====================================================================================
		// 処理ブロック名 : SendScreenDiffWebPAsync
		// 概要           : クライアントからのリクエスト受信時に、JS側のキャプチャおよび軽量差分判定を呼び出し、
		//                  差分がない場合は 204 No Content を、ある場合は WebP 画像を応答します（GET /screen.webp）。
		//                  PCウィンドウの表示サイズやアスペクト比に依存せず、常に余白なしで
		//                  高精細なゲーム画面を配信できます。
		// （C#側のGDI+デコードおよび差分判定を完全廃止し、JS側判定に一本化）
		//------------------------------------------------------------------------------------
		private async Task SendScreenDiffWebPAsync(HttpListenerRequest req, HttpListenerResponse res)
		{
			// HTTP ポーリングの活性時刻を記録（WS を開かない旧クライアントの視聴中、
			// 中央キャプチャループをコンシューマ不在判定で停止させないため）
			Volatile.Write(ref _lastHttpPollTicks, DateTime.UtcNow.Ticks);

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

				// HTTP 204 の無状態化: クライアント申告のコンテンツ版数（cver）がサーバー現在値以上なら
				// そのクライアントへは新コンテンツなし → 204。判定基準がクライアント毎の最終受領版のため、
				// 複数 HTTP クライアント同時でも各クライアントの立場で正しく 204/full が決まる
				// （WS 経路はエンジン側の stateful none＋init フルフレームで保証されるため cver 不要）。
				// cver パラメータなし（旧クライアント）＝常に full＝現行挙動（後方互換）
				long cver = -1;
				string cverStr = req.QueryString["cver"];
				if (!string.IsNullOrEmpty(cverStr))
				{
					long.TryParse(cverStr, out cver);
				}
				if (!isInit && cver >= 0)
				{
					long currentVersion = Volatile.Read(ref _contentVersion);
					if (cver >= currentVersion)
					{
						res.StatusCode = 204;
						res.Headers["X-Diff-Type"] = "none";
						res.Headers["X-Content-Version"] = currentVersion.ToString();
						return;
					}
				}

				// 1. canvas からのオンデマンドキャプチャおよびJS側差分判定を実行
				//    init=1 → pull(true)（新鮮性契約: 陳腐化時は toDataURL で新鮮画像を優先）
				//    それ以外の full 経路 → peek（ダーティフラグを消費せず toDataURL も発火しない読み取り専用）
				var capture = isInit ? await CaptureCanvasAsync(true) : await CaptureCanvasAsync(false, peek: true);
				string type = capture.Item1;
				string dataUrl = capture.Item2;

				if (type == "error")
				{
					res.StatusCode = 503;
					return;
				}

				// 2. 結果に応じたレスポンス制御
				if (type == "none")
				{
					// none は初回フレーム生成前ののみ（cver 判定済みのためここでは「エンジン未準備」を意味する）
					res.StatusCode = 503;
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
					res.Headers["X-Content-Version"] = Volatile.Read(ref _contentVersion).ToString();
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
		// （CanvasCapturePacket廃止に伴う処理移植）
		//                  注意: GC負荷低減のため、新規実装は ConvertToBytesOptimized を使用してください。
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
		// （GC負荷低減）
		// 中間バッファtempの寿命管理厳格化（null代入→GC対象化）
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
		// 処理ブロック名 : プロトコル拡張・サーバー側制御基盤
		//------------------------------------------------------------------------------------
		// 設計書に対応するプロトコル拡張およびサーバー側制御ロジックを実装
		//------------------------------------------------------------------------------------

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : BuildWebCodecsHeader
		// 概要           : WebCodecs モード用の7バイトヘッダーを構築する。
		//                  Version(1B)=0x02 + Type(1B)=0x01 + Flags(1B) + Seq(4B)
		// 設計書 §4.2.1 対応: version 引数を削除し常に 0x02 固定
		//------------------------------------------------------------------------------------
		private byte[] BuildWebCodecsHeader(byte type, byte flags, uint seq)
		{
			return new byte[] { 0x02, type, flags, (byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq };
		}

		//====================================================================================
		// 処理ブロック名 : WebCodecsCaptureLoopAsync
		// 概要           : WebCodecs モード用の中央キャプチャループ。
		//                  JS 側の VideoEncoder 経由でエンコード済みフレームを取得し、
		//                  各 WebSocket クライアントに配信する。
		//------------------------------------------------------------------------------------
		//------------------------------------------------------------------------------------
		private async Task WebCodecsCaptureLoopAsync(CancellationToken token)
		{
			Log(3, "[RemoteController] WebCodecs キャプチャループを開始しました (Debug)");

			// エンジン稼働をセッション構成へ同期（WCS のみなら WebP の toBlob を停止）
			await SetEnginePacingAsync("wcs_loop_start");

			// 2秒毎キーフレーム間隔用のストップウォッチ
			// （pacing は絶対アンカー方式へ移行したため、ループ経過計測用のストップウォッチは不要になった）
			var keyFrameSw = System.Diagnostics.Stopwatch.StartNew();
			// 初回フレームフラグ（初回はキーフレームを強制）
			bool isWebCodecsFirstFrame = true;
			// seq の型整合性の明文字
			// 設計書 §4.2 では uint (32bit 単調増加) が規定されているが、
			// C# の Interlocked.Increment は uint 型をサポートしないため int 型を使用。
			// ヘッダー書き込み時（BuildWebCodecsHeader）で (uint) キャストを行う。
			// int 型として -2147483648 〜 +2147483647 の範囲で単調増加し、
			// ラップアラウンド後も差分計算（int 演算）で過去フレームの検出が機能。
			// 符号付き整数の差分でラップアラウンド後も正しく機能するため、int 型が正当。
			int seq = 0;

			// 二重レート計測（WebP 中央ループとの比較用）: 10 秒窓の配信本数/固有本数。
			// WCS は各 pull_result が新鮮なエンコードのため重複本数は本質的に 0 だが、比較のために記録する
			long wcsDeliveries = 0;
			long wcsUniques = 0;
			var wcsWindowStart = DateTime.UtcNow;

			// 連続失敗（エンコード失敗・チャンク未回収・例外）時のリトライ待機を指数バックオフ化する。
			// 従来は 100ms 固定で、回復直後も毎回 100ms 刻みの待機が乗って復帰が遅れていた。
			// 初回 10ms から失敗のたびに倍化し 100ms を上限とする（10,20,40,80,100,…）。
			// フレーム処理が成功した時点で回数をリセットする
			int consecutiveFails = 0;
			int NextRetryDelayMs()
			{
				consecutiveFails++;
				// シフト量を 4 で頭打ち（10 << 4 = 160 → 上限 100ms でクリップ）。過剰シフトによるオーバーフローも防止
				int shift = Math.Min(consecutiveFails - 1, 4);
				return Math.Min(10 << shift, 100);
			}

			// pacing: 絶対アンカー方式（中央キャプチャループと同じ）。
			// 1 周ごとの相対待機（経過時間差分）では、エンコード〜送信の処理時間がフレーム間隔へ
			// 加算されて実効 FPS が目標を下回るため、次フレームの目標時刻を積上げるアンカーで待機を算出する。
			// ミリ秒→ティック変換は double で行い、整数除算の切り捨て誤差を排除する
			double ticksPerMs = (double)System.Diagnostics.Stopwatch.Frequency / 1000.0;
			long nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();

			try
			{
				while (!token.IsCancellationRequested)
				{
					// 省電力化: WebCodecs セッション（ClientMode=1）が不在になった時点でループを抜ける
					// （WebP セッションのみの接続では WCS パイプラインは不要。コンシューマ判定と
					// _webCodecsCaptureTask のクリアを _modeLock 内でアトミックに実行し、
					// StartWebCodecsCaptureLoopAsync の二重起動ガードとの TOCTOU 競合を防止する。
					// 自終了後に switch_mode で WCS セッションが出れば EnsureWcsPipelineStarted が再起動する。
					// JS エンジン側の稼働ゲート（setActive）は本終了の対象外:
					// エンコーダ停止は既存の finally 動作どおりとする）
					if (!HasWcsClientSessions())
					{
						bool exiting = false;
						lock (_modeLock)
						{
							if (!HasWcsClientSessions())
							{
								// 「ループ不在」状態を (task==null || task.Completed) に統一し、
								// 起動判定が確実に再起動をトリガーできるようにする。
								// 旧 CTS の Dispose は省略し GC に委譲（中央ループと同じ方針）
								Log(2, "[RemoteController] WebCodecs セッションが不在になったため、WebCodecs キャプチャループを停止します (CTS Cancel)");
								_webCodecsCaptureTask = null;
								_webCodecsCaptureCts?.Cancel();
								exiting = true;
							}
						}
						if (exiting)
						{
							// ロック外で同期（EvaluateScriptAsync はアwait が必要なため lock 内では行わない）
							await SetEnginePacingAsync("wcs_loop_stop");
							break;
						}
					}

					// Kancolle frame チェック
					// IFrame.IsValid ガード（CefSharp Issue #1354/#1509）:
					// 破棄フレーム（IsValid == false）への EvaluateScriptAsync は exit code 1073741855
					// （アクセス違反）でプロセスクラッシュし得るため、null チェックと併せて IsValid を確認する
					var kancolleFrame = _parent.GetKanColleFrame();
					if (kancolleFrame == null || !kancolleFrame.IsValid)
					{
						// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					// IPC最適化: isReinitializing チェックを個別EvaluateScriptAsyncから削除
					// 設計書 §5.3.2 対応: pull_encoded() 内で isReinitializing チェックを行う設計に統一
					// JS 側 pull_encoded() は isReinitializing=true のとき ['none', null] を返す

					// キーフレーム強制タイミングの判定（設計書 §5.3.2 対応）
					// ① 初回フレーム ② DescriptionSent 未送信状態（reinit_decoder / switch_mode でリセット後）
					// ③ 2秒毎のキーフレーム強制（GOP 周期: WebCodecsKeyFrameIntervalMs）
					// 2秒毎のキーフレーム強制要求（設計書 §5.3.2 対応）
					// 独立 keyFrameSw を使用（sw.Restart() が FPS 計測を破壊する既存問題を回避）
					// SPS/PPS (Type=0x02) を受信できる Version 2+ クライアントのみを判定対象にする。
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

					// エンコード投入〜チャンク回収を 1 回のスクリプト評価で行う
					// （旧実装の encodeFrame() 呼び出し + pull_encoded() の 2 往復を統合し、フレーム毎の IPC 固定費を半減）
					// AGENTS.md: 空 catch 禁止 — engine 未初期化・例外時は警告ログを出力し、そのフレームをスキップしてループ継続
					EncodeAndPullResult encPull;
					try
					{
						encPull = await EncodeAndPullAsync(isKeyFrame);
					}
					catch (OperationCanceledException)
					{
						throw; // キャンセル要求は外側 catch に伝播（正常終了）
					}
					catch (Exception ex)
					{
						// AGENTS.md: 空 catch 禁止 — 警告ログを出力し、そのフレームをスキップしてループ継続
						Log(3, $"[RemoteController] WebCodecs encodeAndPull() 例外（このフレームをスキップ）: {ex.Message}");
						// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					if (encPull.timedOut)
					{
						// 500ms タイムアウト（連続回数が閾値到達時は EncodeAndPullAsync 側で再初期化をトリガー済み）。
						// このフレームは破棄して次フレームへ進む
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					if (encPull.scriptFailed)
					{
						// engine 未初期化（window._kancolleCaptureEngine が null）または JS 例外
						// 警告ログを出力し、そのフレームをスキップしてループ継続
						Log(3, $"[RemoteController] WebCodecs encodeAndPull() 呼び出し失敗 (engine 未初期化または JS 例外): {encPull.encodeError ?? "unknown"}");
						// encode 失敗（engine 未注入）時にバックオフ付き再初期化を試行。
						// iframe 破棄で JS エンジンが消滅しても _isWebCodecsInitialized が true のまま再初期化されない
						// 問題（W2）を解消する。成功時は次フレームをキーフレームとして強制（SPS/PPS 再生成が必要）。
						if (await TryReinitWebCodecsEncoderAsync(token, "engine not ready")) isWebCodecsFirstFrame = true;
						// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					if (encPull.requestReinit)
					{
						// エンコーダ状態が closed / unconfigured → 再初期化してループ継続（次イテレーションで初回フレーム=キーフレーム）
						Log(3, "[RemoteController] WebCodecs encodeAndPull() が reinit を返却 (encoder closed/unconfigured)、再初期化します");
						// バックオフ付き再初期化に統一（force 初期化 + 2秒バックオフ + L1 ログ）
						var reinitOk = await TryReinitWebCodecsEncoderAsync(token, "reinit");
						if (!reinitOk)
						{
							Log(3, "[RemoteController] WebCodecs 再初期化失敗、このフレームをスキップします");
							// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
							await Task.Delay(NextRetryDelayMs(), token);
							continue;
						}
						// 再初期化後は次フレームをキーフレームとして強制（SPS/PPS 再生成が必要）
						isWebCodecsFirstFrame = true;
						continue;
					}

					if (encPull.encodeFailed)
					{
						// encodeFrame() 失敗（encoder not running 等）→ 警告ログを出力し、そのフレームをスキップしてループ継続
						Log(3, $"[RemoteController] WebCodecs encodeAndPull() 失敗（このフレームをスキップ）: {encPull.encodeError ?? "unknown"}");
						// JS 側の encodeFrame() は videoEncoder==null 状態では action:'reinit' を返さないため、
						// この success:false 経路でも再初期化を行うことで自己回復する。
						// 成功時は次フレームをキーフレームとして強制（SPS/PPS 再生成が必要）。
						if (await TryReinitWebCodecsEncoderAsync(token, $"encode failed: {encPull.encodeError}")) isWebCodecsFirstFrame = true;
						// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					// エンコード投入は成功 → 初回フレームフラグをクリア（初回フレームが実際にエンコードされたことを示す）
					isWebCodecsFirstFrame = false;
					// 正常稼働に戻ったためバックオフを解除（次回が失敗しても 10ms から再試行する）
					consecutiveFails = 0;

					if (encPull.frames == null)
					{
						// エンコーダ出力は非同期（output コールバック）のため、まだ回収可能なチャンクが無い。
						// 短待機して次評価へ進む（旧実装の pull 空返却時と同じ扱い）
						// 連続失敗時は指数バックオフ（10ms 起点・100ms 上限）で再試行
						await Task.Delay(NextRetryDelayMs(), token);
						continue;
					}

					// 回収したチャンクをフレーム単位に配信する（1 回の評価で複数フレームが溜まることがある）
					foreach (var pullResult in encPull.frames)
					{

					// 固有本数: pull_result は毎回新鮮なエンコード（WCS 経路は重複キャッシュを配信しない）
					// 配信本数: 中央ループと同じ「ファンアウトされたフレーム本数」基準で計上する（重複本数は本質的に 0）
					wcsUniques++;
					wcsDeliveries++;

					// キーフレームフラグ: チャンク毎の値を正とする。
					// C# 側の isKeyFrame は「投入要求」のフラグであり、非同期出力で後から届く
					// 古い P フレームへ誤適用すると、P フレームにキーフレームフラグが立って
					// 受信側の復元点がずれるため使用しない
					bool dataIsKeyFrame = pullResult.isKeyFrame;

					// SPS/PPS (description) をキャッシュ（reinit_decoder 再送信・新クライアント追隨送信用）
					// キーフレームに添付された SPS/PPS は AVC セッション内で不変のため、最新値を保持して再利用する
					// 不変性保証: pullResult.description は CefSharp 経由の新規 byte[] だが、
					// 将来のバッファ再利用・共有化に備え、キャッシュ登録時に防御コピー（不変配列化）を行う。
					// 代入先 _lastWebCodecsDescription は volatile 宣言済みのため、プレーン代入でも
					// フルフェンス付きアトミックな参照交換として他スレッドに可視化される（CS0420 回避）
					if (pullResult.description != null && pullResult.description.Length > 0)
					{
						var cachedDesc = new byte[pullResult.description.Length];
						Buffer.BlockCopy(pullResult.description, 0, cachedDesc, 0, cachedDesc.Length);
						_lastWebCodecsDescription = cachedDesc;
					}

					// Flags: キーフレームかどうか（エンコーダが報告したチャンク種別が唯一の正）
					byte flags = dataIsKeyFrame ? (byte)0x01 : (byte)0x00;

					// SPS/PPS キュー投入パス: description 未送信のセッションにはフレームより先に
					// SPS/PPS (Type=0x02) をセッション別チャネルへ投入する（設計書 §4.2.2 ステップ3）。
					// seq をフレーム seq より前のここで採番することで、SPS/PPS seq < フレーム seq が
					// seq 空間・チャネル順の両方で保証される。実際の送信はセッション別の
					// WebCodecsFanoutLoopAsync が行う（送信のループ外化）
					foreach (var session in _webSocketSessions.Values)
					{
						if (session.WebSocket.State != WebSocketState.Open)
							continue;

						// クライアントバージョンを確認（null 時は送信抑止）
						// SPS/PPS (Type=0x02) は 7B ヘッダーの Version 2 形式のため、
						// Version 1 クライアント（6B ヘッダー）には送信不可。
						// バージョンチェックを SPS/PPS 投入より前に置く
						var clientVer = GetClientVersion(session.SessionId);
						if (clientVer == null)
							continue;

						if (clientVer < 2)
						{
							// Version 1: 6B ヘッダー（WebPのみ、WebCodecsは非対応）
							// Version 1 クライアントへの警告ログ追加
							// 毎フレーム出力するとログスパイクになるため、
							// 10秒間隔でダウンスープリングする（TryLogVersion1Warning）
							if (session.TryLogVersion1Warning())
							{
								Log(3, $"[RemoteController] WebCodecs フレームを Version 1 クライアント (session={session.SessionId}) に送信しない（10秒間隔ログ）");
							}
							continue;
						}

						// 配信経路のフィルタ: WCS パイプラインは WebCodecs セッション（ClientMode=1）のみに配信する
						// （WebP セッションは中央ループ→ファンアウト経路で受信するため、両経路配信は重複フレームになる）
						if (session.ClientMode != 1)
							continue;

						// キュー投入中フラグ（TryBeginDescriptionQueue）により、ファンアウトが
						// 未送信のうちに同一セッションへ SPS/PPS を重複投入しない。
						// 連続失敗時のバックオフ期間中は再投入を抑制する（フレームのみの送信は従来同様）
						if (!session.DescriptionSent && session.TryBeginDescriptionQueue())
						{
							if (session.CanSendDescription())
							{
								// volatile 参照はローカル変数へ即スナップショットし、
								// 送信中に別スレッドで _lastWebCodecsDescription が差し替わっても影響を受けないようにする
								// （volatile 宣言フィールドのプレーン読み取りはフルフェンス付きで可視性が保証される）
								// （description 自体は防御コピー済みの不変配列。ファンアウト側は読み取りのみで共有して安全）
								byte[] descToSend = pullResult.description ?? _lastWebCodecsDescription;
								if (descToSend != null && descToSend.Length > 0)
								{
									int descSeq = Interlocked.Increment(ref _globalSequenceNumber);
									session.EnqueueWcsSend(new WebSocketSession.WcsSendItem
									{
										IsDescription = true,
										Seq = descSeq,
										Data = descToSend,
									});
								}
								else
								{
									// 投入先が無い場合はフラグを解放して次フレームの再投入を可能にする
									session.EndDescriptionQueue();
								}
							}
							else
							{
								session.EndDescriptionQueue();
							}
						}
					}

					// フレーム seq 採番: フレーム単位で 1 回（セッションループ外）で採番し、
					// 同一フレームが全セッションに同一 seq で共有される。受信側は seq で順序保証を行う設計であり、
					// セッション横断の単調増加（シーケンス空間）が維持される（§1.3 の不変条件）。
					// 同一グローバルカウンタ（_globalSequenceNumber）を維持し、heartbeat は seq を消費しない
					seq = Interlocked.Increment(ref _globalSequenceNumber);

					// フレーム キュー投入パス: セッション別有界チャネル（最新保持ドロップ）へ書き込む。
					// データ本体（pullResult.data）は不変配列のため全セッションで共有し、コピーしない。
					// 送信をループ外化することで、1 クライアントの TCP 詰まりが他セッションの
					// 配信レートを下げない（HOL blocking 解消）
					foreach (var session in _webSocketSessions.Values)
					{
						if (session.WebSocket.State != WebSocketState.Open)
							continue;

						var clientVer = GetClientVersion(session.SessionId);
						if (clientVer == null || clientVer < 2)
							continue;

						if (session.ClientMode != 1)
							continue;

						session.EnqueueWcsSend(new WebSocketSession.WcsSendItem
						{
							IsDescription = false,
							Seq = seq,
							Flags = flags,
							Data = pullResult.data,
						});
					}
					} // foreach (var pullResult in encPull.frames)

					// 二重レート計測ゲート: 10 秒窓のまとめログ（WebP 中央ループの [Profile] ログと比較用）
					var wcsWindowSec = (DateTime.UtcNow - wcsWindowStart).TotalSeconds;
					if (wcsWindowSec >= 10)
					{
						long wcsDup = wcsDeliveries - wcsUniques;
						if (wcsDup < 0) wcsDup = 0;
						Log(3, $"[Profile] WCSループ: 配信={wcsDeliveries} 固有={wcsUniques} 重複={wcsDup} (窓={wcsWindowSec:F1}s)");
						wcsDeliveries = 0;
						wcsUniques = 0;
						wcsWindowStart = DateTime.UtcNow;
					}

					// pacing: 絶対アンカー方式で次フレームの目標時刻まで待機する。
					// 待機は 2ms 下限を設け、Windows タイマー粒度での過剰な短待機を避ける。
					// 2 フレーム分以上遅れた場合のみアンカーを現在時刻へ戻す。
					// （過去スロットを順に追うと待機なしの連続実行＝スピン状態になるため）
					{
						// _webCodecsFps は RecalculateWebCodecsFps / SetWebCodecsFps で 1 以上へ制限されるが、
						// 初期化前・異常値での 0 除算を避けるため安全側に 10FPS 相当をフォールバックさせる
						int frameIntervalMs = _webCodecsFps > 0 ? 1000 / _webCodecsFps : 100;

						nextTargetTicks += (long)(frameIntervalMs * ticksPerMs);
						long waitTicks = nextTargetTicks - System.Diagnostics.Stopwatch.GetTimestamp();
						if (waitTicks > 0)
						{
							int remainingMs = (int)(waitTicks / ticksPerMs);
							if (remainingMs > 2)
							{
								await Task.Delay(remainingMs, token);
							}
						}
						else if (-waitTicks > (long)(frameIntervalMs * ticksPerMs * 2))
						{
							nextTargetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
						}
					}
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
					// 自分のトークン（token 引数）が現在有効な CTS のトークンと一致する場合のみ null にクリアする
					// （新タスク起動で _webCodecsCaptureCts が差し替えられた場合、旧タスクの finally はクリアしない・S3 解消）
					// null チェック先行: _webCodecsCaptureCts == null（サーバ停止済 / CTS 未作成）の場合、
					// && の短絡評価で .Token アクセスが回避され、タスク参照はクリアしない。ループメソッドは起動時に必ず
					// 専用 CTS のトークンを引数として受け取るため、CancellationToken.None による偽陽性は発生しない
					// .Token アクセスを try-catch で保護
					// （Dispose() メソッドで CTS が Dispose された場合、.Token アクセスで ObjectDisposedException を投げる）
					try
					{
						if (_webCodecsCaptureCts != null && _webCodecsCaptureCts.Token == token)
						{
							_webCodecsCaptureTask = null;
							// 判定結果（一致）をログ出力（§5 L7 と整合、優先度2）
							Log(2, "[RemoteController] WebCodecs ループ終了: token一致でタスク参照をクリア");
						}
						else
						{
							// token 不一致（新ループが既に起動済み）または CTS null の場合、クリアしない（§5 L7 と整合）
							Log(2, "[RemoteController] WebCodecs ループ終了: token不一致（またはCTS null）でタスク参照をクリアしない");
						}
					}
					catch (ObjectDisposedException)
					{
						// CTS が Dispose された場合（サーバ停止時）はクリアしない（サーバ停止時なので無害）。
						// サイレントに握りつぶさず必ずログ出力する（AGENTS.md NEVER「サイレントな例外の握りつぶし」遵守）
						Log(3, "[RemoteController] WebCodecs ループ終了: CTSがDispose済みのためtoken比較をスキップ");
					}
				}
				Log(2, "[RemoteController] WebCodecs キャプチャループが完全に終了しました");
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : TryReinitWebCodecsEncoderAsync
		// 概要           : encode 失敗時のバックオフ付き再初期化ハンドラ。
		//                  JS エンジンが iframe 破棄で消滅・再注入されても _isWebCodecsInitialized が
		//                  true のままで再初期化されない問題（W2）を解消する。
		//------------------------------------------------------------------------------------
		// バックオフ判定（比較→更新）を lock 内で原子化し、並行呼び出しでの
		// 過剰な再初期化発火・過剰な抑制を防ぐ。InitializeWebCodecsEncoderAsync への呼び出しは
		// ロック解放後（_modeLock / _captureStartLock の同時取得禁止、§6.3 参照）。
		private async Task<bool> TryReinitWebCodecsEncoderAsync(CancellationToken token, string reason)
		{
			var nowUtc = DateTime.UtcNow;
			lock (_webCodecsReinitLock)
			{
				if ((nowUtc - _lastWebCodecsReinitUtc).TotalMilliseconds < WebCodecsReinitBackoffMs)
				{
					// バックオフ期間中は再初期化をスキップ（100ms 待機は呼び出し側で実施）
					// スキップ理由もログに残す（可観測性向上、§5 L1 参照）
					// 優先度1（デバッグレベル）: 既定の LogLevel=2 では非表示
					// （FormLog は Priority >= LogLevel で表示、既定 LogLevel=2）。
					// 検証時は LogLevel=1 へ変更して確認（§7.4 / SEC-MINOR-2 参照）
					Log(1, $"[RemoteController] WebCodecs 再初期化をバックオフでスキップ (reason: {reason})");
					return false;
				}
				_lastWebCodecsReinitUtc = nowUtc;
			}
			Log(3, $"[RemoteController] WebCodecs 再初期化を試行 (reason: {reason})");
			var ok = await InitializeWebCodecsEncoderAsync(token, force: true);
			if (ok)
			{
				Log(3, "[RemoteController] WebCodecs 再初期化成功（force）");
			}
			else
			{
				Log(3, $"[RemoteController] WebCodecs 再初期化失敗 (reason: {reason})、次回バックオフ後に再試行");
			}
			return ok;
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : InitializeWebCodecsEncoderAsync
		// 概要           : CefSharp 経由で JS の initEncoder() を呼び出し、WebCodecs エンコーダを初期化する。
		//                  タイムアウト（5秒）制御付き。
		//------------------------------------------------------------------------------------
		// force パラメータを追加し、_isWebCodecsInitialized の早期 true 返却を回避する
		// （iframe 破棄で JS エンジンが消滅してもフラグが true のままで再初期化されない問題 W2 の解消）。
		// メソッド全体を _webCodecsInitSemaphore（SemaphoreSlim(1,1)）で
		// 直列化し、force=true 経路が複数スレッドから同時呼ばれる場合の二重初期化・二重 SPS/PPS 再送を防ぐ。
		// WaitAsync は try ブロックの**外側**に配置する:
		// 待機中に cancellationToken がキャンセルされて OperationCanceledException が投げられた場合は
		// セマフォが未取得（カウント未減算）であり、この状態で Release() を呼び出すと
		// SemaphoreFullException を投げる。try に入る前に例外が発生するため finally は実行されず、
		// 未取得のまま Release() を呼ばないという正しい挙動となる。
		private async Task<bool> InitializeWebCodecsEncoderAsync(
			CancellationToken cancellationToken = default,
			bool force = false)
		{
			// _webCodecsInitSemaphore.Dispose() 後の WaitAsync を防止するガード:
			// RemoteController.Dispose() 後にはセマフォが破棄済みとなり、WaitAsync が ObjectDisposedException を投げる。
			// _webCodecsDisposed フラグを事前チェックして通常経路で例外を回避する（Dispose と初期化呼び出しの競合防御）。
			if (_webCodecsDisposed)
			{
				Log(3, "[RemoteController] WebCodecs エンコーダ初期化: コントローラ破棄済み、初期化をスキップ");
				return false;
			}
			// 上記フラグチェックと WaitAsync 間の TOCTOU ウィンドウで、セマフォが
			// Dispose() により破棄され、WaitAsync が ObjectDisposedException を投げ得る。
			// 捕捉してログ出力する（サイレントな握りつぶし禁止）。許可未取得のため Release は不要。
			// 注意: WaitAsync の OperationCanceledException はここでは捕捉せず従来通り呼び出し側に伝播させる
			// （R3-MINOR-1 の「try ブロック外配置」の正しいパターンを維持）。
			try
			{
				await _webCodecsInitSemaphore.WaitAsync(cancellationToken);
			}
			catch (ObjectDisposedException)
			{
				Log(3, "[RemoteController] WebCodecs エンコーダ初期化: セマフォ破棄済み、初期化をスキップ");
				return false;
			}
			try
			{
				// force=true の場合は _isWebCodecsInitialized をリセットして再初期化を行う
				if (force) _isWebCodecsInitialized = false;
				// 既に初期化済みなら即座に完了
				if (_isWebCodecsInitialized) return true;

				// CefSharp 経由で JS の initEncoder() を呼び出す
				// IFrame.IsValid ガード（CefSharp Issue #1354/#1509）:
				// 破棄フレーム（IsValid == false）への EvaluateScriptAsync は exit code 1073741855
				// （アクセス違反）でプロセスクラッシュを引き起こし得るため、null チェックと併せて IsValid を確認する
				var kancolleFrame = _parent.GetKanColleFrame();
				if (kancolleFrame == null)
				{
					// 失敗種別①: 艦これフレーム自体が未読込（エンジン注入の前提が成立していない）
					Log(2, "[RemoteController] WebCodecs エンコーダ初期化失敗（種別: キャプチャエンジン未注入・艦これフレーム未読込）");
					return false;
				}
				if (!kancolleFrame.IsValid)
				{
					Log(3, "[RemoteController] WebCodecs エンコーダ初期化: frame invalid（破棄フレーム）");
					return false;
				}

				// コーデック値のエスケープ処理を追加（特殊文字の JS 注入防止）
				// 特殊文字（' , " , \ , 制御文字）が含まれる場合のJavaScript構文エラーを防止
				// JS エスケープの完全性向上
				// 制御文字（\u0000-\u001F）を \\uXXXX 形式でエスケープし、JS 構文エラーを防止
				var escapedCodec = _webCodecsCodec
					.Replace("\\", "\\\\")
					.Replace("'", "\\'")
					.Replace("\"", "\\\"")
					.Replace("\n", "\\n")
					.Replace("\r", "\\r")
					.Replace("\t", "\\t");
				// 残りの制御文字（\u0000-\u001F 除去済み以外）を \\uXXXX 形式でエスケープ
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
				// string.Format の代わりに固有トークン置換 + string.Replace で構築
				// 理由: string.Format は JS リテラルブレース（{if...} {return...} {codec...}）をフォーマット項目と解釈し、
				// FormatException（「入力文字列の形式が正しくありません」）を投げて JS に到達しなかった。
				// verbatim テンプレートには {0} プレースホルダを含ませないため、ブレースのエスケープが不要で検証しやすい。
				// 置換順序: __BITRATE__ / __FPS__（整数値）を先に置き、__CODEC__ は最後に置換して、
				// コーデック値にトークン文字列が含まれる場合の二重置換を防止する。
				// framerate は configure() 時点で焼き込まれる値のため、スクリプト構築時の値をスナップショットし、
				// 初期化成功時にそのまま _encoderConfiguredFps へ記録する（初期化待ちの間の FPS 変更と混同しないため）
				int fpsForEncoder = _webCodecsFps;
				var script = WebCodecsInitEncoderTemplate
					.Replace("__BITRATE__", _webCodecsBitrate.ToString())
					.Replace("__FPS__", fpsForEncoder.ToString())
					.Replace("__CODEC__", escapedCodec);
	
				// タイムアウト制御（5秒）
				// design doc §5.3.2a: timeoutCts.Cancel() により delayTask を確実にキャンセル
				// 設計書 §5.3.2a 通り 5秒 に統一
				// using 句で CancellationTokenSource を確実に破棄（タスクリーク防止）
				using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
				using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
				{
					// EvaluateScriptAsPromiseAsync に切替（CefSharp 143 WebBrowserExtensions 拡張メソッド）
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
	
					// EvaluateScriptAsPromiseAsync を使用し、initEncoder() の Promise を解決して回収
					// evalTask が既に完了している場合は await せずに結果を取得
					// Task.WhenAny 後に evalTask を await すると、evalTask が未完了の状態でブロックするリスクがある
					// ただし、この場合 completedTask == evalTask なので安全に await 可能
					var result = await evalTask;
					// Promise が reject された場合（initEncoder が例外を投げる等）
					// EvaluateScriptAsPromiseAsync は catch 経路でエラー文字列を Result に格納して Success=false を返す
					if (!result.Success)
					{
						Log(3, $"[RemoteController] WebCodecs エンコーダ初期化失敗 (Promise reject): {result.Result}");
						return false;
					}

					// engine 未初期化（window._kancolleCaptureEngine が null）の場合、IIFE が undefined を解決し Result は null
					if (result.Result == null)
					{
						// 失敗種別①: スクリプトが undefined を解決＝エンジンオブジェクト未注入（艦これフレーム未読込）
						Log(2, "[RemoteController] WebCodecs エンコーダ初期化失敗（種別: キャプチャエンジン未注入・window._kancolleCaptureEngine なし）");
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
						// configure() に渡した framerate を記録（SetWebCodecsFps の 2 倍乖離判定の基準値）
						_encoderConfiguredFps = fpsForEncoder;
						var codec = dict.TryGetValue("codec", out var codecVal) ? codecVal as string : null;
						Log(2, $"[RemoteController] WebCodecs エンコーダ初期化成功 (codec: {codec}, framerate: {fpsForEncoder})");
						return true;
					}
					else
					{
						var error = dict.TryGetValue("error", out var errorVal) ? errorVal as string : null;
						var errorMsg = error ?? successVal?.ToString() ?? "unknown";
						// 失敗種別②〜⑤ を JS 返却エラー文言で区別（機構 B の 4 条件切り分け・接続方式診断に供する）
						string failureKind;
						if (errorMsg.Contains("VideoEncoder not supported"))
							failureKind = "videoEncoder 非対応";
						else if (errorMsg.Contains("Secure Context"))
							failureKind = "非 Secure Context（http:// 直接入り）";
						else if (errorMsg.Contains("Config unsupported"))
							failureKind = "isConfigSupported 失敗";
						else if (errorMsg.Contains("reinitializing"))
							failureKind = "再初期化中";
						else
							failureKind = "その他";
						Log(2, $"[RemoteController] WebCodecs エンコーダ初期化失敗（種別: {failureKind}）: {errorMsg}");
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
			finally
			{
				// 直列化シグナルの解放
				// （WaitAsync が try 内側にあるため、この finally に到達した場合必ずシグナルを取得済み）
				_webCodecsInitSemaphore.Release();
			}
		}

		//------------------------------------------------------------------------------------
		// 処理ブロック名 : StopWebCodecsEncoderAsync
		// 概要           : CefSharp 経由で JS の stopEncoder() を呼び出し、WebCodecs エンコーダを停止する。
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
		// 設計書 §5.3.1 対応: コーデック変更経路の実装
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
		// 概要           : WebCodecs エンコーダの実効 FPS を変更する。
		//                  volatile 宣言によりキャプチャループの pacing へ即座に反映される。
		//------------------------------------------------------------------------------------
		// エンコーダの framerate は configure() 時に焼き込まれるため、実効 FPS が設定値から
		// 2 倍以上乖離した場合のみ再初期化を行う。2 倍未満の再初期化はデコーダ再構成（再生断）と
		// SPS/PPS 再送を伴う割に体感改善が小さく、帯域・CPU の浪費が勝るため行わない。
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

			// 未初期化（基準値不明）の場合は再初期化しない。次の初期化で新しい FPS が使われる
			int configuredFps = _encoderConfiguredFps;
			if (!_isWebCodecsInitialized || configuredFps <= 0) return;

			// 2 倍以上の乖離判定（大きい値 / 小さい値 >= 2）。整数比較で丸め誤差を避けるため除算ではなく乗算で比較
			int larger = Math.Max(configuredFps, fps);
			int smaller = Math.Min(configuredFps, fps);
			if (larger < smaller * 2) return;

			Log(2, $"[RemoteController] エンコーダ設定 FPS({configuredFps}) と実効 FPS({fps}) が 2 倍以上乖離、エンコーダを再初期化します");

			// 再初期化で SPS/PPS が再生成されるため、全 WCS セッションの送信済みフラグを戻して
			// 次キーフレームでの再送を強制する（ループ側の anyDescriptionPending 判定でキーフレーム化される）
			foreach (var s in _webSocketSessions.Values)
			{
				if (s.ClientMode != 1) continue;
				s.DescriptionSent = false;
				s.ResetDescriptionSendState();
			}

			// 多重要求の抑止: 連続した set_fps 受信で再初期化が積み上がると、
			// セマフォ待機が長引いてキャプチャループの停止時間を延ばすため 1 本のみ実行する
			if (Interlocked.CompareExchange(ref _fpsReinitInFlight, 1, 0) != 0) return;

			// 呼び出し元（WebSocket ハンドラ／FPS 再計算）をブロックしないよう fire-and-forget。
			// InitializeWebCodecsEncoderAsync はセマフォで直列化済みで失敗時は false を返すため、
			// 例外はここで捕捉してログ化する（サイレントな握りつぶしはしない）
			_ = Task.Run(async () =>
			{
				try
				{
					await InitializeWebCodecsEncoderAsync(CancellationToken.None, force: true).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log(3, $"[RemoteController] FPS 乖離によるエンコーダ再初期化で例外: {ex.Message}");
				}
				finally
				{
					Interlocked.Exchange(ref _fpsReinitInFlight, 0);
				}
			});
		}

		//====================================================================================
		// 処理ブロック名 : RecalculateWebCodecsFps
		// 概要           : WebCodecs セッション（ClientMode=1）の状態から WCS パイプラインの FPS を再計算する。
		//                  優先順位: 手動指定 FPS の最大値 → 目標間隔から導出た FPS の最大値 → 既定 30（セッション不在）。
		//                  SetWebCodecsFps は冪等（同一値なら no-op）のため頻繁な呼び出しは安全。
		//------------------------------------------------------------------------------------
		// 手動指定が 1 つでも存在する間は auto 調整（getDynamicDelay）の間隔を反映しない。
		// これにより自動省電力調整がユーザーの FPS スライダー設定を上書きしない。
		private void RecalculateWebCodecsFps()
		{
			int maxFps = 0;
			bool hasManual = false;

			// (1) 手動指定優先: WCS セッションの LastManualFps の最大値
			foreach (var session in _webSocketSessions.Values)
			{
				if (session.ClientMode != 1) continue;
				int? manual = session.LastManualFps;
				if (manual.HasValue)
				{
					hasManual = true;
					if (manual.Value > maxFps) maxFps = manual.Value;
				}
			}

			// (2) 手動指定なし: 目標間隔から導出した FPS の最大値（0 = 無制限 → 60、1..60 にクランプ）
			if (!hasManual)
			{
				foreach (var session in _webSocketSessions.Values)
				{
					if (session.ClientMode != 1) continue;
					int interval = session.TargetIntervalMs;
					int derived = (interval <= 0) ? 60 : Math.Max(1, Math.Min(60, (int)Math.Round(1000.0 / interval)));
					if (derived > maxFps) maxFps = derived;
				}
			}

			// (3) WCS セッション不在時は既定 30
			if (maxFps <= 0) maxFps = 30;

			SetWebCodecsFps(maxFps);
		}

		// 連続タイムアウトカウント（EncodeAndPullAsync）
		// JS 側 _acquireLock() タイムアウト（50ms）と C# 側タイムアウト（500ms）の10倍の開きにより、
		// 連続タイムアウト時に過剰な再試行/ CPU リソース消費を防ぐための保護。
		// 閾値超過時はループを中断して再初期化トリガーを返す。
		private int _pullTimeoutCount = 0;
		private const int MaxPullTimeoutCount = 10;

		//====================================================================================
		// 処理ブロック名 : EncodeAndPullAsync
		// 概要           : encodeFrame() 投入と既生成チャンクの回収を 1 回の EvaluateScriptAsync で行う。
		//                  従来は encode 用スクリプトと pull_encoded() の 2 往復が必要で、
		//                  フレーム毎の CefSharp IPC 往復が固定費となっていた。
		//                  500ms タイムアウトと連続タイムアウトカウントは従来実装から継承する。
		//------------------------------------------------------------------------------------
		// JS 返却値の判別: 失敗系はプレーンオブジェクト（IDictionary）、成功系は配列（IList）。
		// エンコーダ出力は非同期のため、投入時に回収済みチャンクが複数残ることがある（全件を返す）。
		private struct EncodeAndPullResult
		{
			public bool scriptFailed;   // エンジン未注入・JS 例外・不正な返却値
			public bool timedOut;       // 500ms タイムアウト（このフレームは破棄）
			public bool requestReinit;  // JS 側が encoder closed/unconfigured を報告
			public bool encodeFailed;   // encodeFrame() が success:false を返却
			public string encodeError;
			public List<PullEncodedResult> frames;   // 回収チャンク（null = 回収なし）
		}

		private async Task<EncodeAndPullResult> EncodeAndPullAsync(bool isKeyFrame)
		{
			var result = new EncodeAndPullResult { frames = null };

			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null)
			{
				result.scriptFailed = true;
				result.encodeError = "Kancolle frame not found";
				return result;
			}

			string script = WebCodecsEncodeAndPullTemplate.Replace("__IS_KEY_FRAME__", isKeyFrame ? "true" : "false");

			// タイムアウト制御（500ms）: EvaluateScriptAsync の既定タイムアウトは 30秒であり、
			// JS 側の _acquireLock() でブロックされている場合、ループが永久に待機するリスクがある
			using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
			{
				try
				{
					var evalTask = kancolleFrame.EvaluateScriptAsync(script);
					var delayTask = Task.Delay(500, timeoutCts.Token);

					var completed = await Task.WhenAny(evalTask, delayTask);
					if (completed == delayTask)
					{
						// 連続タイムアウトカウントインクリメント
						// Interlocked に原子化（将来的な並列化に備える）
						int currentCount = System.Threading.Interlocked.Increment(ref _pullTimeoutCount);
						if (currentCount >= MaxPullTimeoutCount)
						{
							Log(3, $"[RemoteController] EncodeAndPullAsync 連続タイムアウト {currentCount} 回 (最大 {MaxPullTimeoutCount} 回に達)、再初期化をトリガーします");
							// Interlocked.Exchange でリセット
							System.Threading.Interlocked.Exchange(ref _pullTimeoutCount, 0);
						}
						else
						{
							Log(3, $"[RemoteController] EncodeAndPullAsync タイムアウト ({currentCount}/{MaxPullTimeoutCount} 回) — 再試行を続行");
						}
						// タイムアウト時はフレームをドロップ
						result.timedOut = true;
						return result;
					}

					// 成功時は連続タイムアウトカウントをリセット
					// Interlocked.Exchange でリセット
					System.Threading.Interlocked.Exchange(ref _pullTimeoutCount, 0);

					var scriptResult = await evalTask;
					if (!scriptResult.Success || scriptResult.Result == null)
					{
						result.scriptFailed = true;
						result.encodeError = scriptResult.Success ? "Result null" : scriptResult.Message;
						return result;
					}

					// 失敗系（{ action:'reinit' } / { success:false, error }）はオブジェクトとして届く
					var dict = scriptResult.Result as System.Collections.Generic.IDictionary<string, object>;
					if (dict != null)
					{
						if (dict.TryGetValue("action", out var actionVal) && actionVal is string actionStr && actionStr == "reinit")
						{
							result.requestReinit = true;
							return result;
						}
						result.encodeFailed = true;
						result.encodeError = dict.TryGetValue("error", out var errVal) ? errVal as string : null;
						return result;
					}

					// 成功系: ['none'] または ['encoded', [[base64, isKey, descBase64], …]]
					var list = scriptResult.Result as System.Collections.Generic.IList<object>;
					if (list == null || list.Count < 1)
					{
						result.scriptFailed = true;
						result.encodeError = "予期しない返却値の型";
						return result;
					}
					if (list[0] as string != "encoded") return result; // ['none'] = まだ回収可能なチャンクなし

					var chunkList = list.Count >= 2 ? list[1] as System.Collections.Generic.IList<object> : null;
					if (chunkList == null || chunkList.Count == 0) return result;

					var frames = new List<PullEncodedResult>(chunkList.Count);
					foreach (var chunkObj in chunkList)
					{
						var chunk = chunkObj as System.Collections.Generic.IList<object>;
						if (chunk == null || chunk.Count < 1) continue;

						// Base64 文字列を受信し、直ちに byte[] 化（GC ガベージゼロ）
						string base64 = chunk[0] as string;
						if (string.IsNullOrEmpty(base64)) continue;

						var frame = new PullEncodedResult { isKeyFrame = false, description = null };
						try
						{
							frame.data = Convert.FromBase64String(base64);
						}
						catch (FormatException ex)
						{
							// 不正な Base64 のチャンクは破棄（例外の握りつぶし禁止のためログ出力）
							Log(3, $"[RemoteController] EncodeAndPullAsync: フレーム Base64 復号失敗: {ex.Message}");
							continue;
						}

						if (chunk.Count >= 2 && chunk[1] as string == "1") frame.isKeyFrame = true;

						// SPS/PPS (description): キーフレームにのみ Base64 で添付される（設計書 §4.2.2 ステップ2）
						if (chunk.Count >= 3 && chunk[2] is string descriptionB64 && !string.IsNullOrEmpty(descriptionB64))
						{
							try
							{
								frame.description = Convert.FromBase64String(descriptionB64);
							}
							catch (FormatException ex)
							{
								// 不正な Base64 は破棄（例外の握りつぶし禁止のためログ出力）
								Log(3, $"[RemoteController] EncodeAndPullAsync: SPS/PPS Base64 復号失敗: {ex.Message}");
							}
						}

						frames.Add(frame);
					}

					result.frames = frames.Count > 0 ? frames : null;

					// 成功時に明示的にキャンセル（タイマー稼働防止）
					timeoutCts.Cancel();
					return result;
				}
				catch (OperationCanceledException)
				{
					// タイムアウトによるキャンセル
					result.timedOut = true;
					return result;
				}
				catch (Exception ex)
				{
					Log(3, $"[RemoteController] EncodeAndPullAsync 例外: {ex.Message}");
					result.scriptFailed = true;
					result.encodeError = ex.Message;
					return result;
				}
			}
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
					// 正規表現による堅牢なJSONパース
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
		// 概要           : 非推奨（後方互換 shim）。旧経路（POST /capture-mode / WCS 初期化失敗フォールバック）
		//                  の要求モードを _currentCaptureMode に記録し、要求種別に対応するパイプラインを起動保証する。
		//                  反対側パイプラインの停止・旧ループ完了待機は行わない
		//                  （WebP/WCS の共存を許容し、実効経路はセッション単位 ClientMode で判定するため）。
		//------------------------------------------------------------------------------------
		private async Task SetCaptureModeAsync(CaptureMode newMode)
		{
			lock (_modeLock)
			{
				if (_currentCaptureMode != newMode)
				{
					Log(2, $"[RemoteController] キャプチャモード参照値更新（後方互換）: {_currentCaptureMode} → {newMode}");
					_currentCaptureMode = newMode;
				}
			}

			// 要求種別のパイプラインを起動保証（冪等。稼働中なら内部ガードでスキップ）。
			// 旧モードのキャンセルを行わないため、切替後も反対側パイプラインは
			// コンシューマが居る限り稼働を続ける（混合接続の維持）
			switch (newMode)
			{
				case CaptureMode.WebCodecs:
					await EnsureWcsPipelineStarted("mode_switch");
					break;
				case CaptureMode.WebP:
					EnsureWebpPipelineStarted("mode_switch");
					break;
				case CaptureMode.HttpPolling:
					// HTTP ポーリングは SendScreenDiffWebPAsync のオンデマンド配信であり、専用パイプラインを持たない
					break;
			}
		}

		//====================================================================================
		// 処理ブロック名 : StartWebCodecsCaptureLoopAsync
		// 概要           : WCS ループの起動（事前稼働チェック + force 初期化 + 二重起動防止）。
		//                  SetCaptureModeAsync（後方互換 shim）・EnsureWcsPipelineStarted・
		//                  switch_mode ハンドラの生存性チェックで共用する。
		//------------------------------------------------------------------------------------
		// 事前稼働チェック（Fast-Path）:
		// WCS ループが既に稼働中（タスク存在かつ未完了）なら、force 初期化自体を行わず早期リターンする。
		// これにより、並行接続（2台が同時 switch_mode webcodecs）で 2 番目の呼び出しが
		// InitializeWebCodecsEncoderAsync(force: true) を実行して稼働中エンコーダを強制再初期化
		// （_isWebCodecsInitialized = false の再設定 + JS 側 initEncoder() 再実行 + SPS/PPS 再送）するのを防止する。
		private async Task StartWebCodecsCaptureLoopAsync()
		{
			// 事前稼働チェック（Fast-Path）: _modeLock 内で「タスク存在かつ未完了」を検査し、
			// 稼働中なら force 初期化前に早期リターンする
			lock (_modeLock)
			{
				var existing = _webCodecsCaptureTask;
				if (existing != null && !existing.IsCompleted)
				{
					// 既に稼働中（スキップ）
					Log(3, "[RemoteController] WebCodecs ループは既に稼働中のため、起動処理をスキップ");
					return;
				}
			}

			// force 再初期化（_isWebCodecsInitialized フラグを無視して initEncoder() を実行）
			var initResult = await InitializeWebCodecsEncoderAsync(force: true);
			if (!initResult)
			{
				// 再初期化失敗時は WebP にフォールバック（SetCaptureModeAsync shim 経由: 参照値更新 + WebP パイプライン起動保証）
				// このフォールバック経路には破棄すべき CTS は存在しない:
				// ローカル変数 cts は本チェック通過後（下段）に初めて new されるため、init 失敗経路では
				// 未生成・未使用であり cts.Dispose() が不要。従ってこの経路で CTS リークは発生しない。
				// B-1 方針（旧 CTS の Dispose は省略し GC に委譲）とも整合（孤立 CTS を一切生成しない）。
				// 降格はサーバー全体の状態変化のため、全クライアントへ通知して
				// WCS 側クライアントも WebP 描画経路へ同期させる（黒画面の放置防止）
				Log(2, "[RemoteController] WebCodecs 初期化失敗のため WebP にフォールバックします（クライアントへ通知）");
				await BroadcastControlToAllSessionsAsync("{\"type\":\"capture_mode_fallback\",\"to\":\"webp\",\"reason\":\"webcodecs_init_failed\"}");
				await SetCaptureModeAsync(CaptureMode.WebP);
				return;
			}
			var cts = new CancellationTokenSource();
			lock (_modeLock)
			{
				// 二重起動防止（ダブルチェック）: 事前チェックと初期化の間に並行呼び出しが起動した場合は、
				// 作成した CTS を破棄して返却（force 初期化は冪等なため実行済みでも無害）
				var existing = _webCodecsCaptureTask;
				if (existing != null && !existing.IsCompleted)
				{
					cts.Dispose();
					Log(3, "[RemoteController] WebCodecs ループは並行起動されたため、CTS を破棄してスキップ");
					return;
				}
				_webCodecsCaptureCts = cts;
				_webCodecsCaptureTask = Task.Run(() => WebCodecsCaptureLoopAsync(cts.Token));
			}
			// ループ起動時点のセッション状態（手動指定 / 目標間隔）からエンコーダ FPS を確定する
			// （起動前の set_fps が無い新規 WCS セッションでも目標間隔相応の FPS で開始するため）
			RecalculateWebCodecsFps();
			// 生存性チェックによる起動
			Log(2, "[RemoteController] WebCodecs キャプチャループを生存性チェックにより起動しました");
		}

		//====================================================================================
		// 処理ブロック名 : EnsureWebpPipelineStarted
		// 概要           : 中央キャプチャループ（WebP）の起動保証（起動判定の単一実装）。
		//                  WebP コンシューマ（ClientMode=0 セッション or 活性中の HTTP ポーリング）が居て、
		//                  ループが不在（cts null/cancelled / task null/completed）の場合に起動する。
		//                  ダブルチェックロックパターンで二重起動を防止する。
		//------------------------------------------------------------------------------------
		// コンシューマ判定とループ起動を _captureStartLock 内で一貫して実行し、自己救済経路の
		// TOCTOU ギャップ（コンシューマ存在を確認 → 別セッション切断 → 即自終了）を回避する。
		// 全体モード _currentCaptureMode は参照しない（混合接続時でも WebP 経路を独立に起動するため）。
		// 戻り値: true = 起動した、false = 起動しなかった（既に稼働中 / コンシューマ不在）
		private bool EnsureWebpPipelineStarted(string reason)
		{
			lock (_captureStartLock)
			{
				if (!HasWebpConsumers()) return false;
				if (_centralCaptureCts != null && !_centralCaptureCts.IsCancellationRequested &&
					_centralCaptureTask != null && !_centralCaptureTask.IsCompleted)
				{
					return false; // 既に稼働中
				}
				// 旧 CTS の Dispose は省略し GC に委ねる（B-1 参照）。
				// Cancel 済み CTS は内部 callbacks がクリア済みで、ループは token ローカル変数（value type）を参照するため、
				// Dispose による ObjectDisposedException 競合を回避しつつメモリは GC で回収される。
				_centralCaptureCts = new CancellationTokenSource();
				// 再起動時は必ずフルキーフレームを要求
				_forceNextKeyFrame = true;
				_centralCaptureTask = Task.Run(() => CentralCaptureLoopAsync(_centralCaptureCts.Token));
				// 起動ログ（reason 付きで起動原因を追跡: ws_connect / mode_switch / fanout_self_heal）
				Log(2, $"[RemoteController] 中央キャプチャループ(WebP)を起動しました (reason: {reason})");
				return true;
			}
		}

		//====================================================================================
		// 処理ブロック名 : EnsureWcsPipelineStarted
		// 概要           : WCS パイプラインの起動保証（コンシューマ判定付き）。
		//                  WebCodecs セッション（ClientMode=1）が居てループが不在の場合に起動する。
		//                  二重起動防止・force 初期化は StartWebCodecsCaptureLoopAsync の内部ガードに委譲する。
		//------------------------------------------------------------------------------------
		private async Task EnsureWcsPipelineStarted(string reason)
		{
			// コンシューマ不在なら起動しない（ループ自己終了後の自己救済経路で起動し直しを防止）
			if (!HasWcsClientSessions())
			{
				Log(3, $"[RemoteController] WebCodecs セッションが不在のため WCS パイプラインを起動しません (reason: {reason})");
				return;
			}
			await StartWebCodecsCaptureLoopAsync();
		}

		//====================================================================================
		// 処理ブロック名 : SetEnginePacingAsync
		// 概要           : キャプチャエンジンの稼働パラメータを現在のセッション構成へ同期する。
		//                  WebP セッション不在時は toBlob を停止（intervalMs<0）、全セッション不在時は
		//                  ループ本体処理を停止（active=false）する。rAF の発火は維持するため、
		//                  再接続時に復帰待ち遅延が生じない。
		//------------------------------------------------------------------------------------
		// 呼び出し時期: 各パイプラインの起動／自己終了、set_fps 受信、ClientMode 変更時。
		// 待機不要の片方向操作のため、呼び出し側は fire-and-forget 可（失敗は本メソッド内でログ化）。
		private async Task SetEnginePacingAsync(string reason)
		{
			var kancolleFrame = _parent.GetKanColleFrame();
			if (kancolleFrame == null || !kancolleFrame.IsValid) return;

			bool hasWebp = HasWebpConsumers();
			bool hasWcs = HasWcsClientSessions();

			// intervalMs: WebP あり = 最高 FPS の最小間隔、WebP なし = -1（toBlob 停止・offCanvas は維持）
			int intervalMs = hasWebp ? GetMaxFpsIntervalMs() : -1;
			string activeExpr = (hasWebp || hasWcs) ? "true" : "false";

			string script = "(function(){if(window._kancolleCaptureEngine){" +
				"window._kancolleCaptureEngine.setActive(" + activeExpr + ");" +
				"window._kancolleCaptureEngine.setCaptureInterval(" + intervalMs + ");}})()";

			try
			{
				await kancolleFrame.EvaluateScriptAsync(script).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Log(3, $"[RemoteController] エンジン稼働設定に失敗 ({reason}): {ex.Message}");
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
					// displayState: 接続直後のクライアントへホスト側の表示状態悪化（ヘッドレス/RDP 切断等）を伝える
					string displayReason;
					var displayState = GetCurrentDisplayState(out displayReason);
					string displayStateName = displayState == DisplayState.Degraded ? "degraded" : "normal";
					var json = $"{{\"mode\":\"{_currentCaptureMode.ToString().ToLower()}\",\"bitrate\":{_webCodecsBitrate},\"fps\":{_webCodecsFps},\"displayState\":\"{displayStateName}\",\"displayReason\":\"{displayReason}\"}}";
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
		// IPC 最適化 - スクリプト事前構築
		//------------------------------------------------------------------------------------
		// （EvaluateScriptAsync 用スクリプトの事前構築：GC 負荷削減）

		// WebCodecs エンコーダ停止スクリプト
		private const string WebCodecsStopScript =
			"(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.stopEncoder();}})()";

		// WebCodecs エンコーダ初期化スクリプトテンプレート（固有トークン: __CODEC__ / __BITRATE__ / __FPS__）
		// string.Format で構築しない理由: string.Format は JS リテラルブレース（{codec:'...'} 等）を
		// フォーマット項目と解釈し FormatException（「入力文字列の形式が正しくありません」）を投げるため、
		// JS に到達せず WebCodecs モードが常に WebP にフォールバックしていた。
		// verbatim 文字列 + トークン置換（string.Replace）で構築するため、ブレースのエスケープが不要で検証しやすい。
		// EvaluateScriptAsPromiseAsync はスクリプトを (async function(){ <script> })() で
		// ラップし、Promise.resolve(...).then() で解決値を回収する。async function 内の式文は return しないと
		// undefined を解決するため、テンプレート先頭に return を付与し initEncoder() の Promise を回収する。
		private const string WebCodecsInitEncoderTemplate =
			@"return (function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.initEncoder({codec:'__CODEC__',width:1200,height:720,bitrate:__BITRATE__,framerate:__FPS__,avc:{format:'annexb'}});}})();";

		// WebCodecs エンコード投入＋チャンク回収スクリプトテンプレート（固有トークン: __IS_KEY_FRAME__）
		// encodeAndPull() は async（Promise 返却）のため EvaluateScriptAsync で解決値を回収する（旧 pull_encoded() と同じ）。
		// 返却値: { action:'reinit' } / { success:false, error } / ['none'] / ['encoded', [[base64,isKey,descBase64], …]]
		// 旧実装は encodeFrame() 用スクリプトと pull_encoded() の 2 往復だったが、1 評価に統合した。
		// canvas はエンジン内部で document.querySelector('canvas') により取得（start() / pull() と同一ソース）。
		// __IS_KEY_FRAME__ は true/false に置換（string.Replace）。
		private const string WebCodecsEncodeAndPullTemplate =
			"(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.encodeAndPull(__IS_KEY_FRAME__);}})();";

		private readonly object _modeLock = new object();

		private volatile bool _isWebCodecsInitialized = false;

		private volatile string _webCodecsCodec = "avc1.42001f"; // H.264 Baseline Level 3.1

		// WebCodecs 再初期化バックオフ（iframe 破棄直後・エンジン未注入時に毎フレーム再初期化しないためのスロットリング）
		private DateTime _lastWebCodecsReinitUtc = DateTime.MinValue;
		private const int WebCodecsReinitBackoffMs = 2000; // 再初期化試行間隔（2秒）
		// バックオフ状態の lock 保護（_lastWebCodecsReinitUtc の読み書き・比較更新を原子化する）
		private readonly object _webCodecsReinitLock = new object();

		// WebCodecs キャプチャループのキーフレーム間隔（2秒）
		private const int WebCodecsKeyFrameIntervalMs = 2000;

		// （EvaluateScriptAsync 用スクリプトの事前構築：GC 負荷削減）
		// 高頻度呼び出し（10FPS）の CaptureCanvasAsync で使用するスクリプトを
		// 定数として事前定義し、呼び出し毎の文字列生成によるGC負荷および FormatException を排除する。
		private const string CaptureScriptInit = "(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.pull(true);}})()";
		private const string CaptureScriptNormal = "(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.pull(false);}})()";
		// HTTP の cver 判定後 full 配信用（peek）: ダーティフラグを消費せず toDataURL も発火しない読み取り専用引き抜き
		private const string CaptureScriptPeek = "(function(){if(window._kancolleCaptureEngine){return window._kancolleCaptureEngine.peek();}})()";

		// コンテンツ版数（エンジンの encodeCount = lastWebp 更新回数）。pull/peek 応答の第3要素から単調増加で更新し、
		// /screen.webp の X-Content-Version 付与と cver >= 現在値 → 204 判定に使用（Volatile でスレッドセーフ）
		private long _contentVersion = 0;
	}
}
